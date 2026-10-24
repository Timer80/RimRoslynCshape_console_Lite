// RoslynProbe —— /prof [status|top [n]]（W9，只读）。
//
// 规格：TEXT38/TOOLSTACK-TO-SLASH-1.7.md §4.2。
//
// ── 路线：status 直接引用 / top 反射 ──────────────────────────────────────
// /prof 的数据源横跨两个程序集：
//   * Verse.ProfilerUI.ProfilerPrefs / ProfilerPrefsData —— Assembly-CSharp，**在编译面内**，直接引用；
//   * Waylith.Profiling.{ProfilerWindowAggregator, ProfilerWindowResult, PathAccumulator,
//     ProfilerPathTable, FrameProfiler, ProfilerLabelId} —— **独立程序集 Waylith.dll，编译面外**
//     （Source/RoslynProbe.csproj 的引用块里没有它），故 top 一律经反射读取。
//
// ⚠️ 反射的固有代价：**字段/成员名按 RimWorld 1.7.4979 rev109 实测绑定；引擎升级必须复验。**
//    本文件所有反射读取都包在 try/catch 里，失败进 Data["gaps"]（逐项说明"哪一项、为什么拿不到"），
//    **绝不抛**。
//
// ── 已核实的三个缺口（简报与实测不符之处）──────────────────────────────────
// 1. "名字还原走 ProfilerPathTable" 不准确。ProfilerPathTable.Entry 只有
//    {parentPathId, labelId, foldedPathId, firstChild, nextSibling}，ProfilerLabelId 只是 ushort
//    包装（ToString() 输出数字或 "none"）——**表里没有任何名字数据**。真正的还原器是
//    FrameProfiler.GetLabel(ProfilerLabelId)：public static（FrameProfiler.cs:1098），
//    越界返回 "<unknown>"。本文件按实测结论实现。
// 2. ProfilerWindowResult.frames / wholeFrames / torn 是 **internal** 字段，跨程序集读不到；
//    公开面只有 FramesIncluded（属性，ClipActive 时给 max(1, wholeFrames)，否则给 frames）。
//    故 Data["frames"] 是**近似值**（framesApprox=true），Data["torn"] 只能走 NonPublic 反射，
//    拿不到就是 null + gap。
// 3. "当前是否在采样"没有可读开关：§4.2 明确"采样在主循环无条件跑"。Data["sampling"] 因此
//    置 null + gap（reason 说明它是无条件行为，不是可查询状态），不编造 true。
//
// ── 线程规则（全舰队；契约 v1.1 §3.4.1：判据只认 ctx.IsGameThread，不用 Channel 猜）────────
// 三分支：if (ctx.IsGameThread) 就地执行; else if (ctx.Executor != null) 经 Executor.Enqueue + TCS + 超时
// marshal; else **就地**（本指令特意如此，理由见下方的边界裁定）。ProfilerWindowAggregator.Current 依 §4.2
// 任意线程可读，top 本身不需要 marshal；ProfilerPrefs.Current 的**首次**访问会走 Load()（读 Config/Profiler.xml、
// 设插桩排除表），为不在读线程上抢跑这次初始化，有 Executor 时统一 marshal 过去（与 LogCommand 同款
// Enqueue + TCS 等待 + MarkTimedOut）。
//
// ⚠️ 边界（控制者 2026-10-15 裁定，**不要"顺手补齐"把它改成 marshal**）：
//   「路径/配置类静态读」不属于线程规则要管的「游戏状态/Unity 对象」。ProfilerPrefs.Current 首访走的
//   GenFilePaths.ConfigFolderPath（底层是 UnityEngine persistentDataPath）只是**读一个路径静态量 + 读 XML 文件**，
//   不碰场景/游戏对象/窗口栈那类会竞争或触发断言的状态（/ui open 的 new Window + WindowStack.Add 才是那种）。
//   所以「false + 无 Executor → 就地」是**有意的设计**，不是待补的缺口。若将来真机确认它在读线程上触发断言，
//   按规则**升级为 marshal** 即可 —— 可升级，不靠猜。
//
// ── Data 形状（小驼峰、稳定）─────────────────────────────────────────────
// status: { mode:"status", prefs:{7 项引擎口径}, prefsExtra:{...}, prefsPath, sampling:null,
//           frames:null, torn:null, gaps:[...] }
// top   : { mode:"top", frames:int|null, framesApprox:bool, torn:bool|null, touched:int,
//           scopes:[{pathId, name, path, selfUs, totalUs, calls, selfAllocBytes, allocBytes, entries}],
//           gaps:[...] }

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using RoslynProbe.Repl;   // Executor / ReplRequest / ReplResult / SessionLine（marshal 通道）
using Verse;              // GenFilePaths：provider 本体（线程判据改用契约 v1.1 的 ctx.IsGameThread，不再用 UnityData）
using Verse.ProfilerUI;   // ProfilerPrefs / ProfilerPrefsData（Assembly-CSharp，编译面内）

