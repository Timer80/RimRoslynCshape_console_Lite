// RoslynProbe —— Roslyn 求值引擎（官方 Scripting 实现）。
//
// 后端为微软官方 Roslyn Scripting API（Microsoft.CodeAnalysis.Scripting + CSharp.Scripting，
// 随 UE.Roslyn 载荷分发，ReplHost 在 gate 通过后 LoadFrom）。submission 语义（变量/方法/类
// 跨提交持久化、末尾表达式返回值、using 持久化）由官方实现——替换初版手写 AST 上提管线，
// 旧实现完整备份在 RoslynEngine.HandRolled.cs.bak（不参与编译）。
//
// 红线继续成立：
//   * 本文件是唯一允许出现 Microsoft.CodeAnalysis typeref 的源文件；
//   * 不设任何 Roslyn 类型的静态字段（字段扫描 ReportProbablyMissingAttributes 必须可过）；
//   * 本类仅在 ReplHost.EnsureGate 通过后实例化（JIT 顺序安全），且两件 Scripting 程序集
//     已由 ReplHost.EnsureScriptingAssemblies 装载。
//
// 线程契约：Eval 只能经 Executor 在游戏更新线程上调用。用户代码本体是官方引擎的同步委托
// 调用（离线探针已验证 caller/script 线程一致，INLINE），阻塞等待期间清空
// SynchronizationContext 防内部延续被排进游戏同步队列造成死锁。LastEvalCallerThreadId 供
// 真机烟测自证（/eval Thread.CurrentThread.ManagedThreadId 应与本字段相等）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using RoslynProbe.Repl;

namespace RoslynProbe.Repl
{
    public sealed class RoslynEngine
    {
        private static readonly string[] DefaultUsings =
        {
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "System.Text",
            "System.Threading.Tasks",
            "UnityEngine",
            "Verse",
            "RimWorld",
        };

        private readonly object stateLock = new object();

        private ScriptState state;
        private InteractiveAssemblyLoader loader;
        private readonly List<string> usings = new List<string>();
        // WithImports 承载不了的"源码前导"条目：别名（SB = A.B）与静态 using（static A.B）。
        // 每次提交把它们拼成 `using <d>;` 前导重新发射；为空时源码一字不改（默认路径零影响）。
        private readonly List<string> directives = new List<string>();
        private MetadataReference[] references;
        private readonly ReplInteractiveBase globals = new ReplInteractiveBase();

        // ---- 自动补 using（T5）----
        // 短名 → 命名空间列表。懒构建 + 缓存：首次编译失败时构建，Reset 时失效重建（程序集面只在 Reset 变）。
        // 构建在锁外（快照模式，参照 AddUsings）；索引只读已加载程序集，与 BuildReferences 同源（NOTE-reference-surface.md）。
        private Dictionary<string, List<string>> typeIndex;
        private static readonly char[] SpaceSeparators = { ' ', '\t' };
        /// <summary>单次提交自动补上限（计划 §0.4：防"补一条又暴露一条"抖动）。</summary>
        private const int MaxAutoUsings = 5;

        /// <summary>最近一次 Eval 时调用方线程 id（游戏更新线程自证用）。</summary>
        public int LastEvalCallerThreadId { get; private set; }

        public RoslynEngine()
        {
            lock (stateLock)
            {
                usings.AddRange(DefaultUsings);
                ReplInteractiveBase.Bridge.VariablesDump = DumpVariables;
                ReplInteractiveBase.Bridge.UsingsDump = DumpUsings;
                references = BuildReferences();
                loader = new InteractiveAssemblyLoader();
            }
        }

