using System.Collections.Generic;
using RoslynProbe.Repl;
using Xunit;

namespace RoslynProbe.Tests
{
    public class ConsoleLogTests
    {
        public ConsoleLogTests()
        {
            ConsoleLog.Clear(); // static 状态，用例间隔离
        }

        [Fact]
        public void Begin_Then_Complete_Completes_Entry()
        {
            long id = ConsoleLog.Begin("1+1");
            ConsoleLog.Complete(id, true, "2", null, null);

            ConsoleEntry e = ConsoleLog.Find(id);
            Assert.NotNull(e);
            Assert.True(e.Completed);
            Assert.True(e.Ok);
            Assert.Equal("2", e.ValueText);
            Assert.Null(e.ErrorText);
        }

        [Fact]
        public void Begin_Entry_Is_Pending_Until_Complete()
        {
            long id = ConsoleLog.Begin("Find.CurrentMap");
            ConsoleEntry e = ConsoleLog.Find(id);
            Assert.False(e.Completed);
            Assert.Equal("Find.CurrentMap", e.Input);
        }

        [Fact]
        public void Complete_Unknown_Id_Is_NoOp()
        {
            ConsoleLog.Begin("a");
            int before = ConsoleLog.Count;
            ConsoleLog.Complete(999999, true, "x", null, null);
            Assert.Equal(before, ConsoleLog.Count);
        }

        [Fact]
        public void Adjacent_Identical_Groups_Merge_Into_Repeats()
        {
            long a = ConsoleLog.Begin("1+1");
            ConsoleLog.Complete(a, true, "2", null, null);
            long b = ConsoleLog.Begin("1+1");
            ConsoleLog.Complete(b, true, "2", null, null);

            Assert.Equal(1, ConsoleLog.Count);
            Assert.Equal(2, ConsoleLog.Entries[0].Repeats);
            Assert.Null(ConsoleLog.Find(b)); // 被合并的组 id 消失
        }

        [Fact]
        public void Different_Results_Do_Not_Merge()
        {
            long a = ConsoleLog.Begin("1+1");
            ConsoleLog.Complete(a, true, "2", null, null);
            long b = ConsoleLog.Begin("1+1");
            ConsoleLog.Complete(b, true, "3", null, null);

            Assert.Equal(2, ConsoleLog.Count);
        }

        [Fact]
        public void Pending_Entry_Between_Prevents_Merge()
        {
            long a = ConsoleLog.Begin("1+1");
            ConsoleLog.Complete(a, true, "2", null, null);
            ConsoleLog.Begin("slow thing"); // pending 夹在中间
            long c = ConsoleLog.Begin("1+1");
            ConsoleLog.Complete(c, true, "2", null, null);

            Assert.Equal(3, ConsoleLog.Count); // 不回溯合并
        }

        [Fact]
        public void SystemNote_Is_Standalone_Error_Row()
        {
            ConsoleLog.SystemNote("! 队列已满");
            Assert.Equal(1, ConsoleLog.Count);
            ConsoleEntry e = ConsoleLog.Entries[0];
            Assert.True(e.IsSystem);
            Assert.True(e.Completed);
            Assert.False(e.Ok);
            Assert.Equal("! 队列已满", e.ErrorText);
        }

        [Fact]
        public void Eviction_At_MaxEntries_Drops_Oldest()
        {
            for (int i = 0; i < ConsoleLog.MaxEntries + 5; i++)
            {
                long id = ConsoleLog.Begin("code " + i);
                ConsoleLog.Complete(id, true, "ok " + i, null, null);
            }
            Assert.Equal(ConsoleLog.MaxEntries, ConsoleLog.Count);
            Assert.Equal("code 5", ConsoleLog.Entries[0].Input); // 最老 5 条被淘汰
        }

        [Fact]
        public void AbandonPending_Marks_All_Pending_As_Error()
        {
            long a = ConsoleLog.Begin("a");
            long b = ConsoleLog.Begin("b");
            long c = ConsoleLog.Begin("c");
            ConsoleLog.Complete(b, true, "x", null, null);
            long d = ConsoleLog.Begin("d");
            ConsoleLog.Complete(d, true, "y", null, null);

            ConsoleLog.AbandonPending("reset");

            Assert.True(ConsoleLog.Find(a).Completed);
            Assert.False(ConsoleLog.Find(a).Ok);
            Assert.Contains("reset", ConsoleLog.Find(a).ErrorText);
            Assert.True(ConsoleLog.Find(c).Completed);
            Assert.False(ConsoleLog.Find(c).Ok);
            Assert.Equal("x", ConsoleLog.Find(b).ValueText); // 已完成组不受影响
        }

        [Fact]
        public void Clear_Empties_Buffer()
        {
            ConsoleLog.Begin("1+1");
            ConsoleLog.Clear();
            Assert.Equal(0, ConsoleLog.Count);
        }
    }
}
