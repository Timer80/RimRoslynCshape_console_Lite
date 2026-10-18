// RoslynProbe —— /types：短名 → 命名空间/程序集候选。
//
// 规格：短名 → 命名空间/程序集候选；**多候选不猜**（与自动补 using 的多候选口径一致），
// 全部列出；无命中原样返回空数组。纯 BCL/反射，不需要姿势 B。
//
// 为什么要自己扫（而不是复用 RoslynEngine 的 typeIndex）：
//   Source/Game/Repl/RoslynEngine.cs:470 的 EnsureTypeIndex() 是 **private 实例方法**，外部无法访问；
//   而且它只存 短名 → List<命名空间>（没有程序集、没有全名），信息量不足以填 /types 的
//   {fullName, ns, assembly} 三元组。⇒ 按简报的第二条路：自己扫 AppDomain 已加载程序集，
//   口径与它对齐（GetExportedTypes + 跳过动态程序集 + 短名去掉 `n 元数后缀）。
//
// 与 RoslynEngine 的**两处有意差异**（都是有意的超集；自动补 using 的保守口径不受影响）：
//   ① 全局命名空间类型也收录（ns 为空串；RoslynEngine.cs:487 是 continue 直接跳过）。
//   ② loader 异常时不全丢。**NET 10 实测**：GetExportedTypes() 抛的是**原始** loader 异常
//      （FileNotFoundException / TypeLoadException），不带 Types，单靠它无法 salvage；只有
//      GetTypes() 抛 ReflectionTypeLoadException 且带 ex.Types，所以这里回退到 GetTypes()，
//      把 ex.Types 里的非 null 且对外可见的类型收进索引，账本记 AssembliesPartial（可能缺类型）；
//      两条路都失败、或一个类型都保不住，才记 AssembliesFailed。
//
// 缓存：索引懒构建 + 缓存，失效判据 = **非动态程序集的集合指纹**（Name@Version 排序拼接，见
// Fingerprint）；命中判据与取值在同一 lock 内（见 EnsureIndex 注释）。动态程序集刻意不扫，
// 也就不该因为 Roslyn 每次脚本提交而让整个索引重建（审查 F2）。
// 遍历约 111 个程序集的 public 导出类型是这条指令的固有成本（一次性、之后命中缓存）。

using System;
using System.Collections.Generic;
using System.Reflection;