        /// <summary>
        /// 校验并落盘一批 using 候选。按形态分流：
        ///   * 不含 '=' 且不以 "static " 开头 → 普通命名空间，走 WithImports（usings）；
        ///   * 含 '='（别名 SB = A.B）或以 "static " 开头 → 走源码前导（directives）。
        /// 落盘前用同一编译器 + 同一份 ScriptOptions 试编译（只 Compile 不 Run）：不可用就不落盘。
        /// 边界：校验只管"这条 using 会不会污染之后的每一次编译"，不管它是否让某个名字变歧义
        /// （歧义是每次提交的事）；任何异常一律视为校验失败，宁可拒绝不可污染。
        /// </summary>
        public UsingAddReport AddUsings(IEnumerable<string> items)
        {
            var report = new UsingAddReport();

            // 第一段：只短暂持锁做「规范化 + 按当前状态去重 + 取快照」，**不在锁内编译**。
            // 本方法可能跑在 HTTP 后台线程（/using），而游戏线程的 Eval / DumpUsings 也抢 stateLock；
            // 持锁编译会让游戏线程白等一次 Compile。故编译段放到锁外，用这里的快照。
            var pending = new List<KeyValuePair<string, bool>>();
            List<string> importSnapshot;
            MetadataReference[] refSnapshot;
            lock (stateLock)
            {
                importSnapshot = new List<string>(usings);
                refSnapshot = references;
                foreach (string raw in items ?? Array.Empty<string>())
                {
                    string u = NormalizeUsing(raw);
                    if (u.Length == 0) continue;
                    bool isDirective = u.IndexOf('=') >= 0 || u.StartsWith("static ", StringComparison.Ordinal);
                    if (isDirective ? directives.Contains(u) : usings.Contains(u)) { report.Accepted++; continue; }
                    bool inBatch = false;
                    foreach (var q in pending) if (q.Key == u) { inBatch = true; break; }
                    if (inBatch) { report.Accepted++; continue; }
                    pending.Add(new KeyValuePair<string, bool>(u, isDirective));
                }
            }

            // 第二段：**锁外**试编译（只 Compile 不 Run）。快照保证不与其他线程的 List 变更竞争。
            var accepted = new List<KeyValuePair<string, bool>>();
            foreach (var q in pending)
            {
                string err;
                bool valid = q.Value
                    ? ValidateDirective(q.Key, importSnapshot, refSnapshot, out err)
                    : ValidateNamespaceImport(q.Key, importSnapshot, refSnapshot, out err);
                if (!valid)
                {
                    report.Rejected.Add(new UsingRejection(q.Key,
                        (q.Value ? "无法解析的 using 指令: " : "无法解析的命名空间: ") + q.Key + "（" + err + "）"));
                    continue;
                }
                accepted.Add(q);
            }

            // 第三段：重新持锁落盘，并**再次查重**（编译期间可能有并发加入）。
            if (accepted.Count > 0)
            {
                lock (stateLock)
                {
                    foreach (var q in accepted)
                    {
                        if (q.Value)
                        {
                            if (directives.Contains(q.Key)) { report.Accepted++; continue; }
                            directives.Add(q.Key);
                        }
                        else
                        {
                            if (usings.Contains(q.Key)) { report.Accepted++; continue; }
                            usings.Add(q.Key);
                        }
                        report.Added++;
                        report.Accepted++;
                    }
                }
            }
            return report;
        }

        /// <summary>
        /// 清空 submission 链（变量/类/方法不再跨提交保留）。
        /// keepUsings=false（默认）时把 usings 一并重置回 DefaultUsings —— 否则一条不可用的 using
        /// （不存在的命名空间，或别名形如 SB = A.B，WithImports 只接受命名空间名）会永久污染之后的每次编译。
        /// keepUsings=true 时保留当前 usings（旧行为）。
        /// </summary>
        public void Reset(bool keepUsings = false)
        {
            lock (stateLock)
            {
                state = null;
                loader?.Dispose();
                loader = new InteractiveAssemblyLoader();
                references = BuildReferences();
                typeIndex = null; // 程序集面变了 → 索引失效，下次编译失败时重建
                if (!keepUsings)
                {
                    usings.Clear();
                    usings.AddRange(DefaultUsings);
                    directives.Clear();
                }
            }
        }

