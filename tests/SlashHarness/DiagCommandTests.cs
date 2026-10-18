// RoslynProbe —— /diag 的纯逻辑单测(W5)。
// 零依赖(不用 xunit):public static int Run() 返回失败数,由 SlashTestHarness 反射发现。
// 覆盖:Data 契约(pid/diagnosticPipe/runtimeDir/dotnetEnv)、DOTNET_* 过滤与"活"的环境变量、
// Lines 末行必须给出「连这个名字」的可行动作(规格硬要求)、参数拒绝路径,
// 以及**管道实测三个形状**的真管道端到端用例(CheckPipeProbeBranches)。
//
// 关于"钉住"的口径(避免过度声明):生产路径里那一行,在本进程只可能稳定命中"存在"分支
// (进程自己的诊断管道默认就在,且只有 DOTNET_EnableDiagnostics=0 才会没有),所以 e2e 只做形状白名单;
// 三个分支的**存在性**靠 DiagCommand.ProbeLine(pid) 这个接缝 + 自己造的**真命名管道**来钉:
// 生产路径与单测共用同一个 PipeProbeLine 函数,删掉任一分支本文件就会红。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using RoslynProbe;

namespace RoslynProbe.Tests
{
    /// <summary>/diag:诊断接驳点契约(pid / 管道名 / runtime 目录 / DOTNET_ 变量)。</summary>
    public static class DiagCommandTests
    {
        private const string ProbeFoundPrefix = "诊断管道实测: 存在 —— ";
        private const string ProbePrefixPrefix = "诊断管道实测: 未发现与 ";
        private const string ProbeMissingPrefix = "诊断管道实测: 未发现 —— ";

        private static int failures;

        public static int Run()
        {
            failures = 0;
            SlashRegistry.ResetForTests();
            try
            {
                DiagCommand.Register();

                SlashEntry entry;
                Check(SlashRegistry.TryGet("diag", out entry), "Register 后应能查到 verb \"diag\"");
                Check(entry != null && entry.Kind == "diag", "Kind 应为 \"diag\"");
                Check(entry != null && entry.Usage == "—— pid + 诊断管道名 + 运行时目录 + DOTNET_ 环境变量",
                    "Usage 应逐字符合规格,实际 " + Show(entry == null ? null : entry.Usage));
                Check(entry != null && entry.Handler != null, "Handler 不应为 null");

                SlashResult r = Dispatch("/diag");
                Check(r != null && r.Kind == "diag", "Kind 应为 \"diag\"");
                Check(DataBool(r, "ok"), "成功路径 Data[\"ok\"] 应为 true");
                Check(DataKeys(r, "ok", "pid", "diagnosticPipe", "runtimeDir", "dotnetEnv"),
                    "Data 键集应与冻结契约一致");

                int pid;
                Check(DataInt(r, "pid", out pid) && pid == Environment.ProcessId,
                    "pid 应为 Environment.ProcessId,实际 " + pid);
                string pipe = DataString(r, "diagnosticPipe");
                Check(pipe == "dotnet-diagnostic-" + pid,
                    "diagnosticPipe 应为 dotnet-diagnostic-<pid>,实际 " + Show(pipe));

                string runtimeDir = DataString(r, "runtimeDir");
                Check(!string.IsNullOrEmpty(runtimeDir), "runtimeDir 不应为空");
                Check(!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir),
                    "runtimeDir 应真实存在,实际 " + Show(runtimeDir));

                var env = Val(r, "dotnetEnv") as Dictionary<string, object>;
                Check(env != null, "dotnetEnv 应为 Dictionary<string,object>");
                if (env != null)
                    foreach (KeyValuePair<string, object> kv in env)
                        Check(kv.Key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase),
                            "dotnetEnv 只应收 DOTNET_* 键,实际 " + kv.Key);

                // 环境变量是活的:临时设一个,下一次调用必须看得见(证明不是硬编码)
                Environment.SetEnvironmentVariable("DOTNET_W5_DIAG_TEST", "1");
                try
                {
                    SlashResult r2 = Dispatch("/diag");
                    var env2 = r2 == null ? null : r2.Data == null ? null : r2.Data["dotnetEnv"] as Dictionary<string, object>;
                    Check(env2 != null && env2.ContainsKey("DOTNET_W5_DIAG_TEST"),
                        "dotnetEnv 应包含刚设置的 DOTNET_W5_DIAG_TEST");
                    Check(env2 != null && env2.ContainsKey("DOTNET_W5_DIAG_TEST")
                        && string.Equals(env2["DOTNET_W5_DIAG_TEST"] as string, "1", StringComparison.Ordinal),
                        "环境变量值应原样透传");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("DOTNET_W5_DIAG_TEST", null);
                }

