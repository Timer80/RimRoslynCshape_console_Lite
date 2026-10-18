using System;
using RoslynProbe.Repl;
using Verse;

namespace RoslynProbe
{
    public static class SessionEval
    {
        public static ReplResult EnqueueEval(Executor executor, ReplHost host, string code)
        {
            const int timeoutMs = SessionLine.DefaultTimeoutMs;
            var req = executor.Enqueue(() =>
            {
                long entryId = ConsoleLog.Begin(code);
                return host.Eval(code, timeoutMs, entryId);
            }, timeoutMs);

            ReplResult r;
            try
            {
                r = req.Tcs.Task.Wait(timeoutMs)
                    ? req.Tcs.Task.Result
                    : new ReplResult { TimedOut = true };
            }
            catch (Exception ex)
            {
                r = ReplResult.Failure("eval aborted: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
            }
            if (r.TimedOut) executor.MarkTimedOut(req.Id);
            return r;
        }
    }
}