        public string DumpUsings()
        {
            lock (stateLock)
            {
                var sb = new StringBuilder();
                foreach (string u in usings)
                    sb.AppendLine("using " + u + ";");
                // directives 里的条目按定义就是 "using SB = A.B;" / "using static A.B;" 的形状，同一模板即可。
                foreach (string d in directives)
                    sb.AppendLine("using " + d + ";");
                return sb.ToString().TrimEnd();
            }
        }

        public string DumpVariables()
        {
            lock (stateLock)
            {
                if (state == null) return "(尚无持久化变量)";
                var sb = new StringBuilder();
                foreach (ScriptVariable v in state.Variables)
                {
                    if (v.Name != null && v.Name.Length > 0 && v.Name[0] == '<') continue; // 编译器生成
                    sb.Append(v.Type.Name).Append(' ').Append(v.Name).Append(" = ").AppendLine(FormatValue(v.Value));
                }
                return sb.Length == 0 ? "(尚无持久化变量)" : sb.ToString().TrimEnd();
            }
        }

        public ReplResult Eval(string code)
        {
            LastEvalCallerThreadId = Thread.CurrentThread.ManagedThreadId;

            string trimmed = (code ?? "").Trim();
            if (trimmed.Length == 0) return ReplResult.Success(null);

            // 纯 using 输入：持久化进会话，不产生 submission（对齐 Mono.Evaluator 行为）。
            string[] lines = trimmed.Split('\n');
            var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
            bool allUsing = nonEmpty.Count > 0 && nonEmpty.All(l => l.TrimStart().StartsWith("using ", StringComparison.Ordinal));
            if (allUsing)
            {
                // 减号前缀（T3）：using -X; / using -X = Y;（可多行混写）→ RemoveUsings。与加号路径互不干扰。
                if (nonEmpty.Any(l => l.TrimStart().StartsWith("using -", StringComparison.Ordinal)))
                {
                    var toRemove = new List<string>();
                    var bad = new List<string>();
                    foreach (string l in nonEmpty)
                    {
                        string t = l.Trim();
                        if (t.StartsWith("using -", StringComparison.Ordinal))
                            toRemove.Add(t.Substring(7)); // 去掉 "using -"，余下交给 RemoveUsings 规范化
                        else
                            bad.Add(t); // 混写了加号条目：按"要么删要么加"不猜，整条点名拒绝
                    }
                    if (bad.Count > 0)
                        return ReplResult.Failure("减号前缀提交里混入非删除条目（请分开提交）: " + string.Join("; ", bad));
                    UsingRemoveReport rep = RemoveUsings(toRemove);
                    var sb = new StringBuilder();
                    sb.Append("已移除 ").Append(rep.Removed.Count).Append(" 条");
                    if (rep.Denied.Count > 0) sb.Append(";拒绝(DefaultUsings 不可删): ").Append(string.Join(", ", rep.Denied));
                    if (rep.NotFound.Count > 0) sb.Append(";未找到: ").Append(string.Join(", ", rep.NotFound));
                    return ReplResult.Success(sb.ToString());
                }

                UsingAddReport add = AddUsings(lines);
                if (add.Rejected.Count > 0)
                {
                    var msgs = new List<string>();
                    foreach (UsingRejection rej in add.Rejected) msgs.Add(rej.Error);
                    return ReplResult.Failure(string.Join("\n", msgs));
                }
                return ReplResult.Success("usings 已持久化");
            }

            lock (stateLock)
            {
                ScriptOptions options = ScriptOptions.Default
                    .WithReferences(references)
                    .WithImports(usings)
                    .WithAllowUnsafe(true);

                // 别名 / 静态 using 无法经 WithImports 承载（它只接受命名空间名），改在每次提交的源码
                // 前导里重新发射；directives 为空时不改一个字（默认路径与改动前逐字相同）。
                // 代价：有别名时诊断行号整体下移 N 行（N = 别名条数），用例集本身也注明行号不可依赖。
                string source = directives.Count == 0
                    ? trimmed
                    : string.Join("\n", directives.ConvertAll(d => "using " + d + ";")) + "\n" + trimmed;

                ScriptState newState;
                try
                {
                    SynchronizationContext prev = SynchronizationContext.Current;
                    SynchronizationContext.SetSynchronizationContext(null);
                    try
                    {
                        newState = state == null
                            ? CSharpScript.Create(source, options, typeof(ReplInteractiveBase), loader)
                                .RunAsync(globals).GetAwaiter().GetResult()
                            : state.ContinueWithAsync(source, options).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(prev);
                    }
                }
                catch (CompilationErrorException cee)
                {
                    // ---- 自动补 using（T5）---- 绝不写回 usings 持久表：候选只进本次的 options 临时副本
                    // （WithImports(usings.Concat(autos))），"仅本次" = 从不存，不是"用完删"。
                    // 显式优先：该提交 usings 表里已有的命名空间本就在 options 里，索引候选与它重复时
                    // 重试与不重试等价（重复 import 同一命名空间合法），故无需再滤。
                    Diagnostic[] origDiags = cee.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
                    List<string> autos = CollectAutoUsingCandidates(origDiags);
                    if (autos.Count == 0 && (pendingAmbiguous != null && pendingAmbiguous.Count > 0
                        || pendingZero != null && pendingZero.Count > 0
                        || pendingOverflow != null))
                    {
                        // 多候选/零候选/超限：不注入也不重试，但候选说明必须附在原始诊断后（计划 §0.3）。
                        return AutoRetryRejected(origDiags);
                    }
                    if (autos.Count > 0)
                    {
                        ScriptOptions retryOptions = options.WithImports(usings.Concat(autos));
                        try
                        {
                            SynchronizationContext prev2 = SynchronizationContext.Current;
                            SynchronizationContext.SetSynchronizationContext(null);
                            ScriptState retryState;
                            try
                            {
                                retryState = state == null
                                    ? CSharpScript.Create(source, retryOptions, typeof(ReplInteractiveBase), loader)
                                        .RunAsync(globals).GetAwaiter().GetResult()
                                    : state.ContinueWithAsync(source, retryOptions).GetAwaiter().GetResult();
                            }
                            finally
                            {
                                SynchronizationContext.SetSynchronizationContext(prev2);
                            }

                            // 净收益判据在 CatchAutousRetryError 里裁决；失败回滚返回原始诊断。
                            newState = retryState;
                        }
                        catch (CompilationErrorException)
                        {
                            // 净收益判据：重试仍编译失败 → 错误数未严格减少 → 回滚，返回原始诊断（autoUsing 为空）。
                            pendingAmbiguous = null;
                            pendingZero = null;
                            pendingOverflow = null;
                            return AutoRetryRejected(origDiags);
                        }
                        catch (Exception retryEx)
                        {
                            // 重试编译过了但运行炸了：比"编译都过不了"严格更糟？不 —— 错误形态变了，
                            // 但用户拿到了真实运行时错误，仍视为净收益（原始是编译错，现在是运行错）。
                            Exception inner2 = retryEx is TargetInvocationException t2 && t2.InnerException != null ? t2.InnerException : retryEx;
                            var res = ReplResult.Failure("运行时异常: " + inner2.GetType().Name + ": " + inner2.Message + "\n" + FormatFrames(inner2));
                            res.AutoUsings = autos;
                            return res;
                        }

                        pendingAmbiguous = null;
                        pendingZero = null;
                        pendingOverflow = null;
                        state = newState;
                        var ok = ReplResult.Success(FormatValue(state.ReturnValue));
                        ok.AutoUsings = autos;
                        return ok;
                    }

                    return ReplResult.Failure(FormatDiags(cee.Diagnostics));
                }
                catch (Exception ex)
                {
                    Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    return ReplResult.Failure("运行时异常: " + inner.GetType().Name + ": " + inner.Message + "\n" + FormatFrames(inner));
                }

                state = newState;
                return ReplResult.Success(FormatValue(state.ReturnValue));
            }
        }

