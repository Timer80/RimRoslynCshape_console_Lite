// RoslynProbe —— stdio 控制台通道(-RoslynConsole 裸旗标,规格 2026-10-15)。
// 协议(stdout):">" 就绪/完成(唯一同步信号) | "> 行" 回显 | "..." 续行 |
// 结果/错误裸文本(null 照打) | "> bye" /quit 收尾。
// 线程契约:读线程阻塞 ReadLine(零轮询);求值经 Executor 入游戏线程;
// 结果写走 TCS 续体(线程池)。主线程永不碰 Console.In(实测 Peek 卡死 Update)。
// 协议 v2 起:分派内核与求值闭包抽至 SessionLine/SessionEval,与 PipeConsole
// 共用(行为等价;输入行回显由管道模式的调用方处理,终端模式本地回显不重影)。

using System;
using System.Text;
using System.Threading;
using RoslynProbe.Repl;
using Verse;

namespace RoslynProbe
{
    public static class ConsolePipe
    {
        private static Executor executor;
        private static ReplHost host;
        private static readonly StringBuilder pending = new StringBuilder();

        /// <summary>
        /// stdio 期望的输出编码：UTF-8、**无 BOM**。无 BOM 是关键 —— 协议首行是同步信号 <c>&gt;</c>，
        /// 若编码器带前导 EF BB BF，对端读到的首行就多出三个字节（v2 的 "&gt; 行" 回显与就绪信号全在 stdout）。
        /// </summary>
        public static readonly Encoding StdioOutputEncoding = new UTF8Encoding(false);

        public static void Start(Executor replExecutor, ReplHost replHost)
        {
            // T3 遗留（docs/boundary-suite/NOTE-stdio-noise.md 第 3 节）：游戏进程没设
            // Console.OutputEncoding ⇒ stdio v1 中文乱码。v1 是**给人用的终端**，故在此设为 UTF-8。
            // 赋值在 stdout 已关闭/被占用时会抛 —— 失败必须**响亮记录**，绝不静默吞。
            string encError = TryApplyStdioOutputEncoding(
                enc => Console.OutputEncoding = enc,
                () => Console.OutputEncoding);
            if (encError == null)
                Log.Message("[Roslyn] stdio 输出编码已设为 UTF-8（无 BOM）");
            else
                Log.Error("[Roslyn] stdio 输出编码设置失败，中文可能乱码: " + encError);

            executor = replExecutor;
            host = replHost;
            var t = new Thread(ReaderLoop)
            {
                IsBackground = true,
                Name = "RoslynProbe.ConsolePipe"
            };
            t.Start();
            Console.Out.WriteLine(">");   // 就绪信号
        }

