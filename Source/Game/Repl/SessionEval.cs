using System;
using RoslynProbe.Repl;
using Verse;

namespace RoslynProbe
{
    public static class SessionEval
    {
        /// <summary>
        /// 入队后这么久 TickCount 仍未推进（且当前没有 Tick 在执行）⇒ 判定游戏主线程没在跑 Update。
        /// 正常 Update 每帧一次（16~60ms），2 秒足以覆盖一帧卡顿，又远小于 15 秒总超时
        /// ——把「沉默 15 秒」变成「2 秒内说清为什么没执行」。
        /// </summary>
        public const int EngineStallProbeMs = 2000;

        /// <summary>轮询粒度：既要及时发现结果，又要能在等待中检查引擎是否停摆。</summary>
        public const int PollMs = 50;

        /// <summary>
        /// 求值一次：入队 → 等结果（轮询）→ 超时收口。
        /// 两条超时路径必须区分，因为它们对「表达式到底有没有执行」的答案相反：
        /// <list type="bullet">
        /// <item><description>引擎停摆：请求还排在队列里、游戏主线程没跑过 Update —— 本次提交<strong>未执行</strong>。
        /// 此时 MarkTimedOut 把请求标为 orphaned，Executor.Tick 之后会跳过它（不再偷偷补跑）。</description></item>
        /// <item><description>总超时：引擎在推进但这条提交仍未结束 —— 处理器可能仍在执行，故文案提示先查副作用。</description></item>
        /// </list>
        /// 判停必须同时满足「入队后 TickCount 没推进」与「当前没有 Tick 在执行」：
        /// 后者排除「Update 正被一条长 handler 占住」这种 TickCount 同样不动的正常情况。
        /// </summary>
        public static ReplResult EnqueueEval(Executor executor, ReplHost host, string code)
        {
            const int timeoutMs = SessionLine.DefaultTimeoutMs;
            int ticksAtEnqueue = executor.TickCount;
            var req = executor.Enqueue(() =>
            {
                long entryId = ConsoleLog.Begin(code);
                return host.Eval(code, timeoutMs, entryId);
            }, timeoutMs);

            long start = Environment.TickCount64;
            while (true)
            {
                try
                {
                    if (req.Tcs.Task.Wait(PollMs))
                        return req.Tcs.Task.Result;
                }
                catch (Exception ex)
                {
                    return ReplResult.Failure("eval aborted: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                }

                long elapsed = Environment.TickCount64 - start;

                if (elapsed >= EngineStallProbeMs && executor.TickCount == ticksAtEnqueue && !executor.TickInProgress)
                {
                    executor.MarkTimedOut(req.Id);
                    return new ReplResult
                    {
                        TimedOut = true,
                        Error = "eval timed out: 游戏主线程 " + (EngineStallProbeMs / 1000)
                            + " 秒内没有跑过 Update（窗口失焦/最小化、游戏暂停或仍在加载）——本次提交未执行"
                    };
                }

                if (elapsed >= timeoutMs)
                {
                    executor.MarkTimedOut(req.Id);
                    return new ReplResult
                    {
                        TimedOut = true,
                        Error = "eval timed out: 游戏主线程 " + (timeoutMs / 1000)
                            + " 秒内未完成本次求值（引擎在推进，但这条提交仍未结束；可能仍在执行，先查副作用再决定是否重发）"
                    };
                }
            }
        }
    }
}
