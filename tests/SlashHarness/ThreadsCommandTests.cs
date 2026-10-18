// RoslynProbe —— /threads 的纯逻辑单测(W5)。
// 零依赖(不用 xunit):public static int Run() 返回失败数,由 SlashTestHarness 反射发现。
// 覆盖:登记形状、Data 顶层/每项键集与类型、当前会话线程与主线程的标注、
// OS 线程描述取名字(**有区分力**的写法:另起一个既非当前、也非注册的线程,它的 managed 兜底必然是 null)、
// 契约 v1.1 的线程判据 ctx.IsGameThread(不是用 Channel 猜)、参数拒绝路径。
//
// 本测试**不改写宿主线程名**(M1:曾经给当前线程起名且不还原 ⇒ 断言强度随重跑衰减):
// 名字路径用一个一次性的辅助线程来验,线程结束即回收,重跑强度不衰减。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using RoslynProbe;

namespace RoslynProbe.Tests
{
    /// <summary>/threads:线程清单契约 + 会话/主线程标注 + 原生名字路径 + IsGameThread 判据。</summary>
    public static class ThreadsCommandTests
    {
        private static int failures;

        public static int Run()
        {
            failures = 0;
            SlashRegistry.ResetForTests();
            try
            {
                ThreadsCommand.Register();

                SlashEntry entry;
                Check(SlashRegistry.TryGet("threads", out entry), "Register 后应能查到 verb \"threads\"");
                Check(entry != null && entry.Kind == "threads", "Kind 应为 \"threads\"");
                Check(entry != null && entry.Usage == "—— 进程线程清单",
                    "Usage 应为「—— 进程线程清单」,实际 " + Show(entry == null ? null : entry.Usage));
                Check(entry != null && entry.Handler != null, "Handler 不应为 null");
                Check(ProvideExists(typeof(ThreadsCommand)), "纯 BCL 指令的 handler 应叫 Provide(契约 v1.1 §3.5)");

                SlashResult r = Dispatch("/threads");
                Check(r != null && r.Kind == "threads", "Kind 应为 \"threads\"");
                Check(DataBool(r, "ok"), "成功路径 Data[\"ok\"] 应为 true");
                Check(DataKeys(r, "ok", "count", "threads", "currentThreadId", "mainThreadId"),
                    "Data 顶层键集应与契约一致(含标注用 currentThreadId/mainThreadId)");
                Check(r.Lines.Exists(delegate (string l) { return l.IndexOf("IsGameThread=", StringComparison.Ordinal) >= 0; }),
                    "Lines 应展示 IsGameThread 事实位(通道仅展示,不是判据)");

                int count;
                Check(DataInt(r, "count", out count) && count >= 1, "count 应 >= 1(至少本测试线程),实际 " + count);

                var list = DataThreads(r);
                Check(list != null, "Data[\"threads\"] 应为 List<Dictionary<string,object>>");
                if (list == null) list = new List<Dictionary<string, object>>();
                Check(list.Count == count, "count 应等于 threads.Count,实际 " + count + " vs " + list.Count);

                var ids = new HashSet<int>();
                foreach (Dictionary<string, object> item in list)
                {
                    Check(item.ContainsKey("id") && item["id"] is int, "每项应有 int 键 id");
                    Check(ids.Add(item["id"] is int ? (int)item["id"] : -1), "线程 id 应唯一");
                    Check(item.ContainsKey("name") && (item["name"] == null || item["name"] is string), "name 应为 string 或 null");
                    Check(item.ContainsKey("isBackground") && (item["isBackground"] == null || item["isBackground"] is bool),
                        "isBackground 应为 bool 或 null");
                    Check(item.ContainsKey("priority") && (item["priority"] == null || item["priority"] is string),
                        "priority 应为 string 或 null");
                    Check(item.Count == 4, "每项应恰好 4 个键(id/name/isBackground/priority),实际 " + item.Count);
                }

                object cur = r.Data["currentThreadId"];
                object main = r.Data["mainThreadId"];
                Check(cur is int && (int)cur > 0, "Windows 上 currentThreadId 应为正的 int,实际 " + Show(cur));
                Check(main is int && (int)main > 0, "mainThreadId 应为正的 int(注册线程 = 本测试线程),实际 " + Show(main));
                if (cur is int && main is int)
                    Check((int)cur == (int)main, "注册与调用在同一线程 ⇒ currentThreadId 应等于 mainThreadId,实际 " + cur + " vs " + main);
                if (cur is int) Check(ids.Contains((int)cur), "currentThreadId 应出现在 threads 里");
                if (main is int) Check(ids.Contains((int)main), "mainThreadId 应出现在 threads 里");

                // 原生名字路径(只在 Windows 上有意义):有区分力的断言,见方法注释
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) CheckNativeThreadNamePath();

                // 参数拒绝路径
                SlashResult u = Dispatch("/threads abc");
                Check(u != null && !DataBool(u, "ok"), "/threads 带参数应返回用法结果(ok=false)");
                Check(u != null && u.Lines.Count > 0 && u.Lines[0].IndexOf("/threads", StringComparison.Ordinal) == 0,
                    "用法结果首行应点名 /threads,实际 " + Show(u == null || u.Lines.Count == 0 ? null : u.Lines[0]));
                SlashResult h = Dispatch("/threads help");
                Check(h != null && h.Lines.Count > 0, "/threads help 应给用法");
                Check(Dispatch("/threads") != null, "重复调用不得抛");

                // 契约 v1.1:IsGameThread 是主线程标注的唯一判据(放最后:它会改静态 mainThreadId,方法内自行还原)
                if (cur is int) CheckIsGameThreadIsAuthority((int)cur);
            }
            finally
            {
                SlashRegistry.ResetForTests();
                SlashRegistry.RegisterAll();
            }

