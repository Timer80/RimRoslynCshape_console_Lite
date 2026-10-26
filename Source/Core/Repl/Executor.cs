using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RoslynProbe.Repl
{
    public class Executor
    {
        private readonly Queue<ReplRequest> queue = new Queue<ReplRequest>();

        public int Revision { get; private set; }

        public bool Poisoned { get; private set; }

        /// <summary>Number of main-thread drains so far. Proves Unity fires Update in headless mode.</summary>
        public int TickCount { get; private set; }

        /// <summary>上一次 Tick 的时刻（<see cref="Environment.TickCount64"/> 毫秒，单调时钟）。</summary>
        public long LastTickMs { get; private set; }

        /// <summary>
        /// 距上一次 Tick 的毫秒数。游戏主线程停摆（窗口失焦/最小化、暂停、加载）时该值持续增长 ——
        /// 这是「引擎没在跑 Update」的唯一进程内事实，供 /status 上报与求值路径判停。
        /// 从未 Tick 过时返回进程启动至今的毫秒数（截断到 int），不会为负。
        /// </summary>
        public int IdleMs
        {
            get
            {
                long idle = Environment.TickCount64 - LastTickMs;
                if (idle < 0) idle = 0;
                return idle > int.MaxValue ? int.MaxValue : (int)idle;
            }
        }

        /// <summary>
        /// Tick 正在执行中（含正在跑 handler）。只有它能把「Update 正被一条长请求占住」
        /// 与「Update 根本没被调用」区分开：两种情况 TickCount 都不推进，只按 TickCount
        /// 判停会把前者误判成停摆（那会谎报「未执行」，而表达式其实正在执行）。
        /// </summary>
        public volatile bool TickInProgress;

        /// <summary>Number of requests waiting in the queue (not yet drained by Tick).</summary>
        public int PendingCount { get { return queue.Count; } }

        public ReplRequest Enqueue(Func<ReplResult> handler, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<ReplResult>();

            if (Poisoned)
            {
                tcs.SetResult(new ReplResult { Ok = false, Error = "session poisoned" });
                return new ReplRequest
                {
                    Id = Guid.NewGuid(),
                    Handler = handler,
                    Tcs = tcs,
                    TimeoutMs = timeoutMs,
                    Completed = true
                };
            }

            var req = new ReplRequest
            {
                Id = Guid.NewGuid(),
                Handler = handler,
                Tcs = tcs,
                TimeoutMs = timeoutMs
            };

            queue.Enqueue(req);
            return req;
        }

        public void Tick(int maxPerFrame)
        {
            TickCount++;
            LastTickMs = Environment.TickCount64;
            TickInProgress = true;
            try
            {
                int n = Math.Min(maxPerFrame, queue.Count);
                for (int i = 0; i < n; i++)
                {
                    ReplRequest req = queue.Dequeue();

                    // 已超时弃权的请求**不执行**。调用方（SessionEval / StatusCommand 的 marshal）
                    // 在超时分支里已经告诉了用户「未执行」；此时再执行，副作用会悄悄发生
                    // ——用户以为失败、实际已改游戏状态，是最坏的一类谎报。
                    if (req.Orphaned)
                    {
                        req.Completed = true;
                        continue;
                    }

                    ReplResult result;
                    try
                    {
                        result = req.Handler();
                    }
                    catch (Exception ex)
                    {
                        result = new ReplResult { Ok = false, Error = ex.Message };
                    }

                    req.Completed = true;

                    // If already orphaned (timed out) the Tcs is already completed;
                    // do not attempt to complete it again.
                    if (!req.Orphaned)
                    {
                        req.Tcs.TrySetResult(result);
                    }
                }
            }
            finally
            {
                TickInProgress = false;
            }
        }

        public void MarkTimedOut(Guid id)
        {
            foreach (ReplRequest req in queue)
            {
                if (req.Id == id && !req.Completed)
                {
                    req.Orphaned = true;
                    req.Completed = true;
                    req.Tcs.TrySetResult(new ReplResult { TimedOut = true });
                    break;
                }
            }
        }

        public void Reset()
        {
            Revision++;

            foreach (ReplRequest req in queue)
            {
                req.Completed = true;
                req.Tcs.TrySetCanceled();
            }

            queue.Clear();
        }

        public void Panic()
        {
            Poisoned = true;
        }
    }
}
