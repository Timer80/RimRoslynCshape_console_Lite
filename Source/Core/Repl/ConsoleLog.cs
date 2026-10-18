// RoslynProbe —— 控制台专用日志。
//
// 双日志架构的一半：控制台窗口的唯一渲染数据源，与 Verse.Log.Messages 完全脱钩
// （原版 log 循环滚红字时这里照常工作，规格 §1/§4）。另一半是 ReplHost.Eval 完成时
// 向原版 Log.Message/Warning 的单向双写 —— 本类绝不读原版缓冲。
//
// 线程约定：所有方法只允许在游戏更新线程调用（Executor.Tick 内联求值的同一线程），无锁。
// 纯数据、零 Unity/Verse 依赖 —— tests 工程只编译 Source/Core，本类必须留在 Core。
//
// 合并规则：相邻完全相同的"求值组"（input+result 全等）合并 repeats；输入与返回永远成对；
// 系统提示行（SystemNote）独立存在，不参与配对与合并（规格 §5）。
// 已知取舍：合并时丢弃被并组 attachments（与原版 repeats 合并丢堆栈同款）。

using System;
using System.Collections.Generic;

namespace RoslynProbe.Repl
{
    /// <summary>缓冲中的一条记录：求值组（输入+返回）或独立系统提示行。</summary>
    public class ConsoleEntry
    {
        public long Id;
        public bool IsSystem;
        public string Input;
        public bool Completed;
        public bool Ok;
        public string ValueText;
        public string ErrorText;
        public readonly List<string> Attachments = new List<string>();
        public int Repeats = 1;
    }

    public static class ConsoleLog
    {
        /// <summary>环形缓冲上限（组数）。淘汰即失，合并计数不复活（规格 §6）。</summary>
        public const int MaxEntries = 1000;

        private static readonly List<ConsoleEntry> entries = new List<ConsoleEntry>();
        private static long nextId = 1;

        /// <summary>当前组数（含未完成组与系统行）。</summary>
        public static int Count { get { return entries.Count; } }

        /// <summary>内部列表的活引用（非快照），按时间正序。渲染层按索引遍历，且不得在枚举期间调用本类的写方法。</summary>
        public static IReadOnlyList<ConsoleEntry> Entries { get { return entries; } }

        /// <summary>入队即建组：输入行立即可见于流（规格 §3.2）。</summary>
        public static long Begin(string input)
        {
            var e = new ConsoleEntry { Id = nextId++, Input = input ?? "" };
            entries.Add(e);
            EvictOverflow();
            return e.Id;
        }

        /// <summary>独立系统提示行（未接线、队列已满等），永不成对、永不合并。</summary>
        public static long SystemNote(string text)
        {
            var e = new ConsoleEntry { Id = nextId++, IsSystem = true, Completed = true, Ok = false, ErrorText = text ?? "" };
            entries.Add(e);
            EvictOverflow();
            return e.Id;
        }

        /// <summary>
        /// 求值完成时落账。id 不存在（已被淘汰）时静默忽略。
        /// 若本组是列表最后一组且紧邻前组已完成且完全同文，则并为其 repeats。
        /// </summary>
        public static void Complete(long id, bool ok, string valueText, string errorText, List<string> attachments)
        {
            int idx = LastIndexOfPending(id);
            if (idx < 0) return;
            ConsoleEntry e = entries[idx];

            if (idx == entries.Count - 1 && idx > 0)
            {
                ConsoleEntry prev = entries[idx - 1];
                if (prev.Completed && !prev.IsSystem
                    && string.Equals(prev.Input, e.Input, StringComparison.Ordinal)
                    && SameResult(prev, ok, valueText, errorText))
                {
                    prev.Repeats++;
                    entries.RemoveAt(idx);
                    return;
                }
            }

            e.Completed = true;
            e.Ok = ok;
            e.ValueText = valueText;
            e.ErrorText = errorText;
            if (attachments != null) e.Attachments.AddRange(attachments);
        }

        /// <summary>/reset 等场景：所有未完成组标记为失败，避免永久 pending（规格 §6）。</summary>
        public static void AbandonPending(string reason)
        {
            foreach (ConsoleEntry e in entries)
            {
                if (!e.Completed)
                {
                    e.Completed = true;
                    e.Ok = false;
                    e.ErrorText = "aborted (" + (reason ?? "unknown") + ")";
                }
            }
        }

        public static ConsoleEntry Find(long id)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i].Id == id) return entries[i];
            return null;
        }

        public static void Clear()
        {
            entries.Clear();
        }

        private static int LastIndexOfPending(long id)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i].Id == id && !entries[i].Completed) return i;
            return -1;
        }

        private static bool SameResult(ConsoleEntry e, bool ok, string valueText, string errorText)
        {
            return e.Ok == ok
                && string.Equals(e.ValueText, valueText, StringComparison.Ordinal)
                && string.Equals(e.ErrorText, errorText, StringComparison.Ordinal);
        }

        private static void EvictOverflow()
        {
            while (entries.Count > MaxEntries) entries.RemoveAt(0);
        }
    }
}
