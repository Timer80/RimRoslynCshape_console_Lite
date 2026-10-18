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
            int n = Math.Min(maxPerFrame, queue.Count);
            for (int i = 0; i < n; i++)
            {
                ReplRequest req = queue.Dequeue();
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
