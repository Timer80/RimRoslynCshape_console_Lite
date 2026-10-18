using System;
using UnityEngine;
using Verse;
using RoslynProbe.Repl;
using HarmonyLib;

namespace RoslynProbe
{
    /// <summary>
    /// 引导层（[StaticConstructorOnStartup] 静态构造 + 常驻 GameObject）。A resident,
    /// DontDestroyOnLoad GameObject hosts the Executor (main-thread drain) and the
    /// ConsoleLayer. No map, no F7: works from the main menu. A Harmony Postfix adds a
    /// toggle icon to the vanilla dev toolbar.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                GameObject go = new GameObject("RoslynProbeRoot");
                UnityEngine.Object.DontDestroyOnLoad(go);
                var boot = go.AddComponent<BootstrapBehaviour>();
                boot.Init();
            }
            catch (Exception ex)
            {
                Log.Error("[Roslyn] Bootstrap 静态构造失败: " + ex);
            }
        }
    }

    public class BootstrapBehaviour : MonoBehaviour
    {
        private Executor executor;
        private ReplHost host;
        private bool started;

        public void Init()
        {
            if (started) return;
            started = true;

            // S11-3：表驱动内核的登记与姿势 B 注入必须在**游戏线程**完成 —— RegisterAll 会跑
            // ThreadsCommand.Register（捕获注册期线程为主线程）与 DebugActionCommand 的 mainThreadId 依赖；
            // 本方法由 [StaticConstructorOnStartup] 的静态构造在游戏线程上调用。
            // 唯一出口 = BootstrapWiring.InstallSlashKernel（离线用例也走它，见该类文档）。
            BootstrapWiring.InstallSlashKernel();

            executor = new Executor();
            host = new ReplHost();
            gameObject.AddComponent<ConsoleLayer>();
            ConsoleLayer.Wire(host, executor);

            // 控制台通道分派(规格 2026-10-15 §7.2,协议 v2):命中旗标才存在,零旗标零新路径。
            // GenCommandLine 事实(反编译核实):TryGetCommandLineArg 带 '=' 命中(空 value 也 true);
            // CommandLineArgPassed 裸/带杠、大小写不敏感均命中。因此 "RoslynConsole="(空值)会命中
            // TryGetCommandLineArg 却不能当 v2 管道名用——选:空值视为未携带 → 走 v1 终端形态,日志一行。
            // 决策是纯逻辑（BootstrapWiring.DecideChannel），四类情形各有离线用例。
            string pipeName;
            bool argHasValue = GenCommandLine.TryGetCommandLineArg("RoslynConsole", out pipeName);
            bool argPassed = GenCommandLine.CommandLineArgPassed("RoslynConsole");
            int channel = BootstrapWiring.DecideChannel(argHasValue, pipeName, argPassed);

            if (channel == BootstrapWiring.ChannelPipe)
                PipeConsole.Start(executor, host, pipeName);                      // v2:agent(命名管道)
            else if (channel == BootstrapWiring.ChannelStdio)
            {
                if (argHasValue && pipeName != null && pipeName.Length == 0)
                    Log.Warning("[Roslyn] -RoslynConsole= 空管道名,视为未携带,回退 v1 终端形态");
                ConsolePipe.Start(executor, host);                                // v1:人(终端形态)
            }
            // 都无:零新路径

            // 通道事实的**唯一安装出口**（同一个出口也供离线用例调用）—— 必须在各 Start **之后**调用。
            // F3 口径：事实位记的是「Start 已被调用（线程已起）」，**不是**「bind/可用性已确认」：
            // PipeConsole.Start 只起 accept 线程就返回，真正的 bind 在 AcceptLoop 里，失败被吞并每 ~500ms 重试
            // （同名冲突时后到者永远绑不上，而事实位仍报 pipe=开 —— 已接受的偏离，规避 = 用不同管道名）。
            BootstrapWiring.InstallChannelFacts(channel, pipeName);

            // Append our icon to the vanilla dev toolbar. DrawButtons is private and so are
            // widgetRow / widgetRowFinalX, so the patch reaches them via AccessTools; if the
            // Harmony mod is absent the whole toolbar button simply does not appear and the
            // /ui open 指令 remains the way in.
            try
            {
                // owner 统一 roslynprobe.* 前缀（S11-3）：工具栏按钮单独一个 owner，
                // 与 /debug-action 的鼠标代理（roslynprobe.debug-action.mouse）等互不误卸。
                // HookCommand.SharedOwner 必须与本字面量一致（/hook off 命中它时给响亮警告）。
                var harmony = new Harmony("roslynprobe.toolbar");
                harmony.PatchAll(typeof(Bootstrap).Assembly);
                Log.Message("[Roslyn] Harmony patch 已应用（调试工具栏按钮，owner roslynprobe.toolbar）");
            }
            catch (Exception ex)
            {
                Log.Error("[Roslyn] Harmony patch 应用失败: " + ex);
            }

            Log.Message("[Roslyn] RoslynProbe 引导完成");
        }

        private void Update()
        {
            if (executor != null)
                executor.Tick(4);
        }
    }

    /// <summary>
    /// 启动期接线的**唯一出口**（同时也是**离线测试缝**）。
    /// <para>为什么单独一个类而不留在 <see cref="BootstrapBehaviour.Init"/> 里：Init 需要真 GameObject/Unity，
    /// 离线跑不了 —— 于是「通道决策 / 通道事实安装 / 姿势 B 注入」这三段本来没有任何回归承诺
    /// （删掉仍两遍全绿）。把这段纯逻辑搬到这里，用例就能走**真接线代码**并断言结果。</para>
    /// <para>本类零 Unity/Verse 依赖（只碰 ChannelFacts / SlashCapabilities / PipeConsole 这些已有的
    /// 零 Verse 类型），故 SlashHarness 可以直接调用。</para>
    /// </summary>
    public static class BootstrapWiring
    {
        /// <summary>通道决策：零新路径（既没有 -RoslynConsole=名，也没有裸 -RoslynConsole）。</summary>
        public const int ChannelNone = 0;
        /// <summary>通道决策：命名管道 v2。</summary>
        public const int ChannelPipe = 1;
        /// <summary>通道决策：stdio v1 终端形态。</summary>
        public const int ChannelStdio = 2;

        /// <summary>
        /// 命令行旗标 → 通道决策（纯逻辑，可离线单测）。四类情形：
        /// <list type="bullet">
        /// <item><description>带名（TryGetCommandLineArg 命中且有非空值）⇒ <see cref="ChannelPipe"/>；</description></item>
        /// <item><description>空名（命中但值为空串）⇒ <see cref="ChannelStdio"/>（视为未携带，Init 另记一行 Warning）；</description></item>
        /// <item><description>裸旗标（CommandLineArgPassed 命中）⇒ <see cref="ChannelStdio"/>；</description></item>
        /// <item><description>两者都没给 ⇒ <see cref="ChannelNone"/>（零新路径）。</description></item>
        /// </list>
        /// </summary>
        public static int DecideChannel(bool argHasValue, string pipeName, bool argPassed)
        {
            if (argHasValue && !string.IsNullOrEmpty(pipeName)) return ChannelPipe;
            if (argPassed) return ChannelStdio;
            return ChannelNone;
        }

        /// <summary>
        /// 姿势 B 的 8 个 provider 注入 + 表登记（**唯一注入点**）。必须在**游戏线程**调用
        /// （由 [StaticConstructorOnStartup] 的静态构造经 Init 调用）。注入的是模板上下文，
        /// 每次派发由 <see cref="SlashCapabilities.NewContext"/> 复制出带事实位的真上下文。
        /// </summary>
        public static void InstallSlashKernel()
        {
            SlashRegistry.RegisterAll();
            var caps = new SlashContext();
            caps.Status = StatusCommand.Provide;
            caps.Log = LogCommand.Provide;
            caps.Dev = DevCommand.Provide;
            caps.DebugAction = DebugActionCommand.Provide;
            caps.Hook = HookCommand.Provide;
            caps.Prof = ProfCommand.Provide;
            caps.UsingAdd = UsingAddCommand.Provide;
            caps.UiOpen = UiCommand.Provide;
            SlashCapabilities.Install(caps);
        }

        /// <summary>
        /// 通道事实位的**唯一安装出口**。调用方必须已调用对应通道的 Start —— 本方法只如实记
        /// 「Start 已被调用」（GUI 恒装：ConsoleLayer.Wire 在上游完成），**不**代表 bind/可用性已确认（F3）。
        /// </summary>
        /// <param name="channel"><see cref="DecideChannel"/> 的返回值。</param>
        /// <param name="shortPipeName">命令行短名（仅管道通道用；事实位存的是
        /// <see cref="PipeConsole.FullPipeName"/> 拼出的可连接全名）。</param>
        public static void InstallChannelFacts(int channel, string shortPipeName)
        {
            ChannelFacts.InstallGui();
            if (channel == ChannelPipe) ChannelFacts.InstallPipe(PipeConsole.FullPipeName(shortPipeName));
            else if (channel == ChannelStdio) ChannelFacts.InstallStdio();
        }
    }
}