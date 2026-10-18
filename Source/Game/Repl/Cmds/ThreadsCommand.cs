// RoslynProbe —— /threads:进程线程清单(W5,纯 BCL + 两处 Windows 原生查询)。
//
// 原生清单:Process.GetCurrentProcess().Threads(Id / PriorityLevel)。原生线程没有"是否后台"
// 这个概念,所以 isBackground 只有我们能拿到托管 Thread 对象的线程才给得出(当前会话线程、
// 注册线程),其余写 null —— 这是 Data 里允许为 null 的两处语义之一。
//
// 线程名:Windows 的 GetThreadDescription(线程描述)。.NET 在设置 Thread.Name 时会写线程描述,
// 所以 "RoslynProbe.ConsolePipe" / "RoslynProbe.PipeConsole" / "RoslynProbe.HTTP" /
// ".NET Finalizer" 这类名字能看到(.NET 10 + Windows 11 实测可行)。
//   实测注意:某些 Windows 构建 / kernel32 hook 下 GetThreadDescription 返回非 0 的 HRESULT
//   (实测 0x10000000)但缓冲区照样有效 —— 因此这里**不判 HRESULT**,只认"指针非空且字符串非空";
//   非 Windows / OpenThread 失败 / API 缺失一律降级为 null,绝不抛、绝不崩。
//
// 会话线程与主线程:handler 所在的线程就是"当前会话线程"(stdio / 管道 = 读线程,GUI = 游戏线程);
//   "游戏主线程" = 注册(RegisterAll,StaticConstructorOnStartup 主线程)时所在线程;
//   若本次调用的**线程事实位** ctx.IsGameThread 为 true(契约 v1.1 §3.4.1:壳层填,拿不准填 false),
//   则改取当前线程(权威:本次就在游戏线程)。**不用 Channel 猜线程** —— Channel 只用于展示。
//
// 只读 ⇒ 不需要 Executor marshal;本文件绝不碰 ConsoleLog / Unity / Verse。
//
// Data 键(小驼峰、稳定,工具消费方依赖):count,threads[{id,name,isBackground,priority}],
// currentThreadId,mainThreadId —— 取不到的一律写 null(name / isBackground / 两个 id)。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace RoslynProbe
{
    /// <summary>/threads —— 进程线程清单(纯 BCL;无参数)。</summary>
    public static class ThreadsCommand
    {
        /// <summary>OpenThread 权限:THREAD_QUERY_LIMITED_INFORMATION(读线程描述所需的最小权限)。</summary>
        private const uint ThreadQueryLimitedInformation = 0x0800;

        // ---- 注册期捕获(RegisterAll 在 Bootstrap 的 StaticConstructorOnStartup 里跑 ⇒ 主线程) ----
        private static int mainThreadId = -1;
        private static int registerThreadId = -1;
        private static string registerThreadName;
        private static bool registerThreadIsBackground;
        private static bool registerThreadCaptured;

        /// <summary>RegisterAll 反射调用:public static void Register(),无参、返回 void。</summary>
        public static void Register()
        {
            registerThreadId = CurrentNativeThreadId();
            registerThreadName = Thread.CurrentThread.Name;
            registerThreadIsBackground = Thread.CurrentThread.IsBackground;
            registerThreadCaptured = registerThreadId > 0;
            if (mainThreadId <= 0 && registerThreadCaptured) mainThreadId = registerThreadId;

            SlashRegistry.Register(new SlashEntry
            {
                Verb = "threads",
                Usage = "—— 进程线程清单",
                Group = "运行时",
                Kind = "threads",
                Handler = Provide
            });
        }

        /// <summary>指令实现(契约 v1.1 §3.5:纯 BCL 指令的 handler 也叫 Provide)。</summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            string args = (rest ?? "").Trim();
            if (args.Length > 0 && IsHelp(args)) return Usage(null);
            if (args.Length > 0) return Usage("不接受参数: \"" + args + "\"(/threads 无参数)");

            int currentId = CurrentNativeThreadId();
            // 契约 v1.1 §3.4.1:线程判据**只认事实位** ctx.IsGameThread(壳层填,拿不准 false)。
            // 为 true ⇒ 本次调用就在游戏线程,当前线程即游戏主线程(权威),覆盖注册期取的值。
            bool gameThread = ctx != null && ctx.IsGameThread;
            if (currentId > 0 && gameThread) mainThreadId = currentId;

            int mainId = mainThreadId;
            var threads = new List<Dictionary<string, object>>();
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    foreach (ProcessThread pt in process.Threads)
                    {
                        int id;
                        try { id = pt.Id; } catch { continue; }
                        if (id <= 0) continue;

                        string nativeName = NativeThreadName(id);
                        string managedName = null;
                        bool? isBackground = null;
                        if (id == currentId)
                        {
                            Thread me = Thread.CurrentThread;
                            managedName = me.Name;
                            isBackground = me.IsBackground;
                        }
                        else if (registerThreadCaptured && id == registerThreadId)
                        {
                            managedName = registerThreadName;
                            isBackground = registerThreadIsBackground;
                        }

                        string name = !string.IsNullOrEmpty(nativeName) ? nativeName : managedName;
                        threads.Add(new Dictionary<string, object>
                        {
                            ["id"] = id,
                            ["name"] = string.IsNullOrEmpty(name) ? null : name,
                            ["isBackground"] = isBackground,
                            ["priority"] = ReadPriority(pt)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return SlashResults.Error("threads", "线程清单读取失败: " + ex.GetType().Name + ": " + ex.Message);
            }

            threads.Sort(delegate (Dictionary<string, object> a, Dictionary<string, object> b)
            {
                return ((int)a["id"]).CompareTo((int)b["id"]);
            });

            bool mainFound = false;
            foreach (Dictionary<string, object> t in threads)
                if ((int)t["id"] == mainId) { mainFound = true; break; }

            var r = new SlashResult { Kind = "threads" };
            r.Data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["count"] = threads.Count,
                ["threads"] = threads,
                ["currentThreadId"] = currentId > 0 ? (object)currentId : null,
                ["mainThreadId"] = mainId > 0 ? (object)mainId : null
            };

            r.Lines.Add("线程数: " + threads.Count.ToString(CultureInfo.InvariantCulture)
                + "(当前会话线程 tid=" + IdText(currentId) + ";游戏主线程 tid=" + IdText(mainId) + ")");
            r.Lines.Add("调用上下文: 通道=" + ChannelText(ctx) + ";IsGameThread=" + (gameThread ? "true" : "false")
                + "(主线程标注只认 IsGameThread 这个事实位,契约 v1.1 §3.4.1)");

            foreach (Dictionary<string, object> t in threads)
            {
                int id = (int)t["id"];
                string name = t["name"] as string;
                bool? bg = t["isBackground"] as bool?;
                string priority = t["priority"] as string;

                string note = "";
                if (id == currentId && id == mainId) note = "  ← 当前会话线程(= 游戏主线程)";
                else if (id == currentId) note = "  ← 当前会话线程";
                else if (id == mainId) note = "  ← 游戏主线程";

                r.Lines.Add("  tid=" + id.ToString(CultureInfo.InvariantCulture).PadRight(7)
                    + " name=" + (string.IsNullOrEmpty(name) ? "(无名)" : name).PadRight(32)
                    + " bg=" + (bg.HasValue ? (bg.Value ? "true" : "false") : "?").PadRight(6)
                    + " prio=" + (string.IsNullOrEmpty(priority) ? "(未知)" : priority)
                    + note);
            }

            r.Lines.Add("说明: name 来自 OS 线程描述(.NET 给有名字的托管线程写描述);bg=? 表示原生线程查不到是否后台(只有当前线程与注册线程可得)。");
            r.Lines.Add("说明: 游戏主线程 = 注册(RegisterAll)时所在线程;本次 IsGameThread=true 时改取当前线程(契约 v1.1)。");
            if (mainId > 0 && !mainFound)
                r.Lines.Add("提示: 游戏主线程 tid=" + mainId.ToString(CultureInfo.InvariantCulture)
                    + " 不在当前线程清单里(该线程已退出,或注册发生在非主线程)。");
            return r;
        }

        private static SlashResult Usage(string problem)
        {
            SlashResult r = SlashResults.Usage("threads",
                "用法: /threads —— 列出进程内线程的 tid / name / 是否后台 / 优先级,并标注当前会话线程与游戏主线程(无参数)",
                "Data: count / threads[{id,name,isBackground,priority}] / currentThreadId / mainThreadId");
            if (!string.IsNullOrEmpty(problem)) r.Lines.Insert(0, "/threads " + problem);
            return r;
        }

        private static bool IsHelp(string args)
        {
            return args.Equals("help", StringComparison.OrdinalIgnoreCase)
                || args.Equals("-h", StringComparison.OrdinalIgnoreCase)
                || args.Equals("--help", StringComparison.OrdinalIgnoreCase)
                || args.Equals("?", StringComparison.Ordinal);
        }

        /// <summary>通道名只用于展示;/threads 的线程判据是 ctx.IsGameThread(契约 v1.1)。</summary>
        private static string ChannelText(SlashContext ctx)
        {
            if (ctx == null || string.IsNullOrEmpty(ctx.Channel)) return "(未接线)";
            return ctx.Channel;
        }

        private static string IdText(int id)
        {
            return id > 0 ? id.ToString(CultureInfo.InvariantCulture) : "?";
        }

        private static string ReadPriority(ProcessThread pt)
        {
            // PriorityLevel 只在 windows/linux/freebsd 上有实现(CA1416):按平台真值分支,不是消警告的空壳。
            if (OperatingSystem.IsWindows())
            {
                try { return pt.PriorityLevel.ToString(); }
                catch { }
            }
            try { return "base=" + pt.BasePriority.ToString(CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        // ---- Windows 原生查询(全部"失败即 null/-1",绝不抛) ----

        private static bool IsWindowsPlatform()
        {
            try { return RuntimeInformation.IsOSPlatform(OSPlatform.Windows); }
            catch { return false; }
        }

        private static int CurrentNativeThreadId()
        {
            if (!IsWindowsPlatform()) return -1;
            try { return unchecked((int)GetCurrentThreadId()); }
            catch { return -1; }
        }

        /// <summary>读线程描述(即 OS 层的线程名);取不到返回 null。</summary>
        private static string NativeThreadName(int threadId)
        {
            if (!IsWindowsPlatform() || threadId <= 0) return null;

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)threadId);
                if (handle == IntPtr.Zero) return null;

                IntPtr buffer = IntPtr.Zero;
                // 不判 HRESULT(见文件头实测注);buffer 预置 0 ⇒ 失败时不写回也会读到 0。
                GetThreadDescription(handle, out buffer);
                if (buffer == IntPtr.Zero) return null;
                try
                {
                    string name = Marshal.PtrToStringUni(buffer);
                    return string.IsNullOrEmpty(name) ? null : name;
                }
                finally { LocalFree(buffer); }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int GetThreadDescription(IntPtr threadHandle, out IntPtr description);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr handle);
    }
}