            Console.WriteLine("ThreadsCommandTests: " + (failures == 0 ? "全通过" : failures + " 失败"));
            return failures;
        }

        /// <summary>
        /// 原生名字路径(**有区分力**):另起一个线程并命名,它在 /threads 里
        /// **既不是当前线程、也不是注册线程** ⇒ managedName 兜底必为 null、isBackground 也必为 null;
        /// 于是"name 出现且等于 Thread.Name、同时 isBackground 为 null"只可能来自 OS 线程描述(native)。
        /// 反例:native 路径失效时该行 name 会是 null(兜底拿不到名字)⇒ 断言红。
        /// (旧写法"给本线程起名再断言"没有区分力:native 失效时 managedName 恰好同名,断言照样绿。)
        /// </summary>
        private static void CheckNativeThreadNamePath()
        {
            string unique = "W5.NativeThreadProbe." + Guid.NewGuid().ToString("N").Substring(0, 8);
            using (var started = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                var probe = new Thread(delegate () { started.Set(); release.Wait(5000); })
                {
                    IsBackground = true,
                    Name = unique
                };
                probe.Start();
                try
                {
                    started.Wait(3000);
                    SlashResult r = Dispatch("/threads");
                    var list = DataThreads(r);
                    Dictionary<string, object> hit = null;
                    if (list != null)
                        foreach (Dictionary<string, object> item in list)
                            if (item["name"] is string && ((string)item["name"]).IndexOf(unique, StringComparison.Ordinal) >= 0) { hit = item; break; }

                    Check(hit != null,
                        "被命名的辅助线程应出现在 /threads 里且名字来自 OS 线程描述(nativeName):该线程不是当前/注册线程,"
                        + "managedName 兜底为 null ⇒ 原生路径无效时这里就是 null。期望含 " + unique);
                    if (hit == null) return;

                    Check(string.Equals(hit["name"] as string, unique, StringComparison.Ordinal),
                        "OS 线程描述应逐字返回 Thread.Name,实际 " + Show(hit["name"]));
                    Check(hit["isBackground"] == null,
                        "辅助线程的 isBackground 应为 null(这证明该行 name 走的确实是 native 而不是 managed 兜底),实际 " + Show(hit["isBackground"]));
                }
                finally
                {
                    release.Set();
                    probe.Join(3000);
                }
            }
        }

