// RoslynProbe —— 命名管道控制台(协议 v2,-RoslynConsole=名,规格 §7)。
//
// 形态:accept 阻塞线程 + NamedPipeServerStream 单实例。连接=会话开始,
// 断开=会话结束,重连=新会话;求值状态(usings/directives/变量)在
// ReplHost/RoslynEngine 对象里,与连接无关,天然跨连接延续。单客户端同一
// 时间:第二连接在内核等待队列挂起,首连接断开后被本 accept 接入。
// 协议行:">" / "> 行" / "..." / 裸结果 / "> bye"。
// 回显:读到非空行先打 "> " + 行(trimmed 口径,规格 §7.5:管道对端是
// agent,mod 回显保证转录完整)。
// Slash 分支与完整提交的判定顺序全部复用 SessionLine/SessionEval,
// 本文件只做 IO 层(StreamReader/StreamWriter over NamedPipeServerStream)。
//
// quit 路径:"> bye" 后入游戏线程 Root.Shutdown()(与 v1 同款),会话循环
// return 后回到 accept 等下一轮(进程即将退出,线程随进程结束)。

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using RoslynProbe.Repl;
using Verse;

namespace RoslynProbe
{
    public static class PipeConsole
    {
        /// <summary>命名管道的名字前缀（**唯一的拼接点**）—— 可连接真名 = 前缀 + 命令行短名。</summary>
        public const string PipeNamePrefix = "roslyn-console-";

        /// <summary>
        /// 由命令行短名拼出**可连接的真实管道全名**。<see cref="Start"/> 与通道事实位
        /// （<c>ChannelFacts.InstallPipe</c>，经 Bootstrap）共用同一拼接 —— 消费方按 /status 拿到的名字
        /// 就是能连上的名字（F2）。
        /// </summary>
        public static string FullPipeName(string shortName)
        {
            return PipeNamePrefix + (shortName ?? "");
        }

        /// <summary>
        /// 起 accept 线程后**立即返回**（bind 不在这里）：真正的 bind 在 <see cref="AcceptLoop"/>。
        /// 因此「Start 返回」≠「管道已可连接」—— 同名冲突时后到者绑不上并每 ~500ms 重试（已接受的偏离）。
        /// </summary>
        public static void Start(Executor executor, ReplHost host, string pipeName)
        {
            string fullPipeName = FullPipeName(pipeName);
            var t = new Thread(() => AcceptLoop(executor, host, fullPipeName))
            {
                IsBackground = true,
                Name = "RoslynProbe.PipeConsole"
            };
            t.Start();
            Log.Message("[Roslyn] PipeConsole 已启动: " + fullPipeName);
        }

        private static void AcceptLoop(Executor executor, ReplHost host, string fullPipeName)
        {
            while (true)
            {
                NamedPipeServerStream server = null;
                bool sessionStarted = false;
                try
                {
                    server = new NamedPipeServerStream(fullPipeName, PipeDirection.InOut,
                        maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnection();                       // 阻塞;断开后回到这里
                    sessionStarted = true;
                    ServeSession(server, executor, host);             // 单客户端会话(内部逐行)
                }
                catch (Exception ex)
                {
                    Log.Error("[Roslyn] 管道会话异常(继续 accept): " + ex);
                    // 退避只针对建连阶段(管道名被占等瞬时故障,防错误日志风暴);
                    // 客户端会话中途 IOException 断开是正常序列,不拖慢重连。
                    if (!sessionStarted) Thread.Sleep(500);
                }
                finally { try { server?.Dispose(); } catch { } }
            }
        }

        private static void ServeSession(NamedPipeServerStream pipe, Executor executor, ReplHost host)
        {
            // 状态(pending 累积)绑定会话:断开即丢弃,重连是新会话(规格 §7.3)。
            var pending = new StringBuilder();
            using (var reader = new StreamReader(pipe))
            using (var writer = new StreamWriter(pipe) { AutoFlush = true })
            {
                writer.WriteLine(">");   // 就绪信号(连接建立后首行)

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    // 回显先行(规格 §7.5):管道对端是 agent,mod 回显保证转录完整;
                    // 与 v1 同为 trimmed 口径("> " + 行.Trim()),空行不回显(与 v1 忽略空行一致)。
                    string echoTrimmed = (line ?? "").Trim();
                    if (echoTrimmed.Length > 0)
                        writer.WriteLine("> " + echoTrimmed);

                    SessionAction action = SessionLine.Handle(line, pending, executor,
                        code => SessionEval.EnqueueEval(executor, host, code),
                        input => PipeSlashDispatch(executor, host, input),
                        trimmed => SlashCommands.IsCommand(trimmed, SlashCommands.ChannelPipe));

                    if (action.Kind == "consume") continue;

                    foreach (string l in action.Lines ?? new string[0])
                        writer.WriteLine(l);

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
                        return;   // 结束会话循环;进程即将退出
                    }
                }
                // ReadLine == null:客户端断开,静默结束会话(规格 §7.3),回到 accept。
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
        /// （F4/MUT-F 的接缝；槽外的行为仍是壳的内部实现）。
        /// </summary>
        internal static SlashResult PipeSlashDispatch(Executor executor, ReplHost host, string input)
        {
            SlashResult slash;
            // 事实位(S11-3):通道 pipe;会话读线程不是游戏线程 —— 经 ReadIsGameThread 取值
            // (默认就是 UnityData.IsInMainThread:纯静态量,不碰 Unity 对象;拿不准的语义仍是 false)。
            // S11-4:入口换成 SlashJson(剥离 --json 尾旗标 → 带旗标则把 Lines 换成 Data 的单行 JSON);
            // 不带旗标时交给内核的字符串与 input 逐字相同 ⇒ 会话循环的逐行输出行为不变。
            if (!SlashJson.TryDispatch(input, host, executor, SlashCommands.ChannelPipe,
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