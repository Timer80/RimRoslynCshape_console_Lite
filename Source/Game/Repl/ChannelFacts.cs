// RoslynProbe —— 「本实例实际启动了哪些通道」的事实位（S11-3 补丁，替代 /status 的命令行重放）。
//
// 为什么要有它：/status 的通道信息原本靠**重放 GenCommandLine 旗标**猜。那是「可证伪字段靠猜」的
// 老问题 —— 若某条通道是**程序化**启动的（不靠旗标），重放会报「没开」；反之亦然。现在 Bootstrap
// 在**实际启动每条通道**的地方写入事实，/status 只读事实。
//
// 三态纪律（docs/slash-fleet-rules.md §2「判不出就不下结论」）：**未安装 = 未知**。
// Snapshot 为 null 表示事实位还没被安装（离线单测、或 Bootstrap 尚未跑到那段）——
// 此时 /status 的通道字段报 null/未知，**绝不默认 false**（那会把「不知道」说成「没开」）。
//
// 线程：Bootstrap 在游戏线程的启动期写（各 Install 方法），会话线程读（Current）。
// 写的是**不可变快照**的引用替换（copy-on-write + volatile），读到的永远是一个完整快照，
// 不需要读侧加锁。本文件零 Unity/Verse 依赖（只读事实，不碰游戏对象）。

using System;

namespace RoslynProbe
{
    /// <summary>
    /// 本实例的通道事实位。Bootstrap 每真正启动一条通道就 Install 一次；
    /// <see cref="Current"/> 为 null = 未安装 = 未知（不是「都没开」）。
    /// </summary>
    public static class ChannelFacts
    {
        /// <summary>不可变通道快照。字段全 readonly，一经发布不再变。</summary>
        public sealed class Snapshot
        {
            /// <summary>GUI 控制台（ConsoleLayer.Wire 装配）。</summary>
            public readonly bool Gui;
            /// <summary>stdio v1 终端形态（ConsolePipe.Start）。</summary>
            public readonly bool Stdio;
            /// <summary>命名管道 v2（PipeConsole.Start 已被调用）。</summary>
            public readonly bool Pipe;
            /// <summary>
            /// 命名管道的**可连接全名**（<c>roslyn-console-&lt;命令行短名&gt;</c>，未开则为空串）。
            /// 不是命令行短名 —— 消费方按 /status 拿到的名字必须能直接连上（F2）。
            /// </summary>
            public readonly string PipeName;

            public Snapshot(bool gui, bool stdio, bool pipe, string pipeName)
            {
                Gui = gui;
                Stdio = stdio;
                Pipe = pipe;
                PipeName = pipeName ?? "";
            }
        }

        private static readonly object Gate = new object();
        private static volatile Snapshot current;

        /// <summary>事实位是否已安装（false ⇒ 一切通道皆未知）。</summary>
        public static bool Installed { get { return current != null; } }

        /// <summary>当前快照；<c>null</c> = 未安装（未知）。只读引用，调用方不得修改（快照本就不可变）。</summary>
        public static Snapshot Current { get { return current; } }

        /// <summary>
        /// GUI 控制台已装配（Bootstrap 在 <c>ConsoleLayer.Wire</c> 之后调用）。
        /// 口径（F3）：事实位记的是「这一步已被调用」，不是「某件事已确认成功」。
        /// </summary>
        public static void InstallGui()
        {
            Update(delegate(Snapshot s) { return new Snapshot(true, s.Stdio, s.Pipe, s.PipeName); });
        }

        /// <summary>
        /// stdio v1 的 <c>ConsolePipe.Start</c> **已被调用**（Bootstrap 在它返回后调用本方法）。
        /// 口径（F3）：Start 只起读线程就返回 —— 事实位记的是「线程已起」，不是「stdin 可用已确认」。
        /// </summary>
        public static void InstallStdio()
        {
            Update(delegate(Snapshot s) { return new Snapshot(s.Gui, true, s.Pipe, s.PipeName); });
        }

        /// <summary>
        /// 命名管道 v2 的 <c>PipeConsole.Start</c> **已被调用**（Bootstrap 在它返回后调用本方法）。
        /// 口径（F3）：Start 只起 accept 线程就返回，真正的 bind 在 AcceptLoop 里 —— 事实位记的是
        /// 「线程已起」，不是「管道已可连接」；同名冲突时后到者绑不上但事实位仍报开（已接受的偏离，
        /// 规避 = 用不同的管道名）。据实口径，别据此宣称「bind 已确认」。
        /// </summary>
        /// <param name="pipeName">**可连接全名**（用 <c>PipeConsole.FullPipeName(短名)</c> 拼），不是命令行短名。</param>
        public static void InstallPipe(string pipeName)
        {
            Update(delegate(Snapshot s) { return new Snapshot(s.Gui, s.Stdio, true, pipeName ?? ""); });
        }

        /// <summary>测试隔离专用：清掉事实位（回到「未安装 = 未知」）。运行时不需要。</summary>
        public static void ResetForTests()
        {
            lock (Gate) { current = null; }
        }

        private static void Update(Func<Snapshot, Snapshot> change)
        {
            lock (Gate)
            {
                Snapshot s = current ?? new Snapshot(false, false, false, "");
                current = change(s);
            }
        }
    }
}
