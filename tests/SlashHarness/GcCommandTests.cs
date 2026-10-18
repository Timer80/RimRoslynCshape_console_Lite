// RoslynProbe —— /gc 的纯逻辑单测。
// 零依赖(不用 xunit):public static int Run() 返回失败数,由 SlashTestHarness 反射发现。

using System;
using System.Collections.Generic;
using RoslynProbe;

namespace RoslynProbe.Tests
{
    /// <summary>/gc:登记形状 + Data 契约(键名/类型)+ 参数拒绝路径。</summary>
    public static class GcCommandTests
    {
        private static int failures;

        public static int Run()
        {
            failures = 0;
            SlashRegistry.ResetForTests();
            try
            {
                GcCommand.Register();

                SlashEntry entry;
                Check(SlashRegistry.TryGet("gc", out entry), "Register 后应能查到 verb \"gc\"");
                Check(entry != null && entry.Kind == "gc", "Kind 应为 \"gc\"");
                Check(entry != null && entry.Usage == "—— GC 堆占用与分代",
                    "Usage 应为「—— GC 堆占用与分代」,实际 " + Show(entry == null ? null : entry.Usage));
                Check(entry != null && entry.Group == "运行时", "Group 应为「运行时」");
                Check(entry != null && entry.Handler != null, "Handler 不应为 null(已登记未实现)");
                Check(typeof(GcCommand).GetMethod("Provide", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new Type[] { typeof(string), typeof(SlashContext) }, null) != null,
                    "纯 BCL 指令的 handler 应叫 Provide(契约 v1.1 §3.5)");

                SlashResult r = Dispatch("/gc");
                Check(r != null && r.Kind == "gc", "Kind 应为 \"gc\"");
                Check(r != null && r.Data != null, "Data 不应为 null");
                Check(DataBool(r, "ok"), "成功路径 Data[\"ok\"] 应为 true");
                Check(r != null && r.Lines.Count >= 6, "/gc 应给出多行输出,实际 " + (r == null ? -1 : r.Lines.Count) + " 行");

                long heap = DataLong(r, "heapBytes");
                Check(heap >= 0, "Data[\"heapBytes\"] 应 >= 0,实际 " + heap);
                long total = DataLong(r, "totalMemoryBytes");
                Check(total > 0, "Data[\"totalMemoryBytes\"] 应 > 0(进程可见总量),实际 " + total);
                long alloc = DataLong(r, "allocatedBytes");
                Check(alloc >= 0, "Data[\"allocatedBytes\"] 应 >= 0,实际 " + alloc);
                long working = DataLong(r, "workingSetBytes");
                Check(working > 0, "Data[\"workingSetBytes\"] 应 > 0,实际 " + working);

                int g0, g1, g2;
                Check(DataInt(r, "gen0", out g0) && g0 >= 0, "Data[\"gen0\"] 应为 int(非负)");
                Check(DataInt(r, "gen1", out g1) && g1 >= 0, "Data[\"gen1\"] 应为 int(非负)");
                Check(DataInt(r, "gen2", out g2) && g2 >= 0, "Data[\"gen2\"] 应为 int(非负)");
                if (g0 >= 0 && g1 >= 0 && g2 >= 0)
                    Check(g2 <= g1 && g1 <= g0, "分代回收次数应满足 gen2 <= gen1 <= gen0,实际 " + g2 + "/" + g1 + "/" + g0);

                Check(DataKeys(r, "ok", "heapBytes", "totalMemoryBytes", "gen0", "gen1", "gen2", "allocatedBytes", "workingSetBytes"),
                    "Data 键集应与冻结契约一致");

                // 无参指令的参数拒绝路径(响亮、可读,不回落到 C# 编译)
                SlashResult u = Dispatch("/gc 200");
                Check(u != null && !DataBool(u, "ok"), "/gc 带参数应返回用法结果(ok=false)");
                Check(u != null && u.Lines.Count > 0 && u.Lines[0].IndexOf("/gc", StringComparison.Ordinal) == 0,
                    "用法结果首行应点名 /gc,实际 " + Show(u == null || u.Lines.Count == 0 ? null : u.Lines[0]));
                SlashResult h = Dispatch("/gc help");
                Check(h != null && h.Lines.Count > 0, "/gc help 应给用法");
                Check(h != null && !DataBool(h, "ok"), "/gc help 不是成功数据(ok=false)");

                SlashResult again = Dispatch("/gc");
                Check(again != null && DataBool(again, "ok"), "重复调用应稳定成功");
                SlashResult blank = Dispatch("/gc \t\t");
                Check(blank != null && DataBool(blank, "ok"), "纯空白参数经 Trim 后等同无参");
            }
            finally
            {
                SlashRegistry.ResetForTests();
                SlashRegistry.RegisterAll();
            }

            Console.WriteLine("GcCommandTests: " + (failures == 0 ? "全通过" : failures + " 失败"));
            return failures;
        }

        // ---------------- 断言与取值助手(与 SlashRegistryTests 同风格) ----------------

        private static SlashResult Dispatch(string line)
        {
            SlashResult r;
            bool hit = SlashRegistry.TryDispatch(line, new SlashContext { Channel = "test" }, out r);
            Check(hit, "TryDispatch(" + Show(line) + ") 应返回 true");
            Check(r != null, "TryDispatch(" + Show(line) + ") 的结果不应为 null");
            return r;
        }

        private static bool DataBool(SlashResult r, string key)
        {
            return Val(r, key) is bool && (bool)Val(r, key);
        }

        private static long DataLong(SlashResult r, string key)
        {
            object v = Val(r, key);
            Check(v is long, "Data[" + key + "] 应为 long,实际 " + TypeName(v));
            return v is long ? (long)v : 0L;
        }

        private static bool DataInt(SlashResult r, string key, out int value)
        {
            value = 0;
            object v = Val(r, key);
            if (v is int) { value = (int)v; return true; }
            Check(false, "Data[" + key + "] 应为 int,实际 " + TypeName(v));
            return false;
        }

        private static bool DataKeys(SlashResult r, params string[] expected)
        {
            if (r == null || r.Data == null) { Check(false, "Data 不应为 null"); return false; }
            var actual = new List<string>(r.Data.Keys);
            var want = new List<string>(expected);
            actual.Sort(StringComparer.Ordinal);
            want.Sort(StringComparer.Ordinal);
            bool same = actual.Count == want.Count;
            if (same)
                for (int i = 0; i < actual.Count; i++)
                    if (!string.Equals(actual[i], want[i], StringComparison.Ordinal)) { same = false; break; }
            if (!same)
                Check(false, "Data 键集不符:期望 [" + string.Join(",", want.ToArray()) + "],实际 [" + string.Join(",", actual.ToArray()) + "]");
            return same;
        }

        private static object Val(SlashResult r, string key)
        {
            if (r == null || r.Data == null || !r.Data.ContainsKey(key)) { Check(false, "Data 缺键 " + key); return null; }
            return r.Data[key];
        }

        private static string TypeName(object v)
        {
            return v == null ? "(null)" : v.GetType().Name;
        }

        private static string Show(object v)
        {
            return v == null ? "(null)" : v.ToString();
        }

        private static void Check(bool condition, string message)
        {
            if (condition) return;
            failures++;
            Console.WriteLine("  FAIL " + message);
        }
    }
}