                // 规格:Lines 末尾给一句「要用 dotnet-dump / dotnet-trace 就连这个名字」
                string last = r.Lines != null && r.Lines.Count > 0 ? r.Lines[r.Lines.Count - 1] : null;
                Check(last != null, "Lines 不应为空");
                Check(last != null && last.IndexOf("dotnet-dump", StringComparison.Ordinal) >= 0,
                    "末行应提到 dotnet-dump,实际 " + Show(last));
                Check(last != null && last.IndexOf("dotnet-trace", StringComparison.Ordinal) >= 0,
                    "末行应提到 dotnet-trace,实际 " + Show(last));
                Check(last != null && pipe != null && last.IndexOf(pipe, StringComparison.Ordinal) >= 0,
                    "末行应给出管道名 " + Show(pipe) + ",实际 " + Show(last));

                // 生产路径那一行只做**形状白名单**:不许出现三种约定以外的第四种形状。
                // (分支存在性另测;本机 e2e 稳定命中的是"存在"分支。)
                bool hasPipeLine = false;
                if (r.Lines != null)
                    foreach (string l in r.Lines)
                    {
                        if (l.StartsWith(ProbeFoundPrefix, StringComparison.Ordinal)
                            || l.StartsWith(ProbePrefixPrefix, StringComparison.Ordinal)
                            || l.StartsWith(ProbeMissingPrefix, StringComparison.Ordinal))
                        { hasPipeLine = true; break; }
                    }
                Check(hasPipeLine, "诊断管道实测行应是三种约定形状之一(存在 / 未发现+同前缀提示 / 未发现)");

                Check(typeof(DiagCommand).GetMethod("Provide", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new Type[] { typeof(string), typeof(SlashContext) }, null) != null,
                    "纯 BCL 指令的 handler 应叫 Provide(契约 v1.1 §3.5)");

                // 参数拒绝路径
                SlashResult u = Dispatch("/diag 1");
                Check(u != null && !DataBool(u, "ok"), "/diag 带参数应返回用法结果(ok=false)");
                Check(u != null && u.Lines.Count > 0 && u.Lines[0].IndexOf("/diag", StringComparison.Ordinal) == 0,
                    "用法结果首行应点名 /diag,实际 " + Show(u == null || u.Lines.Count == 0 ? null : u.Lines[0]));
                SlashResult h = Dispatch("/diag help");
                Check(h != null && h.Lines.Count > 0, "/diag help 应给用法");

