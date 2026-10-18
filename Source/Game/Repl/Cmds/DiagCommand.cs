// RoslynProbe —— /diag:pid + 诊断管道名 + 运行时目录 + DOTNET_ 环境变量(W5,纯 BCL)。
//
// 这是给"进程外调试工具"的接驳点:dotnet-dump / dotnet-trace / dotnet-counters 按 pid 认 .NET 进程,
// 靠的就是诊断管道(Windows: \\.\pipe\dotnet-diagnostic-<pid> —— 实测 .NET 10 就是这个裸名字;
// Unix: <TMP>/dotnet-diagnostic-<pid>-socket)。
// 本指令**只查不连**:Windows 列 \\.\pipe\ 找同名管道,Unix 看 TMP 下同名文件 —— 不连接、不 spawn 进程。
//   名字用精确同名匹配;但若将来 .NET 改成带 -<key>-socket 后缀的变体名,精确匹配会**静默假阴性** ——
//   所以匹配失败时还要数一遍同前缀(dotnet-diagnostic-<pid>-)的条数并在 Lines 里点名。
// DOTNET_EnableDiagnostics=0 会让管道根本不存在,所以 DOTNET_* 启动期变量原样列出并单独告警。
//
// 可测性:三个形状的唯一生成点是 <see cref="PipeProbeLine"/>,按 pid 走完整流程的接缝是
// <see cref="ProbeLine"/> —— 生产路径与单测共用同一个函数,所以三条分支都能用**真管道**端到端钉住
// (否则生产路径永远只命中第一分支,第三分支等于没被测过)。
//
// 只读 ⇒ 不需要 Executor marshal;本文件绝不碰 ConsoleLog / Unity / Verse。
//
// Data 键(小驼峰、稳定,工具消费方依赖):pid(int),diagnosticPipe,runtimeDir,dotnetEnv{...}
// (ok 是全局成功位,与 SlashResults.Error 的 ok=false 对称)。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace RoslynProbe
{
    /// <summary>/diag —— pid / 诊断管道名 / 运行时目录 / DOTNET_ 环境变量(纯 BCL;无参数)。</summary>
    public static class DiagCommand
    {
        /// <summary>RegisterAll 反射调用:public static void Register(),无参、返回 void。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = "diag",
                Usage = "—— pid + 诊断管道名 + 运行时目录 + DOTNET_ 环境变量",
                Group = "运行时",
                Kind = "diag",
                Handler = Provide
            });
        }

        /// <summary>指令实现(契约 v1.1 §3.5:纯 BCL 指令的 handler 也叫 Provide)。</summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            string args = (rest ?? "").Trim();
            if (args.Length > 0 && IsHelp(args)) return Usage(null);
            if (args.Length > 0) return Usage("不接受参数: \"" + args + "\"(/diag 无参数)");

            try
            {
                int pid = Environment.ProcessId;
                string pidText = pid.ToString(CultureInfo.InvariantCulture);
                string pipeName = "dotnet-diagnostic-" + pidText;
                string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
                int envCount;
                Dictionary<string, object> dotnetEnv = ReadDotnetEnv(out envCount);

                var r = new SlashResult { Kind = "diag" };
                r.Data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["pid"] = pid,
                    ["diagnosticPipe"] = pipeName,
                    ["runtimeDir"] = runtimeDir,
                    ["dotnetEnv"] = dotnetEnv
                };

                r.Lines.Add("pid: " + pidText);
                r.Lines.Add("诊断管道名: " + pipeName);
                r.Lines.Add("运行时目录: " + runtimeDir);
                r.Lines.Add(ProbeLine(pid));
                if (IsDiagnosticsDisabled(dotnetEnv))
                    r.Lines.Add("⚠ DOTNET_EnableDiagnostics=0 —— 诊断服务器已关,dotnet-dump / dotnet-trace 连不上,管道不会存在。");
                r.Lines.Add("DOTNET_ 环境变量: " + envCount.ToString(CultureInfo.InvariantCulture) + " 个");
                foreach (KeyValuePair<string, object> kv in dotnetEnv)
                    r.Lines.Add("  " + kv.Key + " = " + (kv.Value == null ? "" : kv.Value.ToString()));
                r.Lines.Add("示例: dotnet-dump collect -p " + pidText + " / dotnet-trace collect -p " + pidText);
                r.Lines.Add("要用 dotnet-dump / dotnet-trace 就连这个名字:" + pipeName);
                return r;
            }
            catch (Exception ex)
            {
                return SlashResults.Error("diag", "诊断信息读取失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// "只查不连"的完整一步(按给定 pid)并渲染成 Lines 里那一行 —— 生产路径(<see cref="Provide"/>)
        /// 与单测共用这个接缝,所以三个形状都能用真管道端到端验证。
        /// </summary>
        public static string ProbeLine(int pid)
        {
            string pipeName = "dotnet-diagnostic-" + pid.ToString(CultureInfo.InvariantCulture);
            int prefixCount;
            string prefixSample;
            string pipePath = FindDiagnosticPipe(pid, out prefixCount, out prefixSample);
            return PipeProbeLine(pipeName, pipePath, prefixCount, prefixSample);
        }

        /// <summary>
        /// 三个形状的唯一生成点:① 精确同名存在;② 只有同前缀变体(将来 -&lt;key&gt;-socket 命名);
        /// ③ 什么都没有。删掉任一分支都会让 DiagCommandTests 红。
        /// </summary>
        public static string PipeProbeLine(string pipeName, string pipePath, int prefixCount, string prefixSample)
        {
            if (!string.IsNullOrEmpty(pipePath))
                return "诊断管道实测: 存在 —— " + pipePath;
            if (prefixCount > 0)
                return "诊断管道实测: 未发现与 " + pipeName + " 完全同名的管道;但检测到**同前缀** "
                    + prefixCount.ToString(CultureInfo.InvariantCulture)
                    + " 个(可能带 -<key>-socket 后缀变体),例如 " + prefixSample
                    + " —— 若工具连不上,请以这些实际名字为准";
            return "诊断管道实测: 未发现 —— 诊断服务器可能没起来(见下面的 DOTNET_EnableDiagnostics)";
        }

        private static SlashResult Usage(string problem)
        {
            SlashResult r = SlashResults.Usage("diag",
                "用法: /diag —— 打印 pid、诊断管道名 dotnet-diagnostic-<pid>、运行时目录与 DOTNET_* 启动期环境变量(无参数)",
                "Data: pid / diagnosticPipe / runtimeDir / dotnetEnv");
            if (!string.IsNullOrEmpty(problem)) r.Lines.Insert(0, "/diag " + problem);
            return r;
        }

        private static bool IsHelp(string args)
        {
            return args.Equals("help", StringComparison.OrdinalIgnoreCase)
                || args.Equals("-h", StringComparison.OrdinalIgnoreCase)
                || args.Equals("--help", StringComparison.OrdinalIgnoreCase)
                || args.Equals("?", StringComparison.Ordinal);
        }

        /// <summary>DOTNET_* 启动期环境变量,键排序(不变文化/大小写不敏感)后按序放进字典 ⇒ Lines 顺序稳定。</summary>
        private static Dictionary<string, object> ReadDotnetEnv(out int count)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
                {
                    string key = e.Key as string;
                    if (key == null) continue;
                    if (!key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)) continue;
                    string value = e.Value as string;
                    if (value == null && e.Value != null) value = e.Value.ToString();
                    pairs.Add(new KeyValuePair<string, string>(key, value ?? ""));
                }
            }
            catch { }

            pairs.Sort(delegate (KeyValuePair<string, string> a, KeyValuePair<string, string> b)
            {
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });

            var result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, string> p in pairs) result[p.Key] = p.Value;
            count = result.Count;
            return result;
        }

        private static bool IsDiagnosticsDisabled(Dictionary<string, object> dotnetEnv)
        {
            if (dotnetEnv == null) return false;
            foreach (KeyValuePair<string, object> kv in dotnetEnv)
            {
                if (!string.Equals(kv.Key, "DOTNET_EnableDiagnostics", StringComparison.OrdinalIgnoreCase)) continue;
                string value = kv.Value == null ? "" : kv.Value.ToString().Trim();
                return string.Equals(value, "0", StringComparison.Ordinal);
            }
            return false;
        }

        /// <summary>
        /// 只查不连:返回真实存在的诊断管道路径(Windows)或套接字路径(Unix),没有就 null(绝不抛);
        /// 同时输出"同前缀但不是完全同名"的条数与样例,避免名字变体导致**静默假阴性**。
        /// </summary>
        private static string FindDiagnosticPipe(int pid, out int prefixCount, out string prefixSample)
        {
            string expected = "dotnet-diagnostic-" + pid.ToString(CultureInfo.InvariantCulture);
            string prefix = expected + "-";
            prefixCount = 0;
            prefixSample = null;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    foreach (string path in Directory.GetFiles("\\\\.\\pipe\\"))
                    {
                        string leaf = Path.GetFileName(path);
                        if (string.Equals(leaf, expected, StringComparison.OrdinalIgnoreCase)) return path;
                        if (leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            prefixCount++;
                            if (prefixSample == null) prefixSample = path;
                        }
                    }
                    return null;
                }

                string socket = Path.Combine(Path.GetTempPath(), expected + "-socket");
                if (File.Exists(socket)) return socket;
                try
                {
                    foreach (string path in Directory.GetFiles(Path.GetTempPath(), prefix + "*"))
                    {
                        prefixCount++;
                        if (prefixSample == null) prefixSample = path;
                    }
                }
                catch { }
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