        /// <summary>
        /// 契约 v1.1 §3.4.1:主线程标注**只认** ctx.IsGameThread,不用 Channel 猜。
        /// 从另一个工作线程调用(Channel 故意填 "pipe",绝不用 "gui" 当判据):
        /// IsGameThread=false ⇒ 主线程保持注册线程;IsGameThread=true ⇒ 改取当前(工作)线程。
        /// 旧的 Channel=="gui" 写法在这两条上都会失败。
        /// </summary>
        private static void CheckIsGameThreadIsAuthority(int registerThreadId)
        {
            int workerTid = -1, plainCur = -1, plainMain = -1, gameCur = -1, gameMain = -1;
            var worker = new Thread(delegate ()
            {
                SlashResult a = Dispatch("/threads", new SlashContext { Channel = "pipe", IsGameThread = false });
                workerTid = IdOf(a, "currentThreadId");
                plainCur = workerTid;
                plainMain = IdOf(a, "mainThreadId");

                SlashResult b = Dispatch("/threads", new SlashContext { Channel = "pipe", IsGameThread = true });
                gameCur = IdOf(b, "currentThreadId");
                gameMain = IdOf(b, "mainThreadId");
            });
            worker.IsBackground = true;
            worker.Name = "W5.IsGameThreadProbe";
            worker.Start();
            worker.Join(10000);

            Check(workerTid > 0 && workerTid != registerThreadId,
                "工作线程 tid 应与注册线程不同,实际 worker=" + workerTid + " register=" + registerThreadId);
            Check(plainCur == workerTid, "工作线程上的 currentThreadId 应是它自己,实际 " + plainCur + " 期望 " + workerTid);
            Check(plainMain == registerThreadId,
                "IsGameThread=false 时不得改主线程(即使 Channel=\"pipe\"),实际 main=" + plainMain + " 期望 " + registerThreadId);
            Check(gameCur == workerTid, "IsGameThread=true 时 currentThreadId 应是工作线程,实际 " + gameCur + " 期望 " + workerTid);
            Check(gameMain == workerTid,
                "IsGameThread=true 时应以当前线程为准(只认事实位,不看 Channel),实际 main=" + gameMain + " 期望 " + workerTid);

            // 还原静态状态:在注册线程上以 IsGameThread=true 调一次 ⇒ mainThreadId 回到注册线程(本线程)。
            Dispatch("/threads", new SlashContext { Channel = "pipe", IsGameThread = true });
        }

        // ---------------- 断言与取值助手 ----------------

        private static SlashResult Dispatch(string line)
        {
            return Dispatch(line, new SlashContext { Channel = "test" });
        }

        private static SlashResult Dispatch(string line, SlashContext ctx)
        {
            SlashResult r;
            bool hit = SlashRegistry.TryDispatch(line, ctx, out r);
            Check(hit, "TryDispatch(" + Show(line) + ") 应返回 true");
            Check(r != null, "TryDispatch(" + Show(line) + ") 的结果不应为 null");
            return r;
        }

        /// <summary>契约 v1.1 §3.5:纯 BCL 指令的 handler 方法名统一 Provide(string, SlashContext)。</summary>
        private static bool ProvideExists(Type commandType)
        {
            return commandType.GetMethod("Provide", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null, new Type[] { typeof(string), typeof(SlashContext) }, null) != null;
        }

        private static int IdOf(SlashResult r, string key)
        {
            object v = r == null || r.Data == null ? null : r.Data[key];
            return v is int ? (int)v : -1;
        }

        private static bool DataBool(SlashResult r, string key)
        {
            return Val(r, key) is bool && (bool)Val(r, key);
        }

        private static bool DataInt(SlashResult r, string key, out int value)
        {
            value = 0;
            object v = Val(r, key);
            if (v is int) { value = (int)v; return true; }
            Check(false, "Data[" + key + "] 应为 int,实际 " + TypeName(v));
            return false;
        }

        private static List<Dictionary<string, object>> DataThreads(SlashResult r)
        {
            object v = Val(r, "threads");
            var list = v as List<Dictionary<string, object>>;
            Check(list != null, "Data[\"threads\"] 应为 List<Dictionary<string,object>>,实际 " + TypeName(v));
            return list;
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
