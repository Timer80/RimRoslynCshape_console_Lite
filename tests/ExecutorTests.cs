using System;
using System.Collections.Generic;
using System.Threading;
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
        public async Task MarkTimedOut_Completes_Tcs_And_Tick_Skips_Orphaned_Handler()
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

            // Tick must NOT run an already-orphaned handler：调用方已经被告知「未执行」，
            // 之后再偷偷补跑就等于让副作用凭空发生（用户以为失败、实际已改游戏状态）。
            executor.Tick(1);
            Assert.False(handlerRan);

            Assert.True(req.Tcs.Task.IsCompleted);
            ReplResult r2 = await req.Tcs.Task;
            Assert.True(r2.TimedOut); // unchanged
        }

        [Fact]
        public void IdleMs_Grows_Without_Tick_And_Resets_On_Tick()
        {
            var executor = new Executor();
            executor.Tick(1);                       // 建立基准时刻（否则 IdleMs 是启动至今）
            Assert.True(executor.IdleMs < 1000, "刚 Tick 过，IdleMs 应很小，实际 " + executor.IdleMs);

            Thread.Sleep(80);
            Assert.True(executor.IdleMs >= 50, "没有 Tick 时 IdleMs 必须增长，实际 " + executor.IdleMs);

            executor.Tick(1);
            Assert.True(executor.IdleMs < 1000, "Tick 之后 IdleMs 应回到很小，实际 " + executor.IdleMs);
        }

        [Fact]
        public void TickInProgress_Is_True_During_Handler_And_False_After()
        {
            var executor = new Executor();
            bool? seenDuring = null;
            executor.Enqueue(() =>
            {
                seenDuring = executor.TickInProgress;
                return new ReplResult { Ok = true };
            }, 1000);

            Assert.False(executor.TickInProgress);
            executor.Tick(1);
            Assert.True(seenDuring == true, "handler 执行期间 TickInProgress 必须为 true");
            Assert.False(executor.TickInProgress, "Tick 返回后 TickInProgress 必须复位");
        }

        [Fact]
        public void Tick_Skips_Orphaned_But_Still_Drains_Later_Requests()
        {
            var executor = new Executor();
            bool orphanedRan = false;
            bool laterRan = false;

            ReplRequest orphan = executor.Enqueue(() => { orphanedRan = true; return new ReplResult { Ok = true }; }, 1000);
            executor.Enqueue(() => { laterRan = true; return new ReplResult { Ok = true, Value = 1 }; }, 1000);

            executor.MarkTimedOut(orphan.Id);

            executor.Tick(2);
            Assert.False(orphanedRan, "被标超时的请求不得执行");
            Assert.True(laterRan, "同一批里的正常请求仍须执行");
        }
    }
}
