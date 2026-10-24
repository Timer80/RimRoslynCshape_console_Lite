// RoslynProbe —— **接线层**回归（对照最终宽范围审查 F4 的四个覆盖缺口）。
//
// 审查用 6 个变异副本证明接线层「删掉实现 ⇒ 0 条测试变红」。本文件补上承诺 —— 走**真的接线代码**
// （BootstrapWiring.* / 壳的 internal 派发入口 / SlashJson.TryStrip），使下面四条变异各自**恰好红**：
//   A) 删 BootstrapWiring.InstallChannelFacts 里的 InstallGui/InstallPipe ⇒ ChannelFactsWiringCases 红；
//   B) 删 BootstrapWiring.InstallSlashKernel 里的 SlashCapabilities.Install(caps) ⇒ CapabilityWiringCases 红；
//   F) 把壳里 ReadIsGameThread() 换成常量 true ⇒ ShellThreadFactCases 红；
//   E) 把 SlashJson 的分隔空白从 char.IsWhiteSpace 收窄成 ' ' ⇒ JsonTokenWhitespaceCases 红。
//
// 约定同 tests/SlashHarness/SlashRegistryTests.cs：零 xunit，public static int Run() 返回失败数，
// 由 SlashTestHarness.RunAll() 反射发现；用例必须幂等（§8.4：同进程第二遍 RunAll 也绿）。

using System;
using System.Collections.Generic;
using RoslynProbe;

namespace RoslynProbe.Tests
{
    /// <summary>接线层（启动期事实位/能力注入/壳事实源/--json token）的断言套件。</summary>
    public static class WiringCoverageTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";
        private static int checks;