        /// <summary>
        /// T2 内核：规范化后同时从 usings / directives 删；命中 DefaultUsings → 拒绝并点名。
        /// 纯内存操作，锁内完成（无编译）。返回 removed / notFound / denied 三张清单。
        /// </summary>
        public UsingRemoveReport RemoveUsings(IEnumerable<string> items)
        {
            var report = new UsingRemoveReport();
            lock (stateLock)
            {
                foreach (string raw in items ?? Array.Empty<string>())
                {
                    // "using -X;" 分支已剥前缀；这里再兜一层 NormalizeUsing（兼容 "using X;" 原文）。
                    string u = NormalizeUsing(raw);
                    if (u.Length == 0) continue;
                    if (DefaultUsings.Contains(u))
                    {
                        report.Denied.Add(u);
                        continue;
                    }
                    if (usings.Contains(u)) { usings.Remove(u); report.Removed.Add(u); continue; }
                    if (directives.Contains(u)) { directives.Remove(u); report.Removed.Add(u); continue; }
                    report.NotFound.Add(u);
                }
            }
            return report;
        }

        /// <summary>T1 数据源：两张表的只读快照（含 DefaultUsings 名单，供 /usings 标注）。</summary>
        public UsingsSnapshot SnapshotUsings()
        {
            lock (stateLock)
            {
                var snap = new UsingsSnapshot();
                snap.Usings.AddRange(usings);
                snap.Directives.AddRange(directives);
                snap.Defaults.AddRange(DefaultUsings);
                return snap;
            }
        }