namespace RoslynProbe
{
    /// <summary>/prof —— 剖析配置回读 + 自耗时热点排名（只读）。姿势 B：游戏侧 provider 在本文件内。</summary>
    public static class ProfCommand
    {
        /// <summary>指令 verb（不含 '/'）。</summary>
        public const string Verb = "prof";

        /// <summary>SlashResult.Kind。</summary>
        public const string Kind = "prof";

        /// <summary>/prof top 不带 n 时的条数。</summary>
        public const int DefaultTopN = 20;

        /// <summary>Data["gaps"] 取值：Waylith 不在编译面内（top 走反射）。</summary>
        public const string GapWaylith = "waylith-not-in-reference-set";

        /// <summary>Data["gaps"] 取值：采样状态没有可读开关（§4.2：无条件跑）。</summary>
        public const string GapSampling = "sampling-state-not-readable";

        /// <summary>Data["gaps"] 取值：torn 是 internal 字段，只能 NonPublic 反射。</summary>
        public const string GapTorn = "torn-internal-field";

        /// <summary>Data["gaps"] 取值：frames 只能用公开的 FramesIncluded 近似。</summary>
        public const string GapFrames = "frames-internal-field-approx-only";

        /// <summary>marshal 等待上限：与协议层 SessionLine.DefaultTimeoutMs 同源，不自造数字。</summary>
        public const int MarshalTimeoutMs = SessionLine.DefaultTimeoutMs;

        /// <summary>子命令。</summary>
        public enum Mode
        {
            /// <summary>配置回读。</summary>
            Status,
            /// <summary>自耗时热点。</summary>
            Top
        }

        /// <summary>参数解析结果（纯逻辑，可单测）。</summary>
        public sealed class Query
        {
            /// <summary>子命令；不带参数时默认 status。</summary>
            public Mode Mode = Mode.Status;
            /// <summary>top 的条数。</summary>
            public int N = DefaultTopN;
            /// <summary>非 null = 用法错误。</summary>
            public string Error;
        }

        /// <summary>一个热点 scope（纯数据，便于纯逻辑单测与跨线程带回）。</summary>
        public sealed class ScopeRow
        {
            /// <summary>ProfilerPathTable 的 pathId。</summary>
            public int PathId;
            /// <summary>叶子标签名（FrameProfiler.GetLabel 还原；失败时回落 "#pathId"）。</summary>
            public string Name = "";
            /// <summary>从根到叶的标签链（"a &gt; b"，尽力而为；失败时等于 Name）。</summary>
            public string Path = "";
            /// <summary>该 scope 的自耗时（微秒）。</summary>
            public double SelfUs;
            /// <summary>该 scope 的含子总耗时（微秒）。</summary>
            public double TotalUs;
            /// <summary>调用次数。</summary>
            public int Calls;
            /// <summary>自分配字节数。</summary>
            public double SelfAllocBytes;
            /// <summary>含子总分配字节数。</summary>
            public double AllocBytes;
            /// <summary>采样条目数。</summary>
            public int Entries;
        }

        /// <summary>top 的一次快照（反射读取的结果 + 缺口）。</summary>
        public sealed class TopSnapshot
        {
            /// <summary>命中的 scope（未排序）。</summary>
            public readonly List<ScopeRow> Rows = new List<ScopeRow>();
            /// <summary>逐项缺口说明（永不抛，坏消息都走这里）。</summary>
            public readonly List<string> Gaps = new List<string>();
            /// <summary>近似帧数（FramesIncluded）；拿不到为 null。</summary>
            public int? Frames;
            /// <summary>frames 是否为近似值。</summary>
            public bool FramesApprox;
            /// <summary>窗口是否被撕裂（torn，internal 字段反射）；拿不到为 null。</summary>
            public bool? Torn;
            /// <summary>touchedPathIds 的有效条数。</summary>
            public int Touched;
        }