        public static int Run()
        {
            Failures.Clear();
            checks = 0;
            currentCase = "(none)";

            Case("A 通道事实：管道决策 + 旗标诊断 + 两个事实位确实被装（删 InstallX ⇒ 本用例红）", ChannelFactsWiringCases);
            Case("B 姿势 B 注入：8 个委托全非 null 且身份正确（删 Install ⇒ 本用例红）", CapabilityWiringCases);
            Case("F 读线程事实位：壳把 ReadIsGameThread() 原样送进 ctx（换常量 ⇒ 本用例红）", ShellThreadFactCases);
            Case("E --json 分隔空白：TAB/全角空格/NBSP 都算 token 分隔（收窄成 ' ' ⇒ 本用例红）", JsonTokenWhitespaceCases);

            // 收尾：还原所有被本类动过的进程级状态（§8.4 幂等）。
            ChannelFacts.ResetForTests();
            SlashCapabilities.ResetForTests();
            PipeConsole.ResetIsGameThreadForTests();
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();

            Console.WriteLine("WiringCoverageTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- A：通道决策 + 通道事实 ----------------

        private static void ChannelFactsWiringCases()
        {
            // 管道决策（Bootstrap 在真机上用的就是这个纯函数）
            Check(BootstrapWiring.WantsPipe(true, "sess"), "带名 ⇒ 开管道");
            Check(!BootstrapWiring.WantsPipe(true, ""), "空名 ⇒ 不开管道");
            Check(!BootstrapWiring.WantsPipe(false, null), "裸旗标 ⇒ 不开管道");

            // 旗标诊断（stdio 通道删除后：裸旗标与空值都必须告警，不静默）
            Check(BootstrapWiring.BareFlagWarning(true, false), "裸 -RoslynConsole ⇒ 必须告警");
            Check(!BootstrapWiring.BareFlagWarning(false, true), "带值旗标 ⇒ 不该报「裸旗标」");
            Check(!BootstrapWiring.BareFlagWarning(false, false), "没传旗标 ⇒ 不告警");
            Check(BootstrapWiring.EmptyNameWarning(true, ""), "-RoslynConsole= 空名 ⇒ 必须告警");
            Check(BootstrapWiring.EmptyNameWarning(true, null), "-RoslynConsole= 且值为 null ⇒ 也必须告警");
            Check(!BootstrapWiring.EmptyNameWarning(true, "sess"), "有名字 ⇒ 不告警");

            // 事实位：带名管道 ⇒ 记**可连接全名**（F2），且 GUI 恒装
            ChannelFacts.ResetForTests();
            BootstrapWiring.InstallChannelFacts(true, "sess");
            Check(ChannelFacts.Installed, "装事实位后 Installed 应为真");
            ChannelFacts.Snapshot s = ChannelFacts.Current;
            Check(s != null && s.Gui, "GUI 事实必须被装（ConsoleLayer.Wire 在上游完成）");
            Check(s != null && s.Pipe, "带名管道 ⇒ pipe 真（实际 gui/pipe = "
                + (s == null ? "<null>" : s.Gui + "/" + s.Pipe) + "）");
            Check(s != null && s.PipeName == PipeConsole.FullPipeName("sess"),
                "事实位应记 PipeConsole 拼出的全名，实际 " + Show(s == null ? null : s.PipeName));
            Check(s != null && s.PipeName == "roslyn-console-sess",
                "全名 = roslyn-console-<短名>（消费方按它就能连上），实际 " + Show(s == null ? null : s.PipeName));

            // 消费面：/status 的 pipe reason 必须指向可连接名
            bool agent;
            List<Dictionary<string, object>> matrix = StatusCommand.ChannelMatrix(
                StatusCommand.ChannelFlagsFromFacts(), out agent);
            Check(agent, "pipe 开 ⇒ agentChannelOpen 为真");
            Check(matrix.Count == 2 && matrix[1]["channel"] != null && (string)matrix[1]["channel"] == "pipe",
                "矩阵第 2 项应是 pipe（锚点：避免扫错项）");
            Check(((string)matrix[1]["reason"]).IndexOf("roslyn-console-sess", StringComparison.Ordinal) >= 0,
                "pipe 的 reason 应给出可连接全名，实际 " + Show((string)matrix[1]["reason"]));

            // 零新路径：只有 GUI
            ChannelFacts.ResetForTests();
            BootstrapWiring.InstallChannelFacts(false, null);
            s = ChannelFacts.Current;
            Check(s != null && s.Gui && !s.Pipe, "零新路径 ⇒ 只有 GUI（实际 "
                + (s == null ? "<null>" : s.Gui + "/" + s.Pipe) + "）");
            Check(s != null && s.PipeName == "", "零新路径不应带管道名");

            ChannelFacts.ResetForTests();
        }

        // ---------------- B：8 个姿势 B 委托 ----------------

        private static void CapabilityWiringCases()
        {
            SlashCapabilities.ResetForTests();
            Check(SlashCapabilities.Registered().Count == 0, "前置：先复位模板（否则下面的断言可能是残留）");

            BootstrapWiring.InstallSlashKernel();
            SlashContext c = SlashCapabilities.NewContext(null, null, "wiring-test", false);

            // 身份断言（不是只数数量）：把任一 Provide 写错/漏掉都会红。
            Check(Delegate.Equals(c.Status, (Func<string, SlashContext, SlashResult>)StatusCommand.Provide),
                "Status 必须正是 StatusCommand.Provide");
            Check(Delegate.Equals(c.Log, (Func<string, SlashContext, SlashResult>)LogCommand.Provide),
                "Log 必须正是 LogCommand.Provide");
            Check(Delegate.Equals(c.Dev, (Func<string, SlashContext, SlashResult>)DevCommand.Provide),
                "Dev 必须正是 DevCommand.Provide");
            Check(Delegate.Equals(c.DebugAction, (Func<string, SlashContext, SlashResult>)DebugActionCommand.Provide),
                "DebugAction 必须正是 DebugActionCommand.Provide");
            Check(Delegate.Equals(c.Hook, (Func<string, SlashContext, SlashResult>)HookCommand.Provide),
                "Hook 必须正是 HookCommand.Provide");
            Check(Delegate.Equals(c.Prof, (Func<string, SlashContext, SlashResult>)ProfCommand.Provide),
                "Prof 必须正是 ProfCommand.Provide");
            Check(Delegate.Equals(c.UsingAdd, (Func<string, SlashContext, SlashResult>)UsingAddCommand.Provide),
                "UsingAdd 必须正是 UsingAddCommand.Provide");
            Check(Delegate.Equals(c.UiOpen, (Func<string, SlashContext, SlashResult>)UiCommand.Provide),
                "UiOpen 必须正是 UiCommand.Provide");

            Check(c.Status != null && c.Log != null && c.Dev != null && c.DebugAction != null
                && c.Hook != null && c.Prof != null && c.UsingAdd != null && c.UiOpen != null,
                "8 个委托必须全非 null（漏一个 ⇒ 该指令在真机上静默降级成「能力未注册」）");
            List<string> caps = c.RegisteredCapabilities();
            Check(caps.Count == 8, "应报出 8 个能力名，实际 " + caps.Count + "（" + string.Join(",", caps.ToArray()) + "）");

            // 每次派发必须是**独立**上下文（模板零共享可变状态），且事实位来自壳层
            SlashContext c2 = SlashCapabilities.NewContext(null, null, "pipe", true);
            Check(!object.ReferenceEquals(c, c2), "NewContext 每次应给新上下文");
            Check(c.Channel == "wiring-test" && c2.Channel == "pipe" && !c.IsGameThread && c2.IsGameThread,
                "事实位应由壳层参数决定（模板不携带事实位）");
            Check(c.Host == null && c.Executor == null, "壳层没给的 Host/Executor 不得被模板带过来");

            SlashCapabilities.ResetForTests();
            Check(SlashCapabilities.Registered().Count == 0, "复位后应回到未注入");
        }

        // ---------------- F：壳的读线程事实源 ----------------

        private static void ShellThreadFactCases()
        {
            SlashRegistry.ResetForTests();
            string seenChannel = null;
            bool seenThread = false;
            int calls = 0;
            SlashRegistry.Register(new SlashEntry
            {
                Verb = "t-fact",
                Kind = "t-fact",
                Handler = delegate (string a, SlashContext c)
                {
                    calls++;
                    seenChannel = c.Channel;
                    seenThread = c.IsGameThread;
                    return SlashResults.Ok("t-fact");   // 无行 ⇒ 壳的 ConsoleLog 落账循环不会用到 Executor
                }
            });

            // ① pipe 壳：事实源给 false ⇒ ctx.IsGameThread 必须 false
            PipeConsole.ReadIsGameThread = delegate { return false; };
            calls = 0; seenThread = true; seenChannel = null;
            SlashResult r3 = PipeConsole.PipeSlashDispatch(null, null, "/t-fact");
            Check(r3 != null && calls == 1 && !seenThread, "pipe 壳必须把事实源的值送进 ctx.IsGameThread");
            Check(seenChannel == SlashCommands.ChannelPipe, "pipe 壳的通道应为 pipe，实际 " + Show(seenChannel));

            // ② 反向对照：源给 true ⇒ ctx 也必须 true（证明「原样透传」，不是写死 false）
            PipeConsole.ReadIsGameThread = delegate { return true; };
            calls = 0; seenThread = false;
            PipeConsole.PipeSlashDispatch(null, null, "/t-fact");
            Check(calls == 1 && seenThread, "pipe 壳的事实位也应原样透传（true 情形）");

            PipeConsole.ResetIsGameThreadForTests();
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();
        }

        // ---------------- E：--json 的 token 分隔空白 ----------------

        private static void JsonTokenWhitespaceCases()
        {
            CheckJsonStrip("/status --json", "/status");
            CheckJsonStrip("/status\t--json", "/status");
            CheckJsonStrip("/status\u3000--json", "/status");
            CheckJsonStrip("/status\u00A0--json", "/status");

            string kept;
            Check(!SlashJson.TryStrip("/status\t--jsonx", out kept) && kept == "/status\t--jsonx",
                "非独立 token（--jsonx）不得被剥离，且无旗标时逐字原样，实际 " + Show(kept));
            Check(!SlashJson.TryStrip("/status\u3000--JSON", out kept) && kept == "/status\u3000--JSON",
                "旗标区分大小写，且不剥离时逐字原样，实际 " + Show(kept));
        }

        private static void CheckJsonStrip(string input, string expect)
        {
            string stripped;
            bool hit = SlashJson.TryStrip(input, out stripped);
            Check(hit, "应识别独立 token --json（分隔空白 = 任意空白），输入 " + Show(input));
            Check(stripped == expect, "剥离后应为 " + Show(expect) + "，实际 " + Show(stripped));
        }

        // ---------------- 断言 ----------------

        private static void Case(string name, Action body)
        {
            currentCase = name;
            try { body(); }
            catch (Exception ex)
            {
                checks++;
                Failures.Add("[" + name + "] 用例本身抛出: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Check(bool condition, string message)
        {
            checks++;
            if (!condition) Failures.Add("[" + currentCase + "] " + message);
        }

        private static string Show(string s) { return s == null ? "<null>" : "\"" + s + "\""; }
    }
}