        // ---- 自动补 using（T5）----

        /// <summary>
        /// 从编译错误里解析被点名的类型短名（CS0246/CS0103），查短名索引：唯一候选收集为自动补候选
        /// （≤ MaxAutoUsings 条）；多候选不注入（原始诊断后附候选）；零候选原样返回。跨方法暂存字段
        /// pendingAmbiguous / pendingZero / pendingOverflow 在 Eval 的游戏更新线程上单线程使用。
        /// </summary>
        private List<string> CollectAutoUsingCandidates(Diagnostic[] origDiags)
        {
            var missing = new List<string>();
            foreach (Diagnostic d in origDiags)
            {
                if (d.Id != "CS0246" && d.Id != "CS0103") continue;
                string name = ExtractMissingName(d.GetMessage());
                if (name == null || name.Length == 0) continue;
                if (!missing.Contains(name)) missing.Add(name);
            }
            pendingAmbiguous = null;
            pendingZero = null;
            pendingOverflow = null;
            if (missing.Count == 0) return new List<string>();

            Dictionary<string, List<string>> index = EnsureTypeIndex();

            var autos = new List<string>();
            var ambiguous = new List<KeyValuePair<string, List<string>>>();
            var zero = new List<string>();
            foreach (string shortName in missing)
            {
                if (!index.TryGetValue(shortName, out List<string> nss) || nss.Count == 0) { zero.Add(shortName); continue; }
                if (nss.Count > 1) { ambiguous.Add(new KeyValuePair<string, List<string>>(shortName, nss)); continue; }
                if (!autos.Contains(nss[0])) autos.Add(nss[0]);
            }

            pendingAmbiguous = ambiguous;
            pendingZero = zero;
            if (autos.Count > MaxAutoUsings)
            {
                // 超限：一条都不注入，在原始诊断后列出全部缺失短名。
                pendingOverflow = autos;
                return new List<string>();
            }
            return autos;
        }

        /// <summary>
        /// CS0246/CS0103 消息里被点名的标识符（最后一个单引号包裹段；排除带点、含**任意空白**的形态 ——
        /// C# 标识符不可能含空白，出现空白说明抠出来的不是标识符）。internal 而非 private：SlashHarness
        /// 把 Source 与用例编进同一程序集，这条判定要能被离线用例直接钉住（含 TAB/全角/NBSP 的拒绝行为）。
        /// </summary>
        internal static string ExtractMissingName(string message)
        {
            int last = message.LastIndexOf('\'');
            if (last <= 0) return null;
            int first = message.LastIndexOf('\'', last - 1);
            if (first < 0) return null;
            string name = message.Substring(first + 1, last - first - 1);
            // 「不得含空白」= 任意空白（与内核/指令的分隔空白同一口径：SlashRegistry.IndexOfWhitespace）。
            if (name.Length == 0 || name.IndexOf('.') >= 0 || SlashRegistry.IndexOfWhitespace(name) >= 0) return null;
            return name;
        }