        /// <summary>
        /// 应用 stdio 输出编码：把「赋值抛异常」与「赋值成功但宿主忽略」都收敛成一条可读错误
        /// （null = 成功）。setter / getter 注入只为离线单测（真调用点传 Console 的赋值与读取）——
        /// 本方法**绝不抛**，也**绝不**把失败说成成功。
        /// </summary>
        public static string TryApplyStdioOutputEncoding(Action<Encoding> setter, Func<Encoding> getter)
        {
            if (setter == null) return "内部错误：setter 为 null";
            try
            {
                setter(StdioOutputEncoding);
                if (getter == null) return null;            // 不要求复核时只报"赋值没抛"
                Encoding now = getter();
                if (now == null || now.CodePage != Encoding.UTF8.CodePage)
                    return "赋值后 Console.OutputEncoding 仍不是 UTF-8（实际 "
                        + (now == null ? "<null>" : now.WebName + " / CP" + now.CodePage)
                        + "）—— 该宿主忽略了赋值，中文仍可能乱码";
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static void ReaderLoop()
        {
            try
            {
                string line;
                while ((line = Console.In.ReadLine()) != null)   // null = EOF:静默收摊(决策#7)
                    OnLine(line);
            }
            catch (Exception ex)
            {
                // stdin 不是管道(如桌面直启误带旗标)——响亮落日志,不影响游戏本体
                Log.Error("[Roslyn] stdio 读线程退出: " + ex);
            }
        }

        private static void OnLine(string line)
        {
            string trimmed = (line ?? "").Trim();
            if (trimmed.Length == 0) return;

            if (Console.IsInputRedirected)
                Console.Out.WriteLine("> " + trimmed);   // 管道模式(agent):mod 回显保证转录完整
            // 终端模式(人直接拉起):终端驱动已本地回显,再打一遍成重影

            SessionAction action = SessionLine.Handle(line, pending, executor,
                code => SessionEval.EnqueueEval(executor, host, code),
                input => StdioSlashDispatch(executor, host, input),
                trimmed => SlashCommands.IsCommand(trimmed, SlashCommands.ChannelStdio));

            foreach (string l in action.Lines ?? new string[0])
                Console.Out.WriteLine(l);

            if (action.Kind == "quit")
            {
                executor.Enqueue(() =>
                {
                    // I-6a 报告 §5：handler 异常会被 Executor.Tick 吞掉且**无日志** ⇒
                    // 「bye 收到但进程不退」将毫无痕迹。显式记一行；正常路径行为不变。
                    try { Root.Shutdown(); }
                    catch (Exception ex) { Log.Error("[Roslyn] /quit 退出失败: " + ex); }
                    return ReplResult.Success(null);
                }, 5000);
            }
        }

        /// <summary>
        /// 本次派发的读线程事实源（默认 = 游戏引擎的权威判定 <c>UnityData.IsInMainThread</c>）。
        /// 可注入只为**离线测试**：必须能确定性地钉住「壳把这个值原样送进 ctx.IsGameThread」
        /// （F4/MUT-F：改成常量 true ⇒ 读线程上会裸调 Unity/Verse）。离线宿主里
        /// <c>UnityData.IsInMainThread</c> 取值不可控（它的 mainThreadId 只在游戏初始化时写），故留这个口。
        /// 生产路径零改动。
        /// </summary>
        internal static Func<bool> ReadIsGameThread = () => UnityData.IsInMainThread;

        /// <summary>测试隔离专用：把读线程事实源复位成生产默认（UnityData.IsInMainThread）。</summary>
        internal static void ResetIsGameThreadForTests()
        {
            ReadIsGameThread = () => UnityData.IsInMainThread;
        }

        /// <summary>
        /// Slash 派发钩子:结果行落 ConsoleLog(经队列 marshal),返回派发结果。
        /// **internal 而非 private**：SlashHarness 把 Source 与用例编进同一程序集，故用例能走这条**真实入口**
        /// （F4/MUT-F 的接缝）。
        /// </summary>
        internal static SlashResult StdioSlashDispatch(Executor executor, ReplHost host, string input)
        {
            SlashResult slash;
            // 事实位(S11-3):通道 stdio;读线程不是游戏线程 —— 经 ReadIsGameThread 取值
            // (默认就是 UnityData.IsInMainThread:纯静态量,不碰 Unity 对象;拿不准的语义仍是 false
            // ⇒ provider 会走 marshal 或明确拒绝)。
            // S11-4:入口换成 SlashJson(剥离 --json 尾旗标 → 带旗标则把 Lines 换成 Data 的单行 JSON)。
            // 不带旗标时它交给内核的字符串与 input 逐字相同 ⇒ 下面的落账与 OnLine 的逐行输出行为不变。
            if (!SlashJson.TryDispatch(input, host, executor, SlashCommands.ChannelStdio,
                    ReadIsGameThread(), out slash))
                return null;
            foreach (string l in slash.Lines)
            {
                string captured = l;
                executor.Enqueue(() => { ConsoleLog.SystemNote("! " + captured); return ReplResult.Success(null); }, 5000);
            }
            return slash;
        }
    }
}