namespace RoslynProbe
{
    /// <summary>/types &lt;短名&gt; —— 短名 → 命名空间/程序集候选（纯 BCL/反射，零 Verse/Unity 依赖）。</summary>
    public static class TypesCommand
    {
        /// <summary>RegisterAll 反射调用（全程序集 public static void Register()，无参、返回 void）。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = "types",
                Usage = "<短名> —— 短名 → 命名空间/程序集候选（多候选全部列出，不猜）",
                Group = "运行时",
                Kind = "types",
                Handler = Provide
            });
        }

        /// <summary>一个候选：短名（索引键）+ 全名 + 命名空间 + 程序集简单名。</summary>
        internal sealed class Candidate
        {
            public string ShortName;
            public string FullName;
            public string Ns;
            public string Assembly;
        }

        /// <summary>
        /// 一次索引构建的扫描账本（F1）：逐程序集 try/catch 的每一次失败/部分成功都计数 + 留明细，
        /// 让「扫了多少、失败多少」可被外部证伪（Data 的 assembliesScanned / assembliesPartial /
        /// assembliesFailed / assemblyFailures 四件套同源）。
        /// </summary>
        internal sealed class ScanStats
        {
            /// <summary>成功取到类型表的非动态程序集数（含 AssembliesPartial 里的部分成功者）。</summary>
            public int AssembliesScanned;

            /// <summary>
            /// 其中「回退 GetTypes() 后只得部分类型」的：GetExportedTypes 抛原始 loader 异常后，
            /// GetTypes 抛 RTLEx，从 ex.Types 里救回 ≥1 个对外可见类型（可能缺类型）。
            /// </summary>
            public int AssembliesPartial;

            /// <summary>完全取不到类型表的（该程序集的类型全丢）。</summary>
            public int AssembliesFailed;

            /// <summary>失败/部分的逐条明细：{name, error, partial}。</summary>
            public readonly List<Dictionary<string, object>> Failures = new List<Dictionary<string, object>>();
        }

        private static readonly object Gate = new object();
        private static Dictionary<string, List<Candidate>> index;
        private static ScanStats indexStats;
        private static string indexFingerprint;

        /// <summary>
        /// 指令入口实现（姿势 A：本指令不需要注入点，Provide 就是"该指令的实现"本身，
        /// 与 GcCommand / DiagCommand / ThreadsCommand 完全同形 —— 契约 v1.1 §3.5 的唯一形态；
        /// 由 TypesCommandTests 的命名断言兜住，防止漂回 Handle）。
        /// </summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            string query = (rest ?? "").Trim();
            if (query.Length == 0)
            {
                return SlashResults.Usage("types",
                    "用法: /types <短名>",
                    "例: /types Pawn —— 只给短名；带命名空间的全名不查（去掉前缀再试）。");
            }

            Dictionary<string, List<Candidate>> idx;
            ScanStats stats;
            try
            {
                idx = EnsureIndex(out stats);
            }
            catch (Exception ex)
            {
                return SlashResults.Error("types",
                    "构建短名索引失败: " + ex.GetType().Name + ": " + ex.Message);
            }

            string mode;
            List<Candidate> hits = Match(idx, query, out mode);
            bool ignoreCase = mode == "ignorecase";

            var r = new SlashResult { Kind = "types" };
            if (hits.Count == 0)
            {
                r.Lines.Add("短名 \"" + query + "\": 无候选（0 个）");
                if (query.IndexOf('.') >= 0)
                    r.Lines.Add("  /types 只按**短名**匹配（不含命名空间）：请去掉前缀，例如 List 而不是 System.Collections.Generic.List。");
                else
                    r.Lines.Add("  换短名再试；程序集清单见 /asm（本次扫描范围见下一行）。");
            }
            else if (hits.Count == 1)
            {
                Candidate only = hits[0];
                r.Lines.Add("短名 \"" + query + "\": 唯一候选"
                    + (ignoreCase ? "（大小写不敏感匹配）" : "")
                    + " —— " + (only.Ns.Length == 0
                        ? "全局命名空间，无需 using"
                        : "可用 using " + only.Ns + ";"));
                r.Lines.Add(RenderCandidate(only, 0));
            }
            else
            {
                r.Lines.Add("短名 \"" + query + "\": " + hits.Count + " 个候选"
                    + (ignoreCase ? "（大小写不敏感匹配）" : "")
                    + "（多候选不猜：请写全名或显式 using）");
                int width = 0;
                foreach (Candidate c in hits) if (c.FullName.Length > width) width = c.FullName.Length;
                if (width > 72) width = 72;
                foreach (Candidate c in hits) r.Lines.Add(RenderCandidate(c, width));
            }
            r.Lines.Add("范围: 非动态程序集 成功 " + stats.AssembliesScanned
                + " / 部分 " + stats.AssembliesPartial
                + " / 失败 " + stats.AssembliesFailed
                + "（动态程序集 / Roslyn 脚本提交不计）。");
            if (stats.AssembliesFailed > 0 || stats.AssembliesPartial > 0)
                r.Lines.Add("  失败/部分明细见 Data[\"assemblyFailures\"]（逐程序集给名称 + 原因；部分 = GetExportedTypes 失败后回退 GetTypes 从 ex.Types 救回部分类型，可能缺类型）。");

            var candidates = new List<Dictionary<string, object>>();
            foreach (Candidate c in hits)
            {
                candidates.Add(new Dictionary<string, object>
                {
                    ["fullName"] = c.FullName,
                    ["ns"] = c.Ns,
                    ["assembly"] = c.Assembly
                });
            }
            r.Data = new Dictionary<string, object>
            {
                ["short"] = query,
                ["count"] = candidates.Count,
                ["assembliesScanned"] = stats.AssembliesScanned,
                ["assembliesPartial"] = stats.AssembliesPartial,
                ["assembliesFailed"] = stats.AssembliesFailed,
                ["assemblyFailures"] = CopyFailures(stats),
                ["candidates"] = candidates
            };
            return r;
        }

        /// <summary>账本明细的防御性拷贝（缓存里的 ScanStats 会被复用，别把它的可变表直接交出去）。</summary>
        private static List<Dictionary<string, object>> CopyFailures(ScanStats stats)
        {
            var list = new List<Dictionary<string, object>>();
            if (stats == null || stats.Failures == null) return list;
            foreach (Dictionary<string, object> f in stats.Failures)
            {
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = f["name"],
                    ["error"] = f["error"],
                    ["partial"] = f["partial"]
                });
            }
            return list;
        }

        private static string RenderCandidate(Candidate c, int width)
        {
            string full = c.FullName;
            if (width > 0 && full.Length < width) full = full.PadRight(width);
            return "  " + full + "  " + (c.Ns.Length == 0 ? "(全局命名空间)" : c.Ns) + "  [" + c.Assembly + "]";
        }

        // ---- 短名索引 ----

        /// <summary>
        /// 懒构建 + 缓存。失效判据 = **非动态程序集**的集合指纹（Name@Version 排序拼接，见 Fingerprint）：
        /// 索引本身刻意跳过动态程序集，而 Roslyn 每次 C# 脚本提交都会造一个动态程序集 ——
        /// 若拿 GetAssemblies().Length 当判据，每提交一次就让下一次 /types 全量重建（F2，实测 11→22→32→34）。
        /// </summary>
        internal static Dictionary<string, List<Candidate>> EnsureIndex(out ScanStats stats)
        {
            Assembly[] assemblies;
            string fingerprint;
            lock (Gate)
            {
                // 命中判据（index / indexFingerprint / indexStats）与取值必须同锁：拆开读最坏会返回
                // 上一代索引（能自愈，但白扔一次构建）。Fingerprint 只读程序集名/版本、不做类型枚举，
                // 放锁内不构成竞争；真正的全量扫描（BuildIndex）仍在锁外。
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
                fingerprint = Fingerprint(assemblies);
                Dictionary<string, List<Candidate>> cached = index;
                if (cached != null && indexFingerprint == fingerprint)
                {
                    stats = indexStats;
                    return cached;
                }
            }

            ScanStats builtStats;
            Dictionary<string, List<Candidate>> built = BuildIndex(assemblies, out builtStats);
            lock (Gate)
            {
                index = built;
                indexStats = builtStats;
                indexFingerprint = fingerprint;
            }
            stats = builtStats;
            return built;
        }

        /// <summary>
        /// 非动态程序集的集合指纹：每项 Name@Version，Ordinal 排序后换行拼接。
        /// 只读程序集名/版本，不做任何类型枚举（每次 /types 都算一遍也不贵）；动态程序集一律不计。
        /// </summary>
        internal static string Fingerprint(Assembly[] assemblies)
        {
            var parts = new List<string>();
            if (assemblies != null)
            {
                foreach (Assembly asm in assemblies)
                {
                    if (asm == null) continue;
                    bool dynamic;
                    try { dynamic = asm.IsDynamic; }
                    catch { continue; }
                    if (dynamic) continue;
                    parts.Add(SimpleName(asm) + "@" + SimpleVersion(asm));
                }
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join("\n", parts);
        }

        private static string SimpleVersion(Assembly asm)
        {
            try
            {
                AssemblyName an = asm.GetName();
                return an == null || an.Version == null ? "" : an.Version.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// 短名（去掉 `n 元数后缀）→ 候选表。遍历 public 导出类型；动态程序集跳过（与 RoslynEngine 口径一致）。
        /// 逐程序集 try/catch 的每次失败/部分成功都记进 ScanStats（F1：统计可证伪）。
        /// internal 供单测用显式程序集列表构建（不依赖宿主进程装了什么）。
        /// </summary>
        internal static Dictionary<string, List<Candidate>> BuildIndex(Assembly[] assemblies, out ScanStats stats)
        {
            var idx = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
            var st = new ScanStats();

            if (assemblies != null)
            {
                foreach (Assembly asm in assemblies)
                {
                    if (asm == null) continue;

                    string asmName = SimpleName(asm);

                    bool dynamic;
                    try { dynamic = asm.IsDynamic; }
                    catch (Exception ex)
                    {
                        // IsDynamic 都抛的病态程序集：既没扫也不是"有意跳过"，计入失败（账本守恒）。
                        st.AssembliesFailed++;
                        st.Failures.Add(Failure(asmName, "IsDynamic 抛出 " + ex.GetType().Name + ": " + ex.Message, false));
                        continue;
                    }
                    if (dynamic) continue;

                    Type[] types;
                    try
                    {
                        types = asm.GetExportedTypes();
                    }
                    catch (Exception ex)
                    {
                        // .NET 10 实测：GetExportedTypes() 抛的是原始 loader 异常（FileNotFoundException /
                        // TypeLoadException），**不带 Types**，单靠它没法 salvage。只有 GetTypes() 抛
                        // ReflectionTypeLoadException 且带 ex.Types —— 所以回退一步再救（审查观察 1，选 (a)）：
                        // 能救回至少一个对外可见类型 → AssembliesPartial；一个都救不回 → AssembliesFailed。
                        types = null;
                        try
                        {
                            types = ExportedOnly(asm.GetTypes());
                        }
                        catch (ReflectionTypeLoadException rtlex)
                        {
                            int slots = rtlex.Types == null ? 0 : rtlex.Types.Length;
                            Type[] salvaged = ExportedOnly(rtlex.Types);
                            if (salvaged.Length == 0)
                            {
                                st.AssembliesFailed++;
                                st.Failures.Add(Failure(asmName,
                                    "GetExportedTypes 抛出 " + ex.GetType().Name + ": " + ex.Message
                                    + "；回退 GetTypes() 抛 ReflectionTypeLoadException，" + slots + " 个类型槽一个也保不住", false));
                                continue;
                            }
                            types = salvaged;
                            st.AssembliesPartial++;
                            st.Failures.Add(Failure(asmName,
                                "GetExportedTypes 抛出 " + ex.GetType().Name + ": " + ex.Message
                                + "；回退 GetTypes() 保住 " + salvaged.Length + " / " + slots
                                + " 个对外可见类型（其余因依赖缺失不可用，可能缺类型）", true));
                        }
                        catch (Exception ex2)
                        {
                            st.AssembliesFailed++;
                            st.Failures.Add(Failure(asmName,
                                "GetExportedTypes 抛出 " + ex.GetType().Name + ": " + ex.Message
                                + "；回退 GetTypes() 也失败: " + ex2.GetType().Name + ": " + ex2.Message, false));
                            continue;
                        }
                    }
                    if (types == null)
                    {
                        st.AssembliesFailed++;
                        st.Failures.Add(Failure(asmName, "GetExportedTypes / GetTypes 均未给出类型表", false));
                        continue;
                    }

                    st.AssembliesScanned++;

                    foreach (Type t in types)
                    {
                        if (t == null) continue;
                        string shortName = ShortName(t);
                        if (shortName.Length == 0) continue;

                        var c = new Candidate
                        {
                            ShortName = shortName,
                            FullName = DisplayFullName(t),
                            Ns = t.Namespace ?? "",
                            Assembly = asmName
                        };

                        List<Candidate> list;
                        if (!idx.TryGetValue(shortName, out list)) idx[shortName] = list = new List<Candidate>();
                        if (!Contains(list, c)) list.Add(c);
                    }
                }
            }

            // 每个候选列表排序 → 输出稳定（同一次查询的两遍、以及单测断言都可复现）。
            foreach (KeyValuePair<string, List<Candidate>> kv in idx) kv.Value.Sort(CompareCandidates);
            stats = st;
            return idx;
        }

        /// <summary>
        /// 只留「程序集外可见」的类型 —— 判据就是 Type.IsVisible（等价 GetExportedTypes：顶层 public，
        /// 或嵌套链全 public 的嵌套类型）。回退 GetTypes() 拿到的表含 internal/private、不可加载类型的
        /// null 槽，以及 __ComObject 这类 attrs=Public 但 IsVisible=False 的特殊类型，都要滤掉。
        /// </summary>
        private static Type[] ExportedOnly(Type[] raw)
        {
            if (raw == null) return new Type[0];
            var list = new List<Type>();
            foreach (Type t in raw)
                if (IsExported(t)) list.Add(t);
            return list.ToArray();
        }

        private static bool IsExported(Type t)
        {
            try
            {
                if (t == null || t.IsGenericParameter) return false;
                // IsVisible 才是「程序集外可见」的判据（public + 嵌套链全 public），也正是 GetExportedTypes 的口径。
                // 只看 IsPublic / IsNestedPublic 会**多收** System.__ComObject 这类 attrs=Public 但 IsVisible=False
                // 的类型（审查观察 1：80 程序集比对仅此 1 处差异；corelib 实测 GetExportedTypes 1378 == IsVisible 1378）。
                return t.IsVisible;
            }
            catch { return false; }   // 元数据异常类型：当作不可见，别让它炸掉整次扫描
        }

        /// <summary>账本明细的一条：{name, error, partial}。</summary>
        private static Dictionary<string, object> Failure(string name, string error, bool partial)
        {
            return new Dictionary<string, object>
            {
                ["name"] = name ?? "?",
                ["error"] = error ?? "",
                ["partial"] = partial
            };
        }

        /// <summary>
        /// 精确（Ordinal）优先；未命中再退一步大小写不敏感（C# 短名本区分大小写，这只是便利），
        /// 命中的候选**全部**列出 —— 绝不从多候选里挑一个。mode: exact | ignorecase | none。
        /// internal 供单测直接喂构造表。
        /// </summary>
        internal static List<Candidate> Match(Dictionary<string, List<Candidate>> idx, string shortName, out string mode)
        {
            mode = "none";
            var hits = new List<Candidate>();
            if (idx == null || string.IsNullOrEmpty(shortName)) return hits;

            List<Candidate> exact;
            if (idx.TryGetValue(shortName, out exact) && exact != null && exact.Count > 0)
            {
                mode = "exact";
                hits.AddRange(exact);
                // 输出契约：候选恒按 (ns, fullName, assembly) 升序 —— 不依赖喂进来的表已排好序。
                hits.Sort(CompareCandidates);
                return hits;
            }

            var keys = new List<string>();
            foreach (string k in idx.Keys)
                if (string.Equals(k, shortName, StringComparison.OrdinalIgnoreCase)) keys.Add(k);
            if (keys.Count == 0) return hits;

            keys.Sort(StringComparer.Ordinal);
            mode = "ignorecase";
            foreach (string k in keys)
            {
                List<Candidate> list = idx[k];
                if (list == null) continue;
                foreach (Candidate c in list) if (!Contains(hits, c)) hits.Add(c);
            }
            hits.Sort(CompareCandidates);
            return hits;
        }

        /// <summary>短名：类型名去掉 `n 元数后缀（List`1 → List）。</summary>
        internal static string ShortName(Type t)
        {
            string n = t.Name ?? "";
            int tick = n.IndexOf('\u0060');
            return tick > 0 ? n.Substring(0, tick) : n;
        }

        /// <summary>
        /// 展示用全名：t.FullName 逐 "+" 段去元数后缀，再用 '.' 连接（嵌套类型写成 C# 形态）。
        /// Dictionary`2+Enumerator → System.Collections.Generic.Dictionary.Enumerator。
        /// </summary>
        internal static string DisplayFullName(Type t)
        {
            string full = t.FullName;
            if (string.IsNullOrEmpty(full))
            {
                string shortName = ShortName(t);
                return string.IsNullOrEmpty(t.Namespace) ? shortName : t.Namespace + "." + shortName;
            }

            string[] parts = full.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                int tick = parts[i].IndexOf('\u0060');
                if (tick > 0) parts[i] = parts[i].Substring(0, tick);
            }
            return string.Join(".", parts);
        }

        private static string SimpleName(Assembly asm)
        {
            try
            {
                AssemblyName an = asm.GetName();
                if (an != null && !string.IsNullOrEmpty(an.Name)) return an.Name;
            }
            catch { }
            return "?";
        }

        private static bool Contains(List<Candidate> list, Candidate c)
        {
            foreach (Candidate x in list)
                if (x.FullName == c.FullName && x.Ns == c.Ns && x.Assembly == c.Assembly) return true;
            return false;
        }

        private static int CompareCandidates(Candidate a, Candidate b)
        {
            int c = string.CompareOrdinal(a.Ns, b.Ns);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.FullName, b.FullName);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Assembly, b.Assembly);
        }
    }
}