        /// <summary>
        /// 短名 → 命名空间列表索引：懒构建 + 缓存。遍历已加载程序集（GetExportedTypes，异常程序集跳过），
        /// 泛型去掉 \\`11 后缀；构建在锁外（快照模式），Reset 时失效重建。与 BuildReferences 同源：只读已加载程序集。
        /// </summary>
        private Dictionary<string, List<string>> EnsureTypeIndex()
        {
            var existing = typeIndex;
            if (existing != null) return existing;

            var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            Assembly[] assemblies;
            lock (stateLock)
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly asm in assemblies)
            {
                if (asm.IsDynamic) continue;
                Type[] types;
                try { types = asm.GetExportedTypes(); }
                catch { continue; }
                foreach (Type t in types)
                {
                    if (string.IsNullOrEmpty(t.Namespace)) continue;
                    string name = t.Name;
                    int tick = name.IndexOf('`');
                    if (tick > 0) name = name.Substring(0, tick);
                    if (!index.TryGetValue(name, out List<string> list))
                        index[name] = list = new List<string>();
                    if (!list.Contains(t.Namespace)) list.Add(t.Namespace);
                }
            }

            var raced = typeIndex;
            if (raced != null) return raced;
            typeIndex = index;
            return index;
        }

        /// <summary>
        /// 重试仍失败（或多候选/超限/零候选）时的响应：返回**原始诊断**，其后附候选说明（计划 §0.3）。
        /// 净收益判据（错误数严格减少且无新增）不满足 → 回滚，失败路径一律不带 AutoUsings。
        /// </summary>
        private ReplResult AutoRetryRejected(Diagnostic[] origDiags)
        {
            string text = FormatDiags(origDiags);
            if (pendingAmbiguous != null && pendingAmbiguous.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in pendingAmbiguous)
                    parts.Add(kv.Key + ": " + string.Join(" / ", kv.Value) + "（请显式 using 或写全名）");
                text += "\n候选: " + string.Join("; ", parts);
            }
            if (pendingZero != null && pendingZero.Count > 0)
                text += "\n未找到候选: " + string.Join(", ", pendingZero);
            if (pendingOverflow != null)
                text += "\n单次自动补超上限 " + MaxAutoUsings + " 条，本次一条未注入（请显式提供 using）: " + string.Join(", ", pendingOverflow);
            return ReplResult.Failure(text);
        }

        // 自动补流程跨方法暂存（Eval 只在游戏更新线程执行，无并发）。
        private List<KeyValuePair<string, List<string>>> pendingAmbiguous;
        private List<string> pendingZero;
        private List<string> pendingOverflow;

        private MetadataReference[] BuildReferences()
        {
            // 全部已加载程序集（游戏本体、mod、框架）作为引用面；官方引擎会叠加
            // ScriptOptions.Default 自带的核心引用。同名取首个，兜底探测游戏根目录。
            var byName = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic) continue;
                    string loc = asm.Location;
                    if (string.IsNullOrEmpty(loc) || !File.Exists(loc)) continue;
                    string name = asm.GetName().Name;
                    if (!byName.ContainsKey(name))
                        byName[name] = MetadataReference.CreateFromFile(loc);
                }
                catch
                {
                    // 个别不可读/动态程序集跳过即可。
                }
            }

            string root = AppContext.BaseDirectory;
            string[] fallbacks =
            {
                "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll", "mscorlib.dll",
                "System.Runtime.Extensions.dll", "System.Linq.dll", "System.Collections.dll",
                "System.Text.Encoding.CodePages.dll", "Microsoft.CSharp.dll",
            };
            foreach (string name in fallbacks)
            {
                if (byName.ContainsKey(name)) continue;
                string p = Path.Combine(root, name);
                if (File.Exists(p))
                    byName[name] = MetadataReference.CreateFromFile(p);
            }

            return byName.Values.ToArray();
        }

        /// <summary>试编译探针：候选作为 WithImports 的一条命名空间导入。只 Compile 不 Run。</summary>
        private bool ValidateNamespaceImport(string candidate, List<string> importSnapshot, MetadataReference[] refSnapshot, out string error)
        {
            error = null;
            try
            {
                var probeImports = new List<string>(importSnapshot) { candidate };
                ScriptOptions probeOptions = ScriptOptions.Default
                    .WithReferences(refSnapshot)
                    .WithImports(probeImports)
                    .WithAllowUnsafe(true);
                return NoCompileError(CSharpScript.Create("1", probeOptions, typeof(ReplInteractiveBase), loader).Compile(), out error);
            }
            catch (Exception ex)
            {
                error = "校验异常: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>试编译探针：候选作为源码级指令 "using &lt;candidate&gt;;"。只 Compile 不 Run。</summary>
        private bool ValidateDirective(string candidate, List<string> importSnapshot, MetadataReference[] refSnapshot, out string error)
        {
            error = null;
            try
            {
                ScriptOptions probeOptions = ScriptOptions.Default
                    .WithReferences(refSnapshot)
                    .WithImports(importSnapshot)
                    .WithAllowUnsafe(true);
                string probe = "using " + candidate + ";\n1";
                return NoCompileError(CSharpScript.Create(probe, probeOptions, typeof(ReplInteractiveBase), loader).Compile(), out error);
            }
            catch (Exception ex)
            {
                error = "校验异常: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>取第一条 error 级诊断喂给 error；没有 error 则返回 true。</summary>
        private static bool NoCompileError(IEnumerable<Diagnostic> diags, out string error)
        {
            error = null;
            foreach (Diagnostic d in diags)
            {
                if (d.Severity != DiagnosticSeverity.Error) continue;
                error = d.Id + ": " + d.GetMessage();
                return false;
            }
            return true;
        }

        private static string NormalizeUsing(string raw)
        {
            string u = (raw ?? "").Trim();
            if (u.StartsWith("using ", StringComparison.Ordinal)) u = u.Substring(6).Trim();
            else if (u.StartsWith("using", StringComparison.Ordinal)) u = u.Substring(5).Trim();
            if (u.EndsWith(";", StringComparison.Ordinal)) u = u.Substring(0, u.Length - 1).Trim();
            return u;
        }

        private static string FormatDiags(IEnumerable<Diagnostic> diags)
        {
            var lines = new List<string>();
            foreach (Diagnostic d in diags)
            {
                if (d.Severity != DiagnosticSeverity.Error) continue;
                var pos = d.Location.GetLineSpan().StartLinePosition;
                lines.Add(d.Id + " 第" + (pos.Line + 1) + "行: " + d.GetMessage());
                if (lines.Count >= 6) break;
            }
            if (lines.Count == 0) return "编译错误（无诊断细节）";
            return "编译错误:\n  " + string.Join("\n  ", lines);
        }

        private static string FormatValue(object value)
        {
            if (value == null) return "null";
            if (value is string s) return "\"" + s + "\"";
            string t = value.ToString();
            if (t != null && t.Length > 500) t = t.Substring(0, 500) + "…";
            return t;
        }

        private static string FormatFrames(Exception ex)
        {
            var frames = new List<string>();
            StackTrace st = new StackTrace(ex, true);
            for (int i = 0; i < st.FrameCount && frames.Count < 4; i++)
            {
                MethodBase m = st.GetFrame(i).GetMethod();
                if (m == null) continue;
                string name = (m.DeclaringType != null ? m.DeclaringType.FullName + "." : "") + m.Name;
                frames.Add("  在 " + name);
            }
            return string.Join("\n", frames);
        }
    }
}