                // 三个形状的**真管道**用例(Windows;Unix 是 TMP 套接字文件,语义不同,不在本套件覆盖范围内)
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) CheckPipeProbeBranches();
            }
            finally
            {
                SlashRegistry.ResetForTests();
                SlashRegistry.RegisterAll();
            }

            Console.WriteLine("DiagCommandTests: " + (failures == 0 ? "全通过" : failures + " 失败"));
            return failures;
        }

        /// <summary>
        /// 三个形状都用**自己造的真命名管道**端到端钉住(MUT-E 的正面回答:删掉第三分支 ⇒ 本用例红):
        ///   形状②什么都没有:一个"假 pid"(先自证该 pid 下没有任何管道)⇒ 必须给「未发现 —— 」
        ///   形状①精确同名:为另一个假 pid 建 <c>dotnet-diagnostic-&lt;pid&gt;</c> ⇒ 必须给「存在 —— &lt;path&gt;」
        ///   形状③只有同前缀变体:为第三个假 pid 建两个 <c>dotnet-diagnostic-&lt;pid&gt;-&lt;key&gt;-socket</c>
        ///     (模拟将来 .NET 的命名变体)⇒ 必须给「未发现与 … 完全同名 … 同前缀 2 个 …,例如 …」
        /// 用"假 pid"(真实 Windows pid 远小于 10^9,且先自证无管道)才能稳定复现"精确同名缺失 + 变体存在"。
        /// </summary>
        private static void CheckPipeProbeBranches()
        {
            var pipes = new List<NamedPipeServerStream>();
            try
            {
                int emptyPid = FindFreePid(-1);
                int exactPid = FindFreePid(emptyPid);
                int prefixPid = FindFreePid(exactPid);
                Check(emptyPid > 0 && exactPid > 0 && prefixPid > 0 && emptyPid != exactPid && exactPid != prefixPid,
                    "应能找到三个互不相同、且当前没有任何同名/同前缀管道的假 pid");
                if (emptyPid <= 0 || exactPid <= 0 || prefixPid <= 0) return;

                // 形状②:什么都没有
                string line2 = DiagCommand.ProbeLine(emptyPid);
                Check(line2.StartsWith(ProbeMissingPrefix, StringComparison.Ordinal),
                    "没有任何同名/同前缀管道时应给「未发现 —— 」形状,实际 " + line2);

                // 形状①:精确同名(自己建一个)
                string exactName = "dotnet-diagnostic-" + exactPid.ToString(CultureInfo.InvariantCulture);
                pipes.Add(new NamedPipeServerStream(exactName, PipeDirection.InOut));
                string line1 = DiagCommand.ProbeLine(exactPid);
                Check(line1.StartsWith(ProbeFoundPrefix, StringComparison.Ordinal)
                    && line1.IndexOf(exactName, StringComparison.Ordinal) >= 0,
                    "精确同名管道存在时应给「存在 —— <path>」形状并点名该管道,实际 " + line1);

                // 形状③:只有同前缀变体(模拟 -<key>-socket 命名)
                string baseName = "dotnet-diagnostic-" + prefixPid.ToString(CultureInfo.InvariantCulture);
                pipes.Add(new NamedPipeServerStream(baseName + "-deadbeef1-socket", PipeDirection.InOut));
                pipes.Add(new NamedPipeServerStream(baseName + "-deadbeef2-socket", PipeDirection.InOut));
                string line3 = DiagCommand.ProbeLine(prefixPid);
                Check(line3.StartsWith(ProbePrefixPrefix, StringComparison.Ordinal),
                    "只有同前缀变体、没有精确同名时应给第三种形状,实际 " + line3);
                Check(line3.IndexOf(baseName, StringComparison.Ordinal) >= 0,
                    "第三形状应点名被探测的管道名 " + baseName + ",实际 " + line3);
                Check(line3.IndexOf(" 2 个", StringComparison.Ordinal) >= 0,
                    "第三形状应给出同前缀条数 2,实际 " + line3);
                Check(line3.IndexOf("deadbeef1-socket", StringComparison.Ordinal) >= 0,
                    "第三形状应给出同前缀样例,实际 " + line3);
                Check(line3.IndexOf(ProbeFoundPrefix, StringComparison.Ordinal) < 0,
                    "有变体但无精确同名时不得报「存在」,实际 " + line3);

                // 形状③之后再补一个精确同名 ⇒ 必须回到形状①(优先级正确)
                pipes.Add(new NamedPipeServerStream(baseName, PipeDirection.InOut));
                string line3b = DiagCommand.ProbeLine(prefixPid);
                Check(line3b.StartsWith(ProbeFoundPrefix, StringComparison.Ordinal),
                    "同前缀变体与精确同名并存时,应优先报「存在」,实际 " + line3b);
            }
            catch (Exception ex)
            {
                Check(false, "管道三形状用例自身出错(环境可能不允许建命名管道): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                foreach (NamedPipeServerStream p in pipes)
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }

        /// <summary>找一个"当前没有任何 dotnet-diagnostic-&lt;pid&gt;* 管道"的假 pid(远大于真实 pid 取值范围)。</summary>
        private static int FindFreePid(int exclude)
        {
            for (int i = 0; i < 64; i++)
            {
                int candidate = 900000000 + (i * 7919) + (Environment.TickCount & 0xFF);
                if (candidate == exclude) continue;
                string line = DiagCommand.ProbeLine(candidate);
                if (line.StartsWith(ProbeMissingPrefix, StringComparison.Ordinal)) return candidate;
            }
            return -1;
        }

        // ---------------- 断言与取值助手 ----------------

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

        private static bool DataInt(SlashResult r, string key, out int value)
        {
            value = 0;
            object v = Val(r, key);
            if (v is int) { value = (int)v; return true; }
            Check(false, "Data[" + key + "] 应为 int,实际 " + TypeName(v));
            return false;
        }

        private static string DataString(SlashResult r, string key)
        {
            object v = Val(r, key);
            Check(v is string, "Data[" + key + "] 应为 string,实际 " + TypeName(v));
            return v as string;
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
