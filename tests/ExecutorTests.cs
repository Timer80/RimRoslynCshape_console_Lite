using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RoslynProbe.Repl;
using Xunit;

namespace RoslynProbe.Tests
{
    public class ExecutorTests
    {
        [Fact]
        public async Task Enqueue_Then_Tick_Completes_Tcs_With_Handler_Result()
        {
            var executor = new Executor();

            ReplRequest req = executor.Enqueue(() => new ReplResult { Ok = true, Value = 42 }, 1000);
            executor.Tick(4);

            Assert.True(req.Tcs.Task.IsCompleted);
            ReplResult r = await req.Tcs.Task;
            Assert.True(r.Ok);
            Assert.Equal(42, r.Value);
        }

        [Fact]
        public void MaxPerFrame_Is_Respected_Across_Ticks()
        {
            var executor = new Executor();
            int counter = 0;

            for (int i = 0; i < 10; i++)
            {
                executor.Enqueue(() =>
                {
                    counter++;
                    return new ReplResult { Ok = true };
                }, 1000);
            }

            executor.Tick(4);
            Assert.Equal(4, counter);

            executor.Tick(4);
            Assert.Equal(8, counter);

            executor.Tick(2);
            Assert.Equal(10, counter);
        }

        [Fact]
        public void Reset_Faults_Pending_Tcs_And_Increments_Revision()
        {
            var executor = new Executor();
            var reqs = new List<ReplRequest>();
            for (int i = 0; i < 3; i++)
            {
                reqs.Add(executor.Enqueue(() => new ReplResult { Ok = true }, 1000));
            }

            int before = executor.Revision;
            executor.Reset();

            foreach (ReplRequest r in reqs)
            {
                Assert.True(r.Tcs.Task.IsCompleted);
                Assert.True(r.Tcs.Task.IsCanceled);
            }
            Assert.Equal(before + 1, executor.Revision);

            // A fresh enqueue + Tick still works after reset.
            ReplRequest req2 = executor.Enqueue(() => new ReplResult { Ok = true, Value = 7 }, 1000);
            executor.Tick(1);
            Assert.True(req2.Tcs.Task.IsCompleted);
        }

        [Fact]
        public async Task Panic_Makes_Enqueue_Return_Already_Completed_Poisoned_Request()
        {
            var executor = new Executor();
            executor.Panic();

            ReplRequest req = executor.Enqueue(() => new ReplResult { Ok = true }, 1000);

            Assert.True(req.Tcs.Task.IsCompleted);
            ReplResult r = await req.Tcs.Task;
            Assert.False(r.Ok);
            Assert.Equal("session poisoned", r.Error);
        }

        [Fact]
        public async Task MarkTimedOut_Completes_Tcs_And_Tick_Does_Not_Double_Complete()
        {
            var executor = new Executor();
            bool handlerRan = false;

            ReplRequest req = executor.Enqueue(() =>
            {
                handlerRan = true;
                return new ReplResult { Ok = true, Value = 99 };
            }, 5000);

            executor.MarkTimedOut(req.Id);
            executor.MarkTimedOut(req.Id); // second call must not throw

            Assert.True(req.Tcs.Task.IsCompleted);
            ReplResult r = await req.Tcs.Task;
            Assert.True(r.TimedOut);
            Assert.False(r.Ok);

            // Tick runs the (simulated blocking) handler once; must not throw / double-complete.
            executor.Tick(1);
            Assert.True(handlerRan);

            Assert.True(req.Tcs.Task.IsCompleted);
            ReplResult r2 = await req.Tcs.Task;
            Assert.True(r2.TimedOut); // unchanged
        }
    }
}
