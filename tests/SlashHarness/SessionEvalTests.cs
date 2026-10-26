// RoslynProbe —— SessionEval 停摆判定的回归。
// 背景（2026-10-26 事故）：Waylith 主循环在「窗口可见 + 失焦 + RunInBackground=false」时
// 会 continue 掉整帧的 OnUpdate，Executor.Tick 随之停摆，管道里的求值只能沉默 15 秒后超时。
// 这里钉两件事：
//   * 停摆时必须在 EngineStallProbeMs 量级就明确说「未执行」，而不是等满 DefaultTimeoutMs；
//   * 引擎恢复后，那条已收口的请求不得被 Tick 补跑（副作用不许凭空发生）。
// 零依赖（不用 xunit）：public static int Run() 返回失败数，由 SlashTestHarness 反射发现。

using System;
using System.Threading;
using RoslynProbe;
using RoslynProbe.Repl;

namespace RoslynProbe.Tests
{
    /// <summary>SessionEval：引擎停摆快失败 + 已收口请求不补跑。</summary>
    public static class SessionEvalTests
    {
        private static int failures;

        public static int Run()
        {
            failures = 0;
            Stall_Fails_Fast_And_Says_Not_Executed();
            TimedOut_Request_Is_Not_Executed_When_Engine_Resumes();
            Completed_Eval_Returns_Handler_Outcome();
            return failures;
        }

        /// <summary>
        /// 不 Tick ⇒ 引擎停摆：必须在探针阈值后不久就返回，且文案明确写「未执行」。
        /// host 传 null 是刻意的：停摆路径根本不该碰它，一碰就会 NRE，正好把回归变成失败。
        /// </summary>
        private static void Stall_Fails_Fast_And_Says_Not_Executed()
        {
            var executor = new Executor();
            executor.Tick(1);                       // 先让引擎"跑过一帧"，建立基准

            long start = Environment.TickCount64;
            ReplResult r = SessionEval.EnqueueEval(executor, null, "1+1");
            long elapsed = Environment.TickCount64 - start;

            Check(r != null && r.TimedOut, "停摆时应返回 TimedOut，实际 " + Show(r));
            Check(r != null && !string.IsNullOrEmpty(r.Error) && r.Error.Contains("未执行"),
                "停摆文案必须明确说「未执行」（用户据此决定要不要重发），实际: " + (r == null ? "null" : r.Error));
            Check(elapsed >= SessionEval.EngineStallProbeMs - 300,
                "不应早于探针阈值就下结论，经历了 " + elapsed + "ms");
            Check(elapsed < SessionLine.DefaultTimeoutMs,
                "停摆判定必须远快于总超时（否则等于仍旧沉默 15 秒），经历了 " + elapsed + "ms");
            Check(executor.PendingCount == 1,
                "超时收口只把请求标 orphaned、仍留在队列里，pending 应为 1，实际 " + executor.PendingCount);
        }

        /// <summary>
        /// 停摆收口之后引擎恢复：Tick 必须跳过那条请求（不执行 handler）。
        /// 用 ConsoleLog 记录数作观测点 —— handler 第一件事就是 ConsoleLog.Begin(code)。
        /// </summary>
        private static void TimedOut_Request_Is_Not_Executed_When_Engine_Resumes()
        {
            var executor = new Executor();
            executor.Tick(1);

            ReplResult r = SessionEval.EnqueueEval(executor, null, "1+1");
            Check(r != null && r.TimedOut, "前置：本次应判停摆超时");

            int before = ConsoleLog.Count;
            executor.Tick(4);                       // 引擎"恢复"后的第一次排空
            Check(ConsoleLog.Count == before,
                "已收口的请求不得被补跑（handler 一旦跑就会写 ConsoleLog），实际多出 "
                + (ConsoleLog.Count - before) + " 条");
            Check(executor.PendingCount == 0,
                "Tick 应把它出队，实际仍 pending=" + executor.PendingCount);
        }

        /// <summary>
        /// 正常路径：另一个线程调 EnqueueEval 阻塞等结果，主线程 Tick 排空。
        /// host 传 null ⇒ handler 在 host.Eval 处抛 NRE，被 Tick 兜底成 Failure
        /// —— 这恰好证明 handler 确实执行了（而不是被跳过）。
        /// </summary>
        private static void Completed_Eval_Returns_Handler_Outcome()
        {
            var executor = new Executor();
            executor.Tick(1);

            ReplResult result = null;
            var t = new Thread(delegate() { result = SessionEval.EnqueueEval(executor, null, "2+3"); });
            t.IsBackground = true;
            t.Start();

            // 等它真正入队，再排空（排空是主线程的活儿）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (executor.PendingCount == 0 && sw.ElapsedMilliseconds < 2000) Thread.Sleep(5);
            Check(executor.PendingCount == 1, "求值应先入队，实际 pending=" + executor.PendingCount);

            executor.Tick(4);
            Check(t.Join(3000), "主线程排空后，等待线程应结束");
            Check(result != null && !result.TimedOut, "正常排空不应超时，实际 " + Show(result));
            // Executor.Tick 会把 handler 逃逸的异常兜底成 Failure(ex.Message)；
            // host 为 null ⇒ NRE。能拿到失败结果就证明 handler 确实执行了（没被跳过）。
            Check(result != null && !result.Ok && !string.IsNullOrEmpty(result.Error),
                "host 为 null 时 handler 的异常应折算成失败结果，实际: " + (result == null ? "null" : result.Error));
        }

        private static string Show(ReplResult r)
        {
            if (r == null) return "null";
            return "Ok=" + r.Ok + " TimedOut=" + r.TimedOut + " Error=" + (r.Error ?? "(null)");
        }

        private static void Check(bool ok, string what)
        {
            if (ok) return;
            failures++;
            Console.WriteLine("    FAIL: " + what);
        }
    }
}