        /// <summary>RegisterAll 反射调用（全程序集 public static void Register()，无参、返回 void）。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = Verb,
                Usage = "[status|top [n]] —— 只读：配置回读（7 项）/ 自耗时前 n 个 scope（默认 " + DefaultTopN + "）",
                Group = "运行时",
                Kind = Kind,
                Dangerous = false,
                Handler = Handle
            });
        }

        /// <summary>handler：一律委托给冻结的能力委托（未注入 → 标准"能力未注册"行）。</summary>
        internal static SlashResult Handle(string rest, SlashContext ctx)
        {
            return ctx.CallProf(rest);
        }

        // ---------------- 纯逻辑（可单测） ----------------

        /// <summary>解析 rest：空 / "status" → status；"top [n]" → top；其余 → 用法错误。</summary>
        public static Query ParseArgs(string rest)
        {
            var q = new Query();
            string r = (rest ?? "").Trim();
            if (r.Length == 0) return q;

            string head = r, tail = "";
            // 分隔空白 = 任意空白（SlashRegistry.IndexOfWhitespace 是唯一出口）—— 只认 ASCII 空格会让
            // "/prof top<TAB>3" 落进"未知子命令"分支（明明是 top 3）。
            int sp = SlashRegistry.IndexOfWhitespace(r);
            if (sp >= 0)
            {
                head = r.Substring(0, sp).Trim();
                tail = r.Substring(sp + 1).Trim();
            }

            if (head.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                if (tail.Length > 0) q.Error = "/prof status 不接受参数（收到 \"" + tail + "\"）";
                else q.Mode = Mode.Status;
                return q;
            }

            if (head.Equals("top", StringComparison.OrdinalIgnoreCase))
            {
                q.Mode = Mode.Top;
                if (tail.Length == 0) return q;
                int n;
                if (!int.TryParse(tail, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) || n < 1)
                {
                    q.Error = "n 必须是 ≥ 1 的整数（收到 \"" + tail + "\"）";
                    return q;
                }
                q.N = n;
                return q;
            }

            q.Error = "未知子命令 \"" + head + "\"";
            return q;
        }

        /// <summary>按自耗时降序取前 n 条（稳定：同值按 pathId 升序）。纯逻辑。</summary>
        public static List<ScopeRow> OrderTop(List<ScopeRow> rows, int n)
        {
            var list = new List<ScopeRow>();
            if (rows != null) list.AddRange(rows);
            list.Sort(delegate (ScopeRow a, ScopeRow b)
            {
                int c = b.SelfUs.CompareTo(a.SelfUs);
                if (c != 0) return c;
                return a.PathId.CompareTo(b.PathId);
            });
            int take = n < 1 ? 1 : n;
            if (list.Count > take) list.RemoveRange(take, list.Count - take);
            return list;
        }

        /// <summary>把 µs 渲染成固定小数（列对齐用）。</summary>
        public static string FmtUs(double us)
        {
            if (us >= 1000000.0) return (us / 1000000.0).ToString("0.000", CultureInfo.InvariantCulture) + "s";
            if (us >= 1000.0) return (us / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + "ms";
            return us.ToString("0.0", CultureInfo.InvariantCulture) + "us";
        }

        /// <summary>把字节数渲染成人类可读串。</summary>
        public static string FmtBytes(double b)
        {
            if (b >= 1048576.0) return (b / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + "MB";
            if (b >= 1024.0) return (b / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + "KB";
            return b.ToString("0", CultureInfo.InvariantCulture) + "B";
        }

        /// <summary>用法页（error 非空时带上错误行）。</summary>
        public static SlashResult UsageResult(string error)
        {
            var lines = new List<string> { "用法: /prof [status|top [n]]" };
            if (!string.IsNullOrEmpty(error)) lines.Add("错误: " + error);
            lines.Add("status   —— 配置回读：Config/Profiler.xml 的 7 项引擎口径设置（默认）");
            lines.Add("top [n]  —— 按自耗时（selfMicroseconds）取前 n 个 scope，默认 " + DefaultTopN);
            lines.Add("本指令只读：没有 start/stop —— §4.2 采样在主循环无条件跑；开窗走 /debug-action Frame profiler");
            return SlashResults.Usage(Kind, lines.ToArray());
        }

        /// <summary>status 的 Lines + Data 渲染（纯逻辑：prefs 由调用方读好传进来）。</summary>
        public static SlashResult BuildStatus(Dictionary<string, object> prefs, Dictionary<string, object> prefsExtra,
            string prefsPath, List<string> gaps)
        {
            var r = new SlashResult { Kind = Kind };
            r.Lines.Add("剖析配置" + (string.IsNullOrEmpty(prefsPath) ? "（Config/Profiler.xml）" : "（" + prefsPath + "）"));
            if (prefs != null)
            {
                int col = 0;
                foreach (string k in prefs.Keys) if (k.Length > col) col = k.Length;
                col += 2;
                foreach (KeyValuePair<string, object> kv in prefs)
                    r.Lines.Add("  " + kv.Key.PadRight(col) + ": " + Render(kv.Value));
                if (prefsExtra != null && prefsExtra.Count > 0)
                {
                    r.Lines.Add("  （非引擎口径，UI 侧）");
                    foreach (KeyValuePair<string, object> kv in prefsExtra)
                        r.Lines.Add("  " + kv.Key.PadRight(col) + ": " + Render(kv.Value));
                }
            }
            else
            {
                r.Lines.Add("  读取失败（见 gaps）");
            }

            r.Lines.Add("采样状态: 不可查询 —— §4.2 采样在主循环无条件跑，没有独立开关可读。");
            r.Lines.Add("插桩目标默认值: Assembly-CSharp!Verse.*::* / Assembly-CSharp!RimWorld.*::*");
            r.Lines.Add("插桩排除表: 0Harmony / MonoMod.* / Mono.Cecil*（ProfilerPrefs.Load() 里硬编码，非配置项）");
            r.Lines.Add("开窗用 /debug-action Frame profiler（原版火焰图/调用树窗口，零代码）。");
            if (gaps != null && gaps.Count > 0)
                foreach (string gp in gaps) r.Lines.Add("缺口: " + gp);

            r.Data = new Dictionary<string, object>
            {
                ["mode"] = "status",
                ["prefs"] = prefs ?? (object)new Dictionary<string, object>(),
                ["prefsExtra"] = prefsExtra ?? (object)new Dictionary<string, object>(),
                ["prefsPath"] = prefsPath ?? "",
                // sampling 恒为 null：§4.2 采样在主循环无条件跑，"是否在采样"不是一个可查询的状态。
                // 解释只在 Lines 与 gaps（GapSampling）里说一次 —— 不放恒为同一字面量的 Data 键（判例：常量字段不合格）。
                ["sampling"] = null,
                ["frames"] = null,
                ["torn"] = null,
                ["gaps"] = ToObjectList(gaps)
            };
            return r;
        }

        /// <summary>top 的 Lines + Data 渲染（纯逻辑：snapshot 由反射读好传进来）。</summary>
        public static SlashResult BuildTop(TopSnapshot snap, int n)
        {
            if (snap == null) snap = new TopSnapshot();
            List<ScopeRow> rows = OrderTop(snap.Rows, n);

            var r = new SlashResult { Kind = Kind };
            string head = "剖析热点（按自耗时 selfMicroseconds 降序，取前 " + (n < 1 ? 1 : n) + " / 命中 " + snap.Rows.Count + "）";
            r.Lines.Add(head);
            r.Lines.Add("  帧数: " + (snap.Frames.HasValue
                ? snap.Frames.Value.ToString(CultureInfo.InvariantCulture) + (snap.FramesApprox ? "（近似：FramesIncluded）" : "")
                : "未知"));
            r.Lines.Add("  撕裂(torn): " + (snap.Torn.HasValue ? (snap.Torn.Value ? "是" : "否") : "未知（internal 字段）"));

            if (rows.Count == 0)
            {
                r.Lines.Add("  （没有命中 scope —— 剖析窗口可能还没攒够帧，或引擎侧尚未开始采样）");
            }
            else
            {
                int nameCol = 0;
                foreach (ScopeRow row in rows) if (row.Path.Length > nameCol) nameCol = row.Path.Length;
                if (nameCol > 88) nameCol = 88;
                r.Lines.Add("  " + "自身".PadLeft(10) + " " + "含子".PadLeft(10) + " " + "调用".PadLeft(8)
                    + " " + "自身分配".PadLeft(10) + "  路径");
                foreach (ScopeRow row in rows)
                {
                    string nm = row.Path.Length > nameCol ? row.Path.Substring(0, nameCol) : row.Path;
                    r.Lines.Add("  " + FmtUs(row.SelfUs).PadLeft(10) + " " + FmtUs(row.TotalUs).PadLeft(10)
                        + " " + row.Calls.ToString(CultureInfo.InvariantCulture).PadLeft(8)
                        + " " + FmtBytes(row.SelfAllocBytes).PadLeft(10) + "  " + nm);
                }
            }
            foreach (string gp in snap.Gaps) r.Lines.Add("缺口: " + gp);

            var scopes = new List<Dictionary<string, object>>();
            foreach (ScopeRow row in rows)
                scopes.Add(new Dictionary<string, object>
                {
                    ["pathId"] = row.PathId,
                    ["name"] = row.Name ?? "",
                    ["path"] = row.Path ?? "",
                    ["selfUs"] = row.SelfUs,
                    ["totalUs"] = row.TotalUs,
                    ["calls"] = row.Calls,
                    ["selfAllocBytes"] = row.SelfAllocBytes,
                    ["allocBytes"] = row.AllocBytes,
                    ["entries"] = row.Entries
                });

            r.Data = new Dictionary<string, object>
            {
                ["mode"] = "top",
                ["frames"] = snap.Frames.HasValue ? (object)snap.Frames.Value : null,
                ["framesApprox"] = snap.FramesApprox,
                ["torn"] = snap.Torn.HasValue ? (object)snap.Torn.Value : null,
                ["touched"] = snap.Touched,
                ["scopes"] = scopes,
                ["gaps"] = ToObjectList(snap.Gaps)
            };
            return r;
        }

        private static string Render(object v)
        {
            if (v == null) return "(null)";
            if (v is bool) return ((bool)v) ? "true" : "false";
            if (v is List<object>)
            {
                var l = (List<object>)v;
                var parts = new List<string>();
                foreach (object o in l) parts.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
                return string.Join(" / ", parts.ToArray());
            }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static List<object> ToObjectList(List<string> src)
        {
            var outList = new List<object>();
            if (src != null) foreach (string s in src) outList.Add(s);
            return outList;
        }

        // ---------------- 姿势 B provider（游戏侧） ----------------

        /// <summary>
        /// Bootstrap 注入到 SlashContext.Prof 的 provider 本体（方法名 Provide 按全舰队统一命名规则，控制者 2026-10-15 裁定）。
        /// 解析 → （必要时）marshal → 读取 → 渲染。
        /// 任何异常都收敛成结果行；**永不抛**。
        /// </summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            Query q = ParseArgs(rest);
            if (!string.IsNullOrEmpty(q.Error)) return UsageResult(q.Error);

            // GUI 壳（OnGUI，游戏主线程）或没有 marshal 通道：就地执行。
            if (OnGameThread(ctx) || ctx == null || ctx.Executor == null) return Run(q);

            // 命名管道：读线程 → 经 Executor 队列 marshal 到游戏更新线程
            // （ProfilerPrefs.Current 首次访问会 Load()；top 本身依 §4.2 免 marshal，此处统一走 marshal）。
            SlashResult captured = null;
            ReplRequest req = ctx.Executor.Enqueue(
                delegate { captured = Run(q); return ReplResult.Success(null); }, MarshalTimeoutMs);

            bool done;
            try { done = req.Tcs.Task.Wait(MarshalTimeoutMs); }
            catch (Exception ex)
            {
                return SlashResults.Error(Kind, "剖析读取失败: " + ex.GetType().Name + ": " + ex.Message);
            }
            if (!done)
            {
                ctx.Executor.MarkTimedOut(req.Id);
                return SlashResults.Error(Kind,
                    "剖析读取超时（游戏更新线程 " + MarshalTimeoutMs + "ms 内未排空队列）");
            }
            if (captured == null) return SlashResults.Error(Kind, "剖析读取失败：游戏线程未取到结果");
            return captured;
        }

        /// <summary>在游戏更新线程上执行一次查询；异常一律收敛成错误行。</summary>
        internal static SlashResult Run(Query q)
        {
            try
            {
                if (q == null) q = new Query();
                if (q.Mode == Mode.Status) return DoStatus();
                return BuildTop(CaptureTop(), q.N);
            }
            catch (Exception ex)
            {
                return SlashResults.Error(Kind, "剖析读取失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 是否已在游戏线程：**只认契约 v1.1 的 <c>ctx.IsGameThread</c>**（普通 bool 事实位，壳层在构造
        /// context 时填；拿不准填 false = 多一次 marshal）。不再用 `Channel == "gui"` 猜，也不再拿
        /// `UnityData.IsInMainThread` 自造第二套判定 —— 契约 v1.1 §3.4.1 正是为收口这类启发式而加的字段。
        /// </summary>
        private static bool OnGameThread(SlashContext ctx)
        {
            return ctx != null && ctx.IsGameThread;
        }

        /// <summary>status：只读 ProfilerPrefs.Current（Assembly-CSharp，编译面内），不碰 Waylith。</summary>
        internal static SlashResult DoStatus()
        {
            var prefs = new Dictionary<string, object>();
            var extra = new Dictionary<string, object>();
            var gaps = new List<string>();

            try
            {
                ProfilerPrefsData p = ProfilerPrefs.Current;
                if (p == null)
                {
                    gaps.Add("prefs: ProfilerPrefs.Current 为 null");
                }
                else
                {
                    // 这 7 项就是 ProfilerPrefs.Apply() 推入引擎的口径（"ProfilerPrefs 7 项"）。
                    prefs["historyCapacity"] = p.historyCapacity;
                    prefs["allocTracking"] = p.allocTracking;
                    prefs["mainThreadOnly"] = p.mainThreadOnly;
                    prefs["ignoreThrottle"] = p.ignoreThrottle;
                    prefs["autoMuteTrivial"] = p.autoMuteTrivial;
                    prefs["instrumentation"] = p.instrumentation;
                    var targets = new List<object>();
                    if (p.instrumentationTargets != null) foreach (string t in p.instrumentationTargets) targets.Add(t);
                    prefs["instrumentationTargets"] = targets;

                    extra["startOnBoot"] = p.startOnBoot;
                    extra["keepRunningWhenClosed"] = p.keepRunningWhenClosed;
                    extra["foldRecursion"] = p.foldRecursion;
                    extra["budgetMs"] = (double)p.budgetMs;
                }
            }
            catch (Exception ex)
            {
                gaps.Add("prefs: 读取 ProfilerPrefs.Current 失败 —— " + ex.GetType().Name + ": " + ex.Message);
            }

            gaps.Add(GapSampling + ": sampling 置 null —— " + "§4.2 采样在主循环无条件跑，没有可查询的开关");
            gaps.Add(GapFrames + ": frames 置 null —— frames/wholeFrames 是 ProfilerWindowResult 的 internal 字段，status 不经反射");
            gaps.Add(GapTorn + ": torn 置 null —— 同上，internal 字段；top 会用 NonPublic 反射尽力取");

            return BuildStatus(prefs, extra, PrefsPath(), gaps);
        }

        /// <summary>Config/Profiler.xml 的绝对路径（GenFilePaths.ConfigFolderPath 是 public static）。</summary>
        internal static string PrefsPath()
        {
            try { return System.IO.Path.Combine(GenFilePaths.ConfigFolderPath, "Profiler.xml"); }
            catch { return null; }
        }

        /// <summary>
        /// top：反射读 Waylith.Profiling.*（编译面外）。逐项 try/catch，坏消息进 Gaps，绝不抛。
        /// **成员名按 1.7.4979 rev109 实测绑定；引擎升级需复验。**
        /// </summary>
        internal static TopSnapshot CaptureTop()
        {
            var snap = new TopSnapshot();

            Type tAgg = Reflect.FindType("Waylith.Profiling.ProfilerWindowAggregator");
            Type tRes = Reflect.FindType("Waylith.Profiling.ProfilerWindowResult");
            Type tAcc = Reflect.FindType("Waylith.Profiling.PathAccumulator");
            Type tTable = Reflect.FindType("Waylith.Profiling.ProfilerPathTable");
            Type tProf = Reflect.FindType("Waylith.Profiling.FrameProfiler");
            Type tLabel = Reflect.FindType("Waylith.Profiling.ProfilerLabelId");
            if (tAgg == null || tRes == null || tAcc == null || tTable == null || tProf == null || tLabel == null)
            {
                snap.Gaps.Add(GapWaylith + ": 未找到 " + FirstMissing(tAgg, tRes, tAcc, tTable, tProf, tLabel)
                    + " —— Waylith.dll 未加载？本工程的引用块没有它，只能反射。");
                return snap;
            }

            object result;
            try
            {
                PropertyInfo pi = tAgg.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                if (pi == null) { snap.Gaps.Add(GapWaylith + ": ProfilerWindowAggregator.Current 属性不存在"); return snap; }
                result = pi.GetValue(null);
                if (result == null) { snap.Gaps.Add(GapWaylith + ": ProfilerWindowAggregator.Current 为 null"); return snap; }
            }
            catch (Exception ex)
            {
                snap.Gaps.Add(GapWaylith + ": 读 ProfilerWindowAggregator.Current 失败 —— " + ex.GetType().Name + ": " + ex.Message);
                return snap;
            }

            // frames：公开面只有 FramesIncluded（近似）。
            try
            {
                PropertyInfo pf = tRes.GetProperty("FramesIncluded", BindingFlags.Public | BindingFlags.Instance);
                if (pf != null)
                {
                    snap.Frames = Convert.ToInt32(pf.GetValue(result), CultureInfo.InvariantCulture);
                    snap.FramesApprox = true;
                }
                else
                {
                    snap.Gaps.Add(GapFrames + ": ProfilerWindowResult.FramesIncluded 属性不存在");
                }
            }
            catch (Exception ex)
            {
                snap.Gaps.Add(GapFrames + ": 读 FramesIncluded 失败 —— " + ex.GetType().Name + ": " + ex.Message);
            }

            // torn：internal 字段，只能 NonPublic 反射。
            try
            {
                FieldInfo ft = tRes.GetField("torn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ft != null) snap.Torn = (bool)ft.GetValue(result);
                else snap.Gaps.Add(GapTorn + ": ProfilerWindowResult.torn 字段不存在");
            }
            catch (Exception ex)
            {
                snap.Gaps.Add(GapTorn + ": 读 torn（NonPublic 反射）失败 —— " + ex.GetType().Name + ": " + ex.Message);
            }

            Array byPath;
            int[] touched;
            try
            {
                byPath = tRes.GetField("byPathId", BindingFlags.Instance | BindingFlags.Public)?.GetValue(result) as Array;
                touched = tRes.GetField("touchedPathIds", BindingFlags.Instance | BindingFlags.Public)?.GetValue(result) as int[];
                // touchedCount 缺失时必须**明示**：否则 Touched=0 会让 top 返回空表，把「读不到」伪装成「没有热点」。
                FieldInfo fTouchedCount = tRes.GetField("touchedCount", BindingFlags.Instance | BindingFlags.Public);
                if (fTouchedCount == null)
                {
                    snap.Gaps.Add("prof-result-fields: ProfilerWindowResult.touchedCount 字段不存在 —— 无法判定命中数；"
                        + "本指令按 0 处理并且明示（**不代表真的没有热点**）");
                    snap.Touched = 0;
                }
                else
                {
                    object tc = fTouchedCount.GetValue(result);
                    if (tc == null)
                    {
                        snap.Gaps.Add("prof-result-fields: touchedCount 值为 null —— 无法判定命中数（不代表没有热点），按 0 处理");
                        snap.Touched = 0;
                    }
                    else
                    {
                        snap.Touched = Convert.ToInt32(tc, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception ex)
            {
                snap.Gaps.Add("prof-result-fields: 读 byPathId/touchedPathIds/touchedCount 失败 —— " + ex.GetType().Name + ": " + ex.Message);
                return snap;
            }
            if (byPath == null || touched == null)
            {
                snap.Gaps.Add("prof-result-fields: byPathId/touchedPathIds 为 null（剖析窗口还没产出结果？）");
                return snap;
            }

            // 名字还原器：FrameProfiler.GetLabel(ProfilerLabelId)（public static，FrameProfiler.cs:1098）。
            // 成员查找（F-6）：与其它字段同款降级 —— 全部包 try/catch，缺失/抛异常都进 gaps，
            // 不许落到 Run 的总 catch 变成一条"整条指令失败"的错误行（那是诊断信息更少的路径）。
            Array entries = null;
            Type tEntry = null;
            FieldInfo fLabelId = null;
            FieldInfo fParent = null;
            MethodInfo mGetLabel = null;
            bool lookupFailed = false;
            try
            {
                tEntry = tTable.GetNestedType("Entry", BindingFlags.Public | BindingFlags.NonPublic);
                if (tEntry != null)
                {
                    fLabelId = tEntry.GetField("labelId");
                    fParent = tEntry.GetField("parentPathId");
                }
                mGetLabel = tProf.GetMethod("GetLabel", BindingFlags.Public | BindingFlags.Static, null, new[] { tLabel }, null);
            }
            catch (Exception ex)
            {
                lookupFailed = true;
                snap.Gaps.Add("prof-member-lookup: 反射查找 ProfilerPathTable.Entry / labelId / parentPathId / FrameProfiler.GetLabel 失败 —— "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            if (!lookupFailed)
            {
                if (tEntry == null)
                {
                    snap.Gaps.Add("prof-path-table: ProfilerPathTable.Entry 嵌套类型不存在 —— 名字与父链都无法还原");
                }
                else
                {
                    if (fLabelId == null) snap.Gaps.Add("prof-path-table: Entry.labelId 字段不存在 —— 名字无法还原");
                    if (fParent == null) snap.Gaps.Add("prof-path-table: Entry.parentPathId 字段不存在 —— 路径链退化为叶子名");
                    if (mGetLabel == null) snap.Gaps.Add("prof-label-name: FrameProfiler.GetLabel(ProfilerLabelId) 不存在 —— 名字无法还原");
                }
            }
            try
            {
                PropertyInfo pe = tTable.GetProperty("Entries", BindingFlags.Public | BindingFlags.Static);
                if (pe != null) entries = pe.GetValue(null) as Array;
            }
            catch (Exception ex)
            {
                snap.Gaps.Add("prof-path-table: 读 ProfilerPathTable.Entries 失败 —— " + ex.GetType().Name + ": " + ex.Message);
            }
            if (entries == null) snap.Gaps.Add("prof-path-table: ProfilerPathTable.Entries 拿不到 —— 名字无法还原，只能回落 #pathId");

            FieldInfo fSelf = tAcc.GetField("selfMicroseconds");
            FieldInfo fTotal = tAcc.GetField("totalMicroseconds");
            FieldInfo fCalls = tAcc.GetField("calls");
            FieldInfo fSelfAlloc = tAcc.GetField("selfAllocBytes");
            FieldInfo fAlloc = tAcc.GetField("allocBytes");
            FieldInfo fEntries = tAcc.GetField("entries");

            int limit = Math.Min(snap.Touched, touched.Length);
            for (int i = 0; i < limit; i++)
            {
                int pid = touched[i];
                try
                {
                    if (pid < 0 || pid >= byPath.Length) { snap.Gaps.Add("scope " + pid.ToString(CultureInfo.InvariantCulture) + ": pathId 越界"); continue; }
                    object acc = byPath.GetValue(pid);
                    var row = new ScopeRow { PathId = pid };
                    row.SelfUs = ReadDouble(fSelf, acc);
                    row.TotalUs = ReadDouble(fTotal, acc);
                    row.Calls = (int)ReadDouble(fCalls, acc);
                    row.SelfAllocBytes = ReadDouble(fSelfAlloc, acc);
                    row.AllocBytes = ReadDouble(fAlloc, acc);
                    row.Entries = (int)ReadDouble(fEntries, acc);
                    row.Name = NameOf(entries, fLabelId, mGetLabel, pid);
                    if (row.Name == null) row.Name = "#" + pid.ToString(CultureInfo.InvariantCulture);
                    row.Path = PathOf(entries, fLabelId, fParent, mGetLabel, pid);
                    if (string.IsNullOrEmpty(row.Path)) row.Path = row.Name;
                    snap.Rows.Add(row);
                }
                catch (Exception ex)
                {
                    snap.Gaps.Add("scope " + pid.ToString(CultureInfo.InvariantCulture) + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            return snap;
        }

        private static double ReadDouble(FieldInfo f, object boxed)
        {
            if (f == null || boxed == null) return 0.0;
            object v = f.GetValue(boxed);
            return v == null ? 0.0 : Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        private static string NameOf(Array entries, FieldInfo fLabelId, MethodInfo mGetLabel, int pathId)
        {
            try
            {
                if (entries == null || fLabelId == null || mGetLabel == null) return null;
                if (pathId < 0 || pathId >= entries.Length) return null;
                object labelId = fLabelId.GetValue(entries.GetValue(pathId));
                if (labelId == null) return null;
                return (string)mGetLabel.Invoke(null, new[] { labelId });
            }
            catch { return null; }
        }

        private static string PathOf(Array entries, FieldInfo fLabelId, FieldInfo fParent, MethodInfo mGetLabel, int pathId)
        {
            try
            {
                if (entries == null || fParent == null) return null;
                var parts = new List<string>();
                int cur = pathId;
                int guard = 0;
                while (cur > 1 && guard++ < 64)
                {
                    string n = NameOf(entries, fLabelId, mGetLabel, cur);
                    if (!string.IsNullOrEmpty(n)) parts.Insert(0, n);
                    if (cur >= entries.Length) break;
                    object e = entries.GetValue(cur);
                    if (e == null) break;
                    object pv = fParent.GetValue(e);
                    if (pv == null) break;
                    int p = Convert.ToInt32(pv, CultureInfo.InvariantCulture);
                    if (p <= 0 || p == cur) break;
                    cur = p;
                }
                if (parts.Count == 0) return null;
                return string.Join(" > ", parts.ToArray());
            }
            catch { return null; }
        }

        private static string FirstMissing(params Type[] types)
        {
            string[] names =
            {
                "ProfilerWindowAggregator", "ProfilerWindowResult", "PathAccumulator",
                "ProfilerPathTable", "FrameProfiler", "ProfilerLabelId"
            };
            var miss = new List<string>();
            for (int i = 0; i < types.Length && i < names.Length; i++)
                if (types[i] == null) miss.Add("Waylith.Profiling." + names[i]);
            return miss.Count == 0 ? "(?)" : string.Join(", ", miss.ToArray());
        }

        /// <summary>反射找类型：先扫已加载程序集，再按名兜底加载 Waylith。</summary>
        private static class Reflect
        {
            /// <summary>在已加载程序集里按全名找类型；找不到再尝试 Assembly.Load("Waylith")。全部异常吞掉。</summary>
            public static Type FindType(string fullName)
            {
                Assembly[] asms;
                try { asms = AppDomain.CurrentDomain.GetAssemblies(); }
                catch { asms = new Assembly[0]; }
                for (int i = 0; i < asms.Length; i++)
                {
                    Assembly a = asms[i];
                    if (a == null) continue;
                    try
                    {
                        Type t = a.GetType(fullName, false);
                        if (t != null) return t;
                    }
                    catch { }
                }
                try
                {
                    Assembly w = Assembly.Load("Waylith");
                    if (w != null) return w.GetType(fullName, false);
                }
                catch { }
                return null;
            }
        }
    }
}