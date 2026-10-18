// RoslynProbe —— /debug-action:原版 LudeonTK 调试动作树的「索引 + 执行」。
//
// 规格 = TEXT38\TOOLSTACK-TO-SLASH-1.7.md 的 /debug-action 行 + §4.4 全文(必逐条遵守)。
// 落地口径(与 §4.4 的六条结论一一对应):
//
//  1. 树不自己造:Dialog_Debug.TrySetupNodeGraph() 建树、node.Path(反斜杠分隔) 当键、
//     node.VisibleNow / node.ActiveNow 当可用性判定。全程**不**反射 GenTypes.AllTypes。
//     取节点用 FindNodeNoExpand:语义与 Dialog_Debug.GetNode(path) 逐步一致(按 label 逐段匹配、
//     祖先逐层 TrySetupChildren),唯一差别是**目标节点本身不展开**。这不是可选优化 —— 原版
//     GetNode 对每一段(含最后一段)都调 TrySetupChildren,指向动态子菜单时会把 childGetter
//     同步跑完,正是 §4.4 的 >60 s / AppHangB1;所以**不提供 GetNode 兜底**(两者可命中的节点集合
//     完全相同,兜底不可达),W7 审查轮已确认接受本替代。
//  2. 性能硬约束(边界如实声明;S4 复审后的口径):
//     单次 list 的游戏线程工作分**三段,每段各自受 BudgetMs(50 ms)约束**、分别计时上报:
//       ① 动态物化探针 ProbeDynamicMaterialization —— 只遍历 dynamicEntries(动态菜单项),不是 O(index.Count);
//       ② 走查 Walk —— 在 50 ms 内推进索引;
//       ③ 聚合/渲染/打印 —— 扫描 + 组装 Data.nodes + 生成并打印 Lines(打印在此段计时内)。
//     分别上报 probeMs / walkMs / aggregateMs(另有 budgetMs 常量),所以「单次 list 墙钟上限 ≈ 3 × 50 ms
//     + 不可分片的首建」,不是「≤ 50 ms」。
//     只有一段**不受预算约束**:TrySetupNodeGraph() 首建(GenTypes.AllTypes 扫描 + yielder 立即调用,
//     原版原子成本,只发生一次),如实计时上报 buildMs。
//     另外:(c) 动态子菜单(childGetter != null)**只索引不展开** —— 索引期绝不调用
//     TrySetupChildren/childGetter;(d) 目标节点不展开(执行一个菜单路径本来也只是「导航」)。
//     索引状态跨调用保留(静态字段),失效策略见 EnsureIndex 的注释 + 报告。
//  3. Tool* 的鼠标依赖:临时代理 UI.MouseCell() / GenWorld.MouseTile(bool)(Harmony prefix → 执行 →
//     finally unpatch;owner 固定 roslynprobe.debug-action.mouse,只卸自己的前缀)。代理搭建失败**响亮拒绝**;
//     **卸补丁失败也必须响亮**(W7 审查 M1):此时 executed=false、patchLeak=true、error+unsupportedReason
//     都写明 —— 因为补丁留在进程里会把 UI.MouseCell()/GenWorld.MouseTile() 劫持成我们注入的值,
//     直到游戏重启,绝不允许在结果里表现成「干净成功」。
//  4. 参数:mapX/mapZ(地图格)、tile(世界瓦片)、pawnId(§4.4 明确 ActiveNow 不解决「选哪个 pawn」,
//     故 pawnId 是必需注入点)。
//
// 姿势 B(契约 v1.1 §3.5):public static SlashResult Provide(string rest, SlashContext ctx) 是本文件唯一的
//     注入方法名;Bootstrap 写 ctx.DebugAction = DebugActionCommand.Provide;。内核半边 Handler = Handle
//     只写 return ctx.CallDebugAction(rest);(未注入 ⇒ 内核自动回 missing-capability 行)。
//
// Data 契约的收口(审查 N3-a + S1):Provide 的每个出口都过 StableKeys ——
//     * 补齐 canonical 22 键(缺则补默认值、已有键不覆盖)⇒ 任何模式的结果都含这 22 键,
//       消费方按 executed/mode/unsupportedReason 判读永远不会 KeyNotFoundException;
//     * S1 守卫:execute 模式且 executed != true ⇒ unsupportedReason 必须有值(缺则补录并响亮记日志)。
//   守卫只有这一个位置,所以不可被绕过;CanonicalKeyNames() 供测试直接断言键集。
//
// 线程契约(契约 v1.1 §3.4.1 + 舰队规则):判据**只认 ctx.IsGameThread**(不用 Channel 猜,不自造启发式):
//     ctx.IsGameThread            → 就地执行(已在游戏线程)
//     否则 ctx.Executor != null   → Enqueue + TCS + 超时 marshal 到游戏线程
//     否则                        → **明确拒绝**,绝不直读 Unity/Verse(本指令全程要碰 Dialog_Debug/Find/UI)
//
// 两种模式(Kind 一律 "debug-action"):
//   /debug-action                                  → 用法
//   /debug-action list [filter]                    → 分片索引;Data{count,truncated,nodes:[{path,label,type,active}],…}
//   /debug-action refresh                          → 手工丢弃索引(逃生口)
//   /debug-action <路径> [x z|-- tile <n>|-- pawn <id>] → 执行
//                                                   Data{path,label,actionType,visible,active,executed,unsupportedReason,…}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using RoslynProbe.Repl;
using Verse;

namespace RoslynProbe
{
    /// <summary>
    /// /debug-action —— 原版调试动作树的查询与执行(姿势 B:整棵树的操作在游戏侧 provider)。
    /// </summary>
    public static class DebugActionCommand
    {
        /// <summary>登记的 verb(不含 '/')。</summary>
        public const string VerbName = "debug-action";
        /// <summary>SlashResult.Kind。</summary>
        public const string KindName = "debug-action";
        /// <summary>help 分组。</summary>
        public const string GroupName = "调试";

        /// <summary>Harmony owner:只用一次补丁(鼠标代理),卸的时候绝不误伤别人的补丁。</summary>
        private const string HarmonyOwner = "roslynprobe.debug-action.mouse";

        /// <summary>
        /// 单次游戏线程分片的墙钟预算(§4.4 硬约束:50 ms)。**走查与聚合各一份**(不是合计一份):
        /// 见文件头第 2 条的边界声明 —— 单次 list 的墙钟上限 ≈ 2 × BudgetMs + 首建(不可分片)。
        /// </summary>
        public const int BudgetMs = 50;

        /// <summary>list 每次最多回传的节点条数(Data.nodes 的硬上限,防通道被淹)。</summary>
        private const int MaxNodesReturned = 5000;

        /// <summary>list 最多打印到 Lines 的节点条数(管道壳只传 Lines,Data 到不了对端)。</summary>
        private const int MaxLinesPrinted = 200;

        /// <summary>unsupportedReason 里塞技术细节时的截断长度(保持行可读)。</summary>
        private const int ReasonDetailMax = 300;

        /// <summary>marshal 等待上限:与协议层 SessionLine.DefaultTimeoutMs 同源,不自造数字。</summary>
        private const int MarshalTimeoutMs = SessionLine.DefaultTimeoutMs;

        /// <summary>路由结果:在游戏线程(ctx.IsGameThread == true),就地执行。</summary>
        private const int RouteInline = 0;
        /// <summary>路由结果:非游戏线程 + 有 Executor,marshal 到游戏线程再执行。</summary>
        private const int RouteMarshal = 1;
        /// <summary>路由结果:非游戏线程 + 无 Executor,明确拒绝(绝不裸碰 Unity/Verse)。</summary>
        private const int RouteNoChannel = 2;

        private static readonly object Gate = new object();

        // ---------------------------------------------------------------- 索引状态(跨调用保留)

        /// <summary>扁平索引项(路径 + 活的节点引用;active 不缓存,每次 list 现算)。</summary>
        private sealed class IndexEntry
        {
            public DebugActionNode Node;
            public string Path;
            public string Label;
            public string Type;        // Action / Menu / DynamicMenu / ToolMap / ToolWorld / ToolMapForPawns
            /// <summary>索引这一刻该节点的 children.Count;动态菜单靠它检测「之后被游戏物化了新子项」。</summary>
            public int ChildCount;
        }

        private static readonly List<IndexEntry> index = new List<IndexEntry>();
        /// <summary>索引里的 DynamicMenu 项(探针只遍历这一小撮,S4 复审:不能是 O(index.Count))。</summary>
        private static readonly List<IndexEntry> dynamicEntries = new List<IndexEntry>();
        private static readonly HashSet<DebugActionNode> seen = new HashSet<DebugActionNode>();
        private static readonly Queue<DebugActionNode> pending = new Queue<DebugActionNode>();
        private static DebugActionNode indexedRoot;
        private static bool indexComplete;
        private static int dynamicMenus;
        private static long lastBuildMs = -1;
        private static string lastRebuildReason = "";

        // ---------------------------------------------------------------- 注册

        /// <summary>
        /// RegisterAll 反射调用(public static、无参、返回 void)。
        /// 注意:这里**不**记任何线程号 —— 线程判据只认契约 v1.1 的 ctx.IsGameThread(审查 S3)。
        /// </summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = VerbName,
                Usage = "[list [filter]|refresh|<路径> [x z|tile <n>|pawn <id>]] —— 原版调试动作树:分片索引 / 按 node.Path 执行",
                Group = GroupName,
                Kind = KindName,
                Dangerous = true,
                Handler = Handle
            });
        }

        /// <summary>内核半边:本指令的一切都要读游戏侧的对象树 ⇒ 一律委派 provider(未注入 → missing-capability 行)。</summary>
        private static SlashResult Handle(string rest, SlashContext ctx)
        {
            return ctx.CallDebugAction(rest ?? "");
        }

        // ================================================================ 姿势 B provider(游戏侧)

        /// <summary>
        /// provider 入口(契约 v1.1 §3.5 的注入方法名与签名;Bootstrap 写
        /// <c>ctx.DebugAction = DebugActionCommand.Provide;</c>)。
        /// 纯语法错误就地返回(零 Verse,不必上游戏线程);其余按舰队线程规则路由。本方法绝不抛。
        /// </summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            DebugActionRequest req;
            string why;
            if (!DebugActionRequest.TryParse(rest, out req, out why))
            {
                // 纯语法错误:零 Verse,不必上游戏线程 —— 仍然是执行形状 + 唯一出口补键。
                string attempted = (rest ?? "").Trim();
                return StableKeys(ExecuteResult(new ExecOutcome
                {
                    RequestedPath = attempted,
                    Path = attempted,
                    UnsupportedReason = why,
                    Error = why
                }, 0, true), "execute");
            }
            return StableKeys(ProvideCore(req, ctx), ModeName(req.Mode));
        }

        /// <summary>已解析请求的实际处理(不负责 Data 形状);所有出口都经 <see cref="StableKeys"/> 收口。</summary>
        private static SlashResult ProvideCore(DebugActionRequest req, SlashContext ctx)
        {
            if (req.Mode == DebugActionRequest.Intent.Usage)
                return UsageResult();

            SlashContext c = ctx ?? new SlashContext();
            int route = RouteFor(c.IsGameThread, c.Executor != null);

            if (route == RouteNoChannel)
                return CoreError(ModeName(req.Mode),
                    "拒绝执行:本次调用不在游戏线程(ctx.IsGameThread = false;ctx.Channel = " + ChannelText(c) + "),"
                    + "且 ctx.Executor 未接线 —— 无法 marshal 到游戏更新线程。本指令要读 Unity/Verse 对象树"
                    + "(Dialog_Debug / Find / UI),在会话读线程上裸碰是未定义行为,故拒绝。");

            try
            {
                if (route == RouteInline) return DispatchLocked(req);
                return RunOnGameThread(req, c);
            }
            catch (Exception ex)
            {
                return CoreError(ModeName(req.Mode), "游戏侧执行失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>模式名(小写):让每个 Data 都能自报「这是哪条路径的结果」。</summary>
        private static string ModeName(DebugActionRequest.Intent mode)
        {
            switch (mode)
            {
                case DebugActionRequest.Intent.List: return "list";
                case DebugActionRequest.Intent.Refresh: return "refresh";
                case DebugActionRequest.Intent.Execute: return "execute";
                default: return "usage";
            }
        }

        /// <summary>
        /// 失败结果(**未过 StableKeys 前的原始形状**):<c>{ok:false, mode, error, unsupportedReason}</c>。
        /// 只用于「拒绝执行」与「内部失败」这两类非模式特有的失败 —— 它们随后一律经
        /// <see cref="StableKeys"/> 补齐 canonical 键,所以对外永远不是这个 4 键形状(审查 N3-a)。
        /// </summary>
        private static SlashResult CoreError(string mode, string message)
        {
            SlashResult r = SlashResults.Error(KindName, message);
            r.Data["mode"] = mode;
            r.Data["unsupportedReason"] = message;
            return r;
        }

        /// <summary>
        /// canonical(执行形状)的 22 个键。**任何模式的任何结果**都保证含这 22 个键 —— 由
        /// <see cref="StableKeys"/> 在 Provide 的唯一出口补齐(已有键不覆盖)。
        /// </summary>
        private static readonly string[] CanonicalKeys =
        {
            "ok", "mode", "path", "requestedPath", "label", "actionType",
            "visible", "active", "visibleError", "activeError",
            "executed", "unsupportedReason", "error", "patchLeak",
            "mapX", "mapZ", "tile", "pawnId", "singleInt",
            "resolved", "foldedArgs", "elapsedMs"
        };

        /// <summary>
        /// **唯一出口**(Provide 末端;W6 的 StableKeys 同款形状):
        /// 1) 补齐 canonical 22 键(缺则给默认值:**已有键不覆盖**),于是消费方按 §executed§ / §mode§ 判读
        ///    永远不会 KeyNotFoundException(审查 N3-a);
        /// 2) **S1 守卫**:execute 模式且 §executed != true§ 时,§unsupportedReason§ 必须有值 —— 缺失就自己补录
        ///    (优先用 §error§ 的原文)并响亮记一条日志。守卫在这里、不在各调用点,所以它**不可被绕过**;
        ///    测试 §DebugActionCommandTests.StableKeysCases§ 直接打这个出口,删掉守卫立刻变红(可被变异检出)。
        /// </summary>
        public static SlashResult StableKeys(SlashResult r, string mode)
        {
            if (r == null) return r;
            if (r.Data == null) r.Data = new Dictionary<string, object>();
            Dictionary<string, object> d = r.Data;

            if (!d.ContainsKey("mode") || d["mode"] == null) d["mode"] = mode ?? "";
            SetDefault(d, "ok", false);
            SetDefault(d, "path", null);
            SetDefault(d, "requestedPath", null);
            SetDefault(d, "label", null);
            SetDefault(d, "actionType", null);
            SetDefault(d, "visible", false);
            SetDefault(d, "active", false);
            SetDefault(d, "visibleError", null);
            SetDefault(d, "activeError", null);
            SetDefault(d, "executed", false);
            SetDefault(d, "unsupportedReason", null);
            SetDefault(d, "error", null);
            SetDefault(d, "patchLeak", false);
            SetDefault(d, "mapX", null);
            SetDefault(d, "mapZ", null);
            SetDefault(d, "tile", null);
            SetDefault(d, "pawnId", null);
            SetDefault(d, "singleInt", null);
            SetDefault(d, "resolved", false);
            SetDefault(d, "foldedArgs", null);
            SetDefault(d, "elapsedMs", null);

            // ---- S1 守卫(唯一出口,不可绕过)----
            // 判定用「本次结果**自报**的 mode」(Data.mode,缺则用调用方声明的 mode),保证自洽:
            // list/refresh/usage 的 executed=false 只是「没执行」而不是失败,不该被补录原因。
            string effectiveMode = d["mode"] as string;
            if (effectiveMode == null) effectiveMode = mode;
            if (string.Equals(effectiveMode, "execute", StringComparison.Ordinal) && !(d["executed"] is bool exec1 && exec1))
            {
                string reason = d["unsupportedReason"] as string;
                if (string.IsNullOrEmpty(reason))
                {
                    string detail = d["error"] as string;
                    d["unsupportedReason"] = !string.IsNullOrEmpty(detail)
                        ? "执行失败: " + Shorten(detail)
                        : "未执行(守卫补录:调用方没有给出原因)";
                    try
                    {
                        Verse.Log.ErrorOnce(
                            "[Roslyn] /debug-action: StableKeys 守卫补录 unsupportedReason —— 说明某条路径忘了给原因(实现缺陷): "
                            + d["unsupportedReason"], "RoslynProbe.DebugAction.Guard".GetHashCode());
                    }
                    catch (Exception)
                    {
                        // 记日志失败绝不能把结果路径带崩(守卫的职责是保证结果可读,不是保证日志)。
                    }
                }
            }
            return r;
        }

        /// <summary>canonical 键名快照(测试按它断言「任何结果的 Data 都含这 22 键」,避免测试里再抄一份)。</summary>
        public static string[] CanonicalKeyNames()
        {
            var copy = new string[CanonicalKeys.Length];
            Array.Copy(CanonicalKeys, copy, CanonicalKeys.Length);
            return copy;
        }

        /// <summary>缺键才补(W6 同款:已有键绝不覆盖,保住各模式自己的语义)。</summary>
        private static void SetDefault(Dictionary<string, object> d, string key, object value)
        {
            if (!d.ContainsKey(key)) d[key] = value;
        }

        /// <summary>舰队线程规则(契约 v1.1 §3.4.1):只认 IsGameThread 这个事实位,不用 Channel 猜。</summary>
        private static int RouteFor(bool onGameThread, bool hasExecutor)
        {
            if (onGameThread) return RouteInline;
            return hasExecutor ? RouteMarshal : RouteNoChannel;
        }

        private static string ChannelText(SlashContext ctx)
        {
            if (ctx == null || ctx.Channel == null) return "<null>";
            return "\"" + ctx.Channel + "\"";
        }

        /// <summary>读线程 → 游戏线程,阻塞等结果(与 SessionEval.EnqueueEval 同款)。</summary>
        private static SlashResult RunOnGameThread(DebugActionRequest req, SlashContext ctx)
        {
            Executor exec = ctx.Executor;
            SlashResult boxed = null;
            ReplRequest rq = exec.Enqueue(delegate { boxed = DispatchLocked(req); return ReplResult.Success(null); }, MarshalTimeoutMs);

            try
            {
                if (!rq.Tcs.Task.Wait(MarshalTimeoutMs))
                {
                    exec.MarkTimedOut(rq.Id);
                    return SlashResults.Error(KindName,
                        "marshal 到游戏线程等待超过 " + MarshalTimeoutMs + " ms(与协议层 SessionLine.DefaultTimeoutMs 同源):"
                        + "动作体可能仍在游戏线程上执行,但本次调用不再等它 —— 结果未知,不会重试。");
                }
            }
            catch (Exception ex)
            {
                return SlashResults.Error(KindName, "marshal 到游戏线程失败: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (boxed == null)
            {
                ReplResult rr = rq.Tcs.Task.Result;
                return SlashResults.Error(KindName,
                    "游戏侧未产出结果(内部错误)" + (rr != null && !string.IsNullOrEmpty(rr.Error) ? ": " + rr.Error : ""));
            }
            return boxed;
        }

        private static SlashResult DispatchLocked(DebugActionRequest req)
        {
            lock (Gate) { return DispatchGameSide(req); }
        }

        private static SlashResult DispatchGameSide(DebugActionRequest req)
        {
            switch (req.Mode)
            {
                case DebugActionRequest.Intent.List: return BuildList(req);
                case DebugActionRequest.Intent.Refresh: return BuildRefresh();
                case DebugActionRequest.Intent.Execute: return BuildExecute(req);
                default: return UsageResult();
            }
        }

        // ---------------------------------------------------------------- 用法

        private static SlashResult UsageResult()
        {
            SlashResult usage = SlashResults.Usage(KindName,
                "用法: /debug-action list [filter]      —— 分片索引原版调试动作树(走查 50 ms/次,可反复调) ",
                "      /debug-action refresh            —— 丢弃索引,下次 list 从零重建",
                "      /debug-action <路径>            —— 执行:路径就是 node.Path(反斜杠分隔),如 Actions\\General\\...",
                "      /debug-action <路径> x z        —— ToolMap:注入地图格(x/z 为整数格坐标)",
                "      /debug-action <路径> tile <n>   —— ToolWorld:注入世界瓦片 n",
                "      /debug-action <路径> pawn <id>  —— ToolMapForPawns:注入 pawnId(thingIDNumber)",
                "  路径含空格时用 \" -- \" 显式分隔路径与参数,如: /debug-action Frame profiler",
                "  Tool* 的鼠标读数由临时 Harmony 前缀代理(执行后立刻卸)。代理不可用、或补丁卸不掉,都算失败:",
                "  结果里 executed=false,原因写进 unsupportedReason(error 给技术细节,patchLeak 标记补丁泄漏)。");
            usage.Data["mode"] = "usage";   // 公共核心键:任何 Data 都自报 mode
            return usage;
        }

        // ---------------------------------------------------------------- 索引(分片)

        /// <summary>
        /// 失效策略(唯一需要重建的触发 = 游戏自己重建了树):
        ///   PlayDataLoader 在「换档/载入/重载 Defs」时调 Dialog_Debug.ResetStaticData()(rootNode = null,
        ///   roots.Clear())。下一次 TrySetupNodeGraph() 会**换一个新的 rootNode 对象** ⇒
        ///   我们只比较 Dialog_Debug.rootNode 的**对象身份**:一旦不等,丢掉整份索引重建。
        ///   这比 tick 轮询或按游戏状态猜都更准:节点路径只由 label 拼成,只有树被重建才会变。
        ///   另有 list refresh 作为手工逃生口(例如 mod 运行时改了 label);
        ///   动态菜单之后被游戏 UI 物化出来的子项由 ProbeDynamicMaterialization 补齐(审查 S5)。
        /// </summary>
        private static void EnsureIndex()
        {
            if (indexedRoot != null && !ReferenceEquals(Dialog_Debug.rootNode, indexedRoot))
                DropIndex("Dialog_Debug.rootNode 身份变化(游戏重建了调试动作树)");

            if (Dialog_Debug.rootNode == null)
            {
                // 不可分片的一次性成本:GenTypes.AllTypes × GetMethods × [DebugAction] 扫描 +
                // DebugActionYielderAttribute 方法的**立即**调用(DebugTabMenu_Actions.InitActions)。
                // 它在原版里原子发生,我们只能如实计时并报出来(buildMs)。
                Stopwatch build = Stopwatch.StartNew();
                Dialog_Debug.TrySetupNodeGraph();
                build.Stop();
                lastBuildMs = build.ElapsedMilliseconds;
            }

            if (Dialog_Debug.rootNode == null)
                throw new InvalidOperationException("Dialog_Debug.TrySetupNodeGraph() 之后 rootNode 仍为 null");

            if (indexedRoot == null)
            {
                indexedRoot = Dialog_Debug.rootNode;
                indexComplete = false;
                dynamicMenus = 0;
                seen.Clear();
                index.Clear();
                dynamicEntries.Clear();
                pending.Clear();

                List<DebugActionNode> tabs = indexedRoot.children;
                if (tabs != null)
                    for (int i = 0; i < tabs.Count; i++)
                        if (tabs[i] != null) pending.Enqueue(tabs[i]);
                if (pending.Count == 0) indexComplete = true;
            }
        }

        private static void DropIndex(string reason)
        {
            indexedRoot = null;
            indexComplete = false;
            dynamicMenus = 0;
            seen.Clear();
            index.Clear();
            dynamicEntries.Clear();
            pending.Clear();
            lastBuildMs = -1;
            lastRebuildReason = reason ?? "";
        }

        /// <summary>
        /// 索引走查:在 50 ms 预算内推进,返回本次实际耗时(ms)。
        /// 永不调用 childGetter / TrySetupChildren —— 动态菜单与 Tool* 一并只索引。
        /// </summary>
        private static long Walk()
        {
            Stopwatch sw = Stopwatch.StartNew();
            if (!indexComplete)
            {
                while (pending.Count > 0 && sw.ElapsedMilliseconds < BudgetMs)
                {
                    DebugActionNode node = pending.Dequeue();
                    if (node == null || !seen.Add(node)) continue;

                    string type = ClassifyNode(node);
                    if (type == TypeDynamicMenu) dynamicMenus++;

                    List<DebugActionNode> kids = node.children;
                    var entry = new IndexEntry
                    {
                        Node = node,
                        Path = PathSafe(node),
                        Label = node.label ?? "",
                        Type = type,
                        ChildCount = kids == null ? 0 : kids.Count
                    };
                    index.Add(entry);
                    if (type == TypeDynamicMenu) dynamicEntries.Add(entry);

                    // 动态子菜单**只索引不展开**(§4.4 性能硬约束):childGetter 同步执行是 >60 s 的元凶,
                    // 这里绝不调用 TrySetupChildren。已经物化的子项(游戏自己展开过该菜单)顺带索引 ——
                    // 零成本,也不会给 childGetter 任何被调用的机会。
                    if (kids == null) continue;
                    for (int i = 0; i < kids.Count; i++)
                    {
                        DebugActionNode c = kids[i];
                        if (c != null && !seen.Contains(c)) pending.Enqueue(c);
                    }
                }
                if (pending.Count == 0) indexComplete = true;
            }
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }

        /// <summary>
        /// 审查 S5:索引走完之后,游戏 UI 仍可能把某个动态菜单展开(childGetter 被游戏自己调),
        /// 新物化出来的子项**永远补不进索引**(Walk 会因 indexComplete 早退)。
        /// 这里只对已索引的 DynamicMenu 项做一次廉价探针:比较 children.Count 与索引时的 ChildCount,
        /// 有新子项就入队并撤销 indexComplete(下一次 Walk 覆盖它们);**依然绝不调用 childGetter**。
        ///
        /// 预算口径(S4 复审):本段**只遍历 <see cref="dynamicEntries"/>**(动态菜单项,通常几十个),
        /// 不再扫整个 index;并且自带一段 50 ms 预算,超了就停并置 budgetHit —— 所以它既不是 O(index.Count),
        /// 也不是无预算段。返回本次发现「已物化但未索引」的节点数(0 = 没有陈旧)。
        /// </summary>
        private static int ProbeDynamicMaterialization(out bool budgetHit, out long probeMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            budgetHit = false;
            int unindexed = 0;
            for (int i = 0; i < dynamicEntries.Count; i++)
            {
                if (sw.ElapsedMilliseconds >= BudgetMs) { budgetHit = true; break; }
                IndexEntry e = dynamicEntries[i];
                if (e.Type != TypeDynamicMenu) continue;
                List<DebugActionNode> kids = e.Node.children;
                int n = kids == null ? 0 : kids.Count;
                if (n == e.ChildCount) continue;
                e.ChildCount = n;
                for (int k = 0; k < n; k++)
                {
                    DebugActionNode c = kids[k];
                    if (c == null || seen.Contains(c)) continue;
                    unindexed++;
                    pending.Enqueue(c);
                }
            }
            if (unindexed > 0) indexComplete = false;
            sw.Stop();
            probeMs = sw.ElapsedMilliseconds;
            return unindexed;
        }

        private const string TypeAction = "Action";
        private const string TypeMenu = "Menu";
        private const string TypeDynamicMenu = "DynamicMenu";
        private const string TypeToolMap = "ToolMap";
        private const string TypeToolWorld = "ToolWorld";
        private const string TypeToolMapForPawns = "ToolMapForPawns";

        private static string ClassifyNode(DebugActionNode node)
        {
            // 有 childGetter ⇒ 动态子菜单。children 里可能已有内容(游戏自己展开过),但我们**不**去确认,
            // 也绝不调用 TrySetupChildren:无法从公开面区分「childrenSetup 已置位」与「还没展开」。
            if (node.childGetter != null) return TypeDynamicMenu;
            if (node.children != null && node.children.Count > 0) return TypeMenu;

            switch (node.actionType)
            {
                case DebugActionType.ToolMap: return TypeToolMap;
                case DebugActionType.ToolWorld: return TypeToolWorld;
                case DebugActionType.ToolMapForPawns: return TypeToolMapForPawns;
                default: return TypeAction;
            }
        }

        private static SlashResult BuildList(DebugActionRequest req)
        {
            Stopwatch call = Stopwatch.StartNew();
            EnsureIndex();

            // ---- 三段各自受 BudgetMs 约束(S4 复审口径):① 动态物化探针 ② 走查 ③ 聚合+渲染+打印 ----
            bool probeBudgetHit;
            long probeMs;
            int materializedUnindexed = ProbeDynamicMaterialization(out probeBudgetHit, out probeMs);
            long walkMs = Walk();

            Stopwatch agg = Stopwatch.StartNew();
            var nodes = new List<Dictionary<string, object>>();
            int matched = 0;
            int returned = 0;
            int scanned = 0;
            int activeFailures = 0;
            bool aggregateBudgetHit = false;
            for (int i = 0; i < index.Count; i++)
            {
                if (agg.ElapsedMilliseconds >= BudgetMs) { aggregateBudgetHit = true; break; }
                scanned++;
                IndexEntry e = index[i];
                if (!Matches(e, req.Filter)) continue;
                matched++;
                if (returned >= MaxNodesReturned) continue;
                bool active = TryActiveNow(e.Node, out string activeError);
                if (activeError != null) activeFailures++;
                nodes.Add(new Dictionary<string, object>
                {
                    ["path"] = e.Path,
                    ["label"] = e.Label,
                    ["type"] = e.Type,
                    ["active"] = active
                });
                returned++;
            }

            int remaining = pending.Count;
            bool capped = matched > returned;

            // ---- 打印阶段:同样在 agg 计时内、同样受 BudgetMs 约束(W7-fix3)----
            // 单独记 printBudgetHit(不再往扫描那个标志里塞),这样 Lines 能点名究竟是哪一段被截断。
            var printedLines = new List<string>();
            bool printBudgetHit = false;
            for (int i = 0; i < nodes.Count && printedLines.Count < MaxLinesPrinted; i++)
            {
                if (agg.ElapsedMilliseconds >= BudgetMs) { printBudgetHit = true; break; }
                Dictionary<string, object> n = nodes[i];
                printedLines.Add("  " + Pad((string)n["type"], 17)
                    + ((bool)n["active"] ? "active  " : "inactive") + "  " + (string)n["path"]);
            }
            agg.Stop();   // ← 所有预算判定到此全部落定

            // ---- 唯一的渲染点(W7-fix3 核心):Lines 与 Data 都只从这一个 ListFacts 取值 ----
            // 修掉了 fix2 引入的两处自相矛盾:① 汇总行原先在打印之前就拼好 ⇒「仅打印被预算截断」时
            // 它会印 truncated=false 而 Data 是 true;② 它印的聚合耗时是打印前取的 ⇒ 少报打印时间。
            var facts = new ListFacts
            {
                Indexed = index.Count,
                Matched = matched,
                Scanned = scanned,
                Returned = returned,
                Printed = printedLines.Count,
                Remaining = remaining,
                DynamicMenus = dynamicMenus,
                ActiveFailures = activeFailures,
                MaterializedUnindexed = materializedUnindexed,
                Capped = capped,
                ScanBudgetHit = aggregateBudgetHit,
                PrintBudgetHit = printBudgetHit,
                ProbeBudgetHit = probeBudgetHit,
                BuildMs = lastBuildMs,
                ProbeMs = probeMs,
                WalkMs = walkMs,
                AggregateMs = agg.ElapsedMilliseconds,
                NodesTotal = nodes.Count,
                MaxNodesReturned = MaxNodesReturned,
                MaxLinesPrinted = MaxLinesPrinted
            };

            // 索引被丢弃的原因只报告一次(便于真机核对「换档后是否真的重建了」)。
            string rebuildReason = lastRebuildReason.Length > 0 ? lastRebuildReason : null;
            lastRebuildReason = "";

            var r = new SlashResult { Kind = KindName };
            r.Lines.AddRange(ListSummaryLines(facts));   // 汇总两行,与 Data 同源
            if (dynamicMenus > 0)
                r.Lines.Add("  注:动态子菜单只索引未展开(childGetter 绝不调用);" + dynamicMenus
                    + " 个菜单项本身在 nodes 里(type=DynamicMenu),其内容要么由游戏 UI 展开后再 list,要么用更深路径直接执行。");
            if (materializedUnindexed > 0)
                r.Lines.Add("  注:检测到游戏刚物化了 " + materializedUnindexed + " 个新子项,已并入待走查(下次 list 覆盖)。");
            if (activeFailures > 0)
                r.Lines.Add("  注:有 " + activeFailures + " 个节点的 ActiveNow 判定抛异常(active 报 false,但那是「判不出」不是「不可用」)。");
            r.Lines.AddRange(printedLines);
            if (nodes.Count > printedLines.Count)
                r.Lines.Add("  … 另有 " + (nodes.Count - printedLines.Count) + " 条只存在于 Data.nodes(管道壳只传 Lines)");

            call.Stop();
            r.Data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["mode"] = "list",                    // canonical 键(其余 canonical 键由 StableKeys 补齐)
                ["count"] = facts.Returned,           // = Data.nodes 的条数(BRIEF 口径)
                ["returned"] = facts.Returned,        // count 的显式同义键(审查 N4:消除 count 语义歧义)
                ["matched"] = facts.Matched,          // 本次扫描命中的总数(可能 > count)
                ["scanned"] = facts.Scanned,          // 本次扫描的索引项数(scanBudgetHit 时 < indexed)
                ["truncated"] = facts.Truncated,      // 与 Lines 汇总行**同一个值**(同一个 ListFacts)
                ["capped"] = facts.Capped,
                ["aggregateBudgetHit"] = facts.ScanBudgetHit || facts.PrintBudgetHit,   // 兼容旧键:聚合段整体是否被截断
                ["scanBudgetHit"] = facts.ScanBudgetHit,
                ["printBudgetHit"] = facts.PrintBudgetHit,
                ["remaining"] = facts.Remaining,
                ["indexed"] = facts.Indexed,
                ["walkDone"] = facts.Remaining == 0,
                ["dynamicMenus"] = facts.DynamicMenus,
                ["dynamicMenusIndexOnly"] = facts.DynamicMenus > 0,
                ["activeFailures"] = facts.ActiveFailures,
                ["materializedUnindexed"] = facts.MaterializedUnindexed,
                ["filter"] = req.Filter ?? "",
                ["buildMs"] = facts.BuildMs,
                ["rebuildReason"] = rebuildReason,
                ["budgetMs"] = BudgetMs,
                ["probeMs"] = facts.ProbeMs,
                ["probeBudgetHit"] = facts.ProbeBudgetHit,
                ["walkMs"] = facts.WalkMs,
                ["aggregateMs"] = facts.AggregateMs,  // 含打印(agg.Stop() 在打印之后)
                ["elapsedMs"] = call.ElapsedMilliseconds,
                ["nodes"] = nodes
            };
            return r;
        }

        /// <summary>
        /// list 的**最终事实**:所有预算判定落定之后一次性装好。**Lines 的汇总行与 Data 都只从这里取值**,
        /// 于是同一个量（truncated / 各段耗时 / 条数）不可能在两处给出不同说法（W7-fix3）。
        /// 公开 + 纯函数 ⇒ 测试可以直接构造「仅打印被预算截断」这类离线难以自然出现的组合并断言渲染。
        /// </summary>
        public sealed class ListFacts
        {
            public int Indexed;
            public int Matched;
            public int Scanned;
            public int Returned;
            public int Printed;
            public int Remaining;
            public int DynamicMenus;
            public int ActiveFailures;
            public int MaterializedUnindexed;
            public int NodesTotal;
            public int MaxNodesReturned;
            public int MaxLinesPrinted;
            public bool Capped;
            public bool ScanBudgetHit;
            public bool PrintBudgetHit;
            public bool ProbeBudgetHit;
            public long BuildMs;
            public long ProbeMs;
            public long WalkMs;
            public long AggregateMs;

            /// <summary>唯一口径:结果真的不全才为 true（走查没走完 / 回传被上限截断 / 任一段被预算截断）。</summary>
            public bool Truncated
            {
                get { return Remaining > 0 || Capped || ScanBudgetHit || PrintBudgetHit || ProbeBudgetHit; }
            }
        }

        /// <summary>
        /// list 的两行汇总(人读)。纯函数:只读 ListFacts,不碰任何静态状态 ⇒ 与 Data 同源且可单测。
        /// 注意它必须在 <c>agg.Stop()</c> **之后**调用(那时 Printed / AggregateMs / 各 BudgetHit 才是终值)。
        /// </summary>
        public static string ListSummaryLine(ListFacts f)
        {
            if (f == null) return "/debug-action list: (无 ListFacts)";
            var sb = new StringBuilder();
            sb.Append("/debug-action list: 已索引 ").Append(f.Indexed).Append(" 个节点")
              .Append(f.Remaining > 0 ? "(本次预算用尽,还有 " + f.Remaining + " 个待走查)" : "(走查完成)")
              .Append(";动态子菜单 ").Append(f.DynamicMenus).Append(" 个只索引未展开(§4.4)");
            sb.Append("\n  匹配 ").Append(f.Matched).Append(" 条(本次扫描 ").Append(f.Scanned).Append("/").Append(f.Indexed)
              .Append(" 项),回传 ").Append(f.Returned).Append(" 条")
              .Append(f.Capped ? "(已达 " + f.MaxNodesReturned + " 条上限,用 filter 收窄)" : "")
              .Append(";truncated=").Append(f.Truncated ? "true" : "false")
              .Append("  [预算 ").Append(BudgetMs).Append(" ms/段:探针 ").Append(f.ProbeMs)
              .Append(" + 走查 ").Append(f.WalkMs).Append(" + 聚合/渲染/打印 ").Append(f.AggregateMs).Append(" ms]")
              .Append(f.ProbeBudgetHit ? " 探针被预算截断" : "")
              .Append(f.ScanBudgetHit ? " 扫描被预算截断" : "")
              .Append(f.PrintBudgetHit ? " 打印被预算截断(已打印 " + f.Printed + "/" + f.Returned + " 条)" : "")
              .Append(f.BuildMs >= 0 ? "  首建 " + f.BuildMs + " ms(不可分片)" : "");
            return sb.ToString();
        }

        /// <summary>
        /// 供 Lines 使用:把汇总的两行拆成两条(第一行给人与管道都易读)。与 ListSummaryLine 同源,不另算。
        /// </summary>
        public static string[] ListSummaryLines(ListFacts f)
        {
            return ListSummaryLine(f).Split('\n');
        }

        private static SlashResult BuildRefresh()
        {
            int dropped = index.Count;
            DropIndex(null);   // 手工丢弃不进 rebuildReason(本结果已经说明白了)

            var r = new SlashResult { Kind = KindName };
            r.Lines.Add("调试动作树索引已丢弃(" + dropped + " 个节点);下次 list 从零重建。");
            r.Data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["mode"] = "refresh",
                ["dropped"] = dropped,
                ["note"] = "下次 list 从零重建"
            };
            return r;
        }

        private static bool Matches(IndexEntry e, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            if (e.Path != null && e.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (e.Label != null && e.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ---------------------------------------------------------------- 执行

        /// <summary>
        /// 一次执行尝试的全部终值。**两者用同一个类承载**,因此成功/拒绝/失败/语法错误四条路径
        /// 的 Data 键集合**完全一致**(审查 N3:Data 键名稳定)。
        /// 不变式(报告与 Data 同源):
        ///   executed == true  ⟺ ok == true ⟺ 动作体跑完且没抛、且(若有)临时补丁已卸干净;
        ///   unsupportedReason != null ⟺ executed == false (拒绝理由,或失败要点);
        ///   error != null ⟺ 有技术细节(异常原文 / 补丁操作失败原文 / 语法错误消息);
        ///   patchLeak == true ⟹ 进程里留下了未卸掉的补丁(严重,见 Lines 与日志)。
        /// </summary>
        private sealed class ExecOutcome
        {
            public string Path = "";
            public string RequestedPath = "";
            public string Label = "";
            public string ActionType = "";
            public bool Visible;
            public bool Active;
            public string VisibleError;
            public string ActiveError;
            public bool Executed;
            public string UnsupportedReason;
            public string Error;
            public bool PatchLeak;
            public bool Resolved;
            public string FoldedArgs;
            public int? MapX, MapZ, Tile, PawnId, SingleInt;
        }

        private static SlashResult BuildExecute(DebugActionRequest req)
        {
            Stopwatch call = Stopwatch.StartNew();
            var o = new ExecOutcome { RequestedPath = req.Path ?? "", Path = req.Path ?? "" };

            DebugActionRequest.Args args;
            string argError;
            if (!DebugActionRequest.Args.TryParse(req.ArgsRaw, out args, out argError))
            {
                // 纯语法错误:从没尝试执行 ⇒ unsupportedReason 与 error 同为该消息。
                o.UnsupportedReason = argError;
                o.Error = argError;
                return ExecuteResult(o, call.ElapsedMilliseconds, true);
            }

            o.MapX = args.HasMapCell ? (int?)args.MapX : null;
            o.MapZ = args.HasMapCell ? (int?)args.MapZ : null;
            o.Tile = args.HasTile ? (int?)args.Tile : null;
            o.PawnId = args.HasPawn ? (int?)args.PawnId : null;
            o.SingleInt = args.HasSingleInt ? (int?)args.SingleInt : null;

            DebugActionNode node = null;
            try
            {
                Dialog_Debug.TrySetupNodeGraph();
                node = FindNodeNoExpand(o.Path);
                if (node == null && !req.ExplicitSeparator && req.ArgsRaw.Length > 0)
                {
                    // 位置式切分可能把「末段含数字的路径」切错(如 …\Spawn 1)⇒ 回落到「整串当路径」。
                    string whole = (o.Path + " " + req.ArgsRaw).Trim();
                    DebugActionNode alt = FindNodeNoExpand(whole);
                    if (alt != null)
                    {
                        node = alt;
                        o.FoldedArgs = req.ArgsRaw;
                        o.Path = whole;
                        // 保守:整串是路径 ⇒ 参数作废,绝不带着半截参数(比如 x 有了 z 没有)去执行。
                        o.MapX = o.MapZ = o.Tile = o.PawnId = o.SingleInt = null;
                        req.ArgsRaw = "";
                    }
                }
            }
            catch (Exception ex)
            {
                o.UnsupportedReason = "解析节点失败: " + ex.GetType().Name + ": " + ex.Message;
                o.Error = o.UnsupportedReason;
                return ExecuteResult(o, call.ElapsedMilliseconds, false);
            }

            if (node == null)
            {
                o.UnsupportedReason = "节点不存在: " + o.Path
                    + " —— 路径必须与 node.Path 逐段相等(反斜杠分隔,区分大小写)。先 /debug-action list 或 list <filter> 取路径。";
                return ExecuteResult(o, call.ElapsedMilliseconds, false);
            }

            o.Resolved = true;
            o.Path = PathSafe(node);
            o.Label = node.label ?? "";
            o.ActionType = node.actionType.ToString();

            o.Visible = TryVisibleNow(node, out o.VisibleError);
            o.Active = TryActiveNow(node, out o.ActiveError);

            bool patchLeak = false;
            if (o.VisibleError != null)
                o.UnsupportedReason = "VisibleNow 判定失败(异常: " + Shorten(o.VisibleError)
                    + ") —— 判不出就不下结论、不执行";
            else if (!o.Visible)
                o.UnsupportedReason = "VisibleNow=false:当前游戏状态不允许该动作(原版 AllowedGameStates / visibilityGetter 判定)";
            else if (o.ActiveError != null)
                o.UnsupportedReason = "ActiveNow 判定失败(异常: " + Shorten(o.ActiveError)
                    + ") —— 判不出就不下结论、不执行";
            else if (!o.Active)
                o.UnsupportedReason = "ActiveNow=false:动作要求另一个视图(ToolMap/ToolMapForPawns 要在地图视图、ToolWorld 要在世界视图)";
            else if (node.childGetter != null)
                o.UnsupportedReason = "该节点是动态子菜单(childGetter),按 §4.4 不展开、也不可执行;请给更深的路径(其子节点可由原版 UI 展开后再 list)";
            else if (node.children != null && node.children.Count > 0)
                o.UnsupportedReason = "该节点是子菜单,不是可执行动作;" + ChildHint(node);
            else
            {
                switch (node.actionType)
                {
                    case DebugActionType.ToolMap:
                        if (node.action == null) o.UnsupportedReason = "该 ToolMap 节点没有执行体(action == null)";
                        else if (!args.HasMapCell) o.UnsupportedReason = "ToolMap 需要地图格坐标:/debug-action <path> x z(或 -- x z)";
                        else o.Error = InvokeWithMouseCell(new IntVec3(args.MapX, 0, args.MapZ), node.action, out o.Executed, out patchLeak);
                        break;

                    case DebugActionType.ToolWorld:
                        if (node.action == null) o.UnsupportedReason = "该 ToolWorld 节点没有执行体(action == null)";
                        else if (!args.HasTile && !args.HasSingleInt)
                            o.UnsupportedReason = "ToolWorld 需要世界瓦片:/debug-action <path> tile <n>(单个整数也按瓦片解释)";
                        else
                        {
                            int tile = args.HasTile ? args.Tile : args.SingleInt;
                            o.Error = InvokeWithWorldTile(tile, node.action, out o.Executed, out patchLeak);
                        }
                        break;

                    case DebugActionType.ToolMapForPawns:
                        if (node.pawnAction == null) o.UnsupportedReason = "该 ToolMapForPawns 节点没有执行体(pawnAction == null)";
                        else if (!args.HasPawn && !args.HasSingleInt)
                            o.UnsupportedReason = "ToolMapForPawns 需要 pawnId:/debug-action <path> pawn <thingIDNumber>(§4.4:ActiveNow 不解决选哪个 pawn)";
                        else
                        {
                            int pawnId = args.HasPawn ? args.PawnId : args.SingleInt;
                            string pawnLookupError;
                            Pawn pawn = FindPawnById(pawnId, out pawnLookupError);
                            if (pawnLookupError != null)
                                o.UnsupportedReason = "pawn 查找失败(异常: " + Shorten(pawnLookupError)
                                    + ") —— 判不出就不下结论、不执行";
                            else if (pawn == null)
                                o.UnsupportedReason = "找不到 thingIDNumber=" + pawnId + " 的 Pawn(只查已加载地图上的 pawn)";
                            else
                            {
                                Pawn captured = pawn;
                                Action body = delegate { node.pawnAction(captured); };
                                // pawnAction 本体也可能读鼠标 ⇒ 顺便注入坐标(给了才注入)。
                                o.Error = args.HasMapCell
                                    ? InvokeWithMouseCell(new IntVec3(args.MapX, 0, args.MapZ), body, out o.Executed, out patchLeak)
                                    : InvokeGuarded(body, out o.Executed);
                            }
                        }
                        break;

                    default:
                        if (node.action == null) o.UnsupportedReason = "该节点没有执行体(action == null)";
                        else if (args.HasAny) o.UnsupportedReason = "该动作不接受坐标 / pawnId 参数(参数只对 Tool* 有意义)";
                        else o.Error = InvokeGuarded(node.action, out o.Executed);
                        break;
                }
            }

            o.PatchLeak = patchLeak;

            // 不变式对齐(审查 S1 + M1):**没执行就一定给得出原因** —— 拒绝理由与失败要点都进 unsupportedReason。
            if (!o.Executed)
            {
                if (o.UnsupportedReason == null)
                    o.UnsupportedReason = o.Error != null
                        ? "执行失败: " + Shorten(o.Error)
                        : "未执行(内部原因未记录)";
                else if (o.Error != null)
                    o.UnsupportedReason = o.UnsupportedReason + " ; 执行失败: " + Shorten(o.Error);
            }

            return ExecuteResult(o, call.ElapsedMilliseconds, false);
        }

        /// <summary>
        /// 把 ExecOutcome 渲染成 SlashResult:Lines(人读)与 Data(机器读)**同一份终值、同一组键**。
        /// syntaxError=true 时走 SlashResults.Error(只有一行消息、ok=false)。
        /// </summary>
        private static SlashResult ExecuteResult(ExecOutcome o, long elapsedMs, bool syntaxError)
        {
            SlashResult r = syntaxError
                ? SlashResults.Error(KindName, o.UnsupportedReason)
                : new SlashResult { Kind = KindName };

            if (!syntaxError)
            {
                if (o.Executed)
                    r.Lines.Add("已执行: " + o.Path + "  [" + o.ActionType + "]");
                else if (!o.Resolved)
                    r.Lines.Add("未执行(未解析到节点): " + o.Path + (o.UnsupportedReason != null ? "  —— " + o.UnsupportedReason : ""));
                else
                    r.Lines.Add("未执行(" + (o.UnsupportedReason ?? "原因未记录") + "): " + o.Path
                        + "  [" + o.ActionType + "]" + (o.Error != null ? "  失败: " + o.Error : ""));

                if (o.PatchLeak)
                    r.Lines.Add("  ‼ 严重:临时 Harmony 前缀补丁**未能卸掉** —— 该进程内 UI.MouseCell() / GenWorld.MouseTile() "
                        + "会一直返回本次注入的坐标,直到游戏重启(见 ROSLYN 日志与 Data.patchLeak)。本结果已按失败处理(executed=false)。");
                if (o.FoldedArgs != null)
                    r.Lines.Add("  注:参数 \"" + o.FoldedArgs + "\" 被当成路径的一部分(整串命中节点),因此没有作为坐标/pawnId 注入。");
                if (o.VisibleError != null)
                    r.Lines.Add("  注:VisibleNow 判定抛异常,已按「判不出」处理,未执行。");
                if (o.ActiveError != null)
                    r.Lines.Add("  注:ActiveNow 判定抛异常,已按「判不出」处理,未执行。");
            }

            r.Data = new Dictionary<string, object>
            {
                ["ok"] = o.Executed,
                ["mode"] = "execute",
                ["path"] = o.Path,
                ["requestedPath"] = o.RequestedPath,
                ["label"] = o.Label,
                ["actionType"] = o.ActionType,
                ["visible"] = o.Visible,
                ["active"] = o.Active,
                ["visibleError"] = o.VisibleError,
                ["activeError"] = o.ActiveError,
                ["executed"] = o.Executed,
                ["unsupportedReason"] = o.UnsupportedReason,
                ["error"] = o.Error,
                ["patchLeak"] = o.PatchLeak,
                ["mapX"] = o.MapX.HasValue ? (object)o.MapX.Value : null,
                ["mapZ"] = o.MapZ.HasValue ? (object)o.MapZ.Value : null,
                ["tile"] = o.Tile.HasValue ? (object)o.Tile.Value : null,
                ["pawnId"] = o.PawnId.HasValue ? (object)o.PawnId.Value : null,
                ["singleInt"] = o.SingleInt.HasValue ? (object)o.SingleInt.Value : null,
                ["resolved"] = o.Resolved,
                ["foldedArgs"] = o.FoldedArgs,
                ["elapsedMs"] = elapsedMs
            };
            // SlashResults.Error 已经放了 ok/error;上面统一覆盖成同一组键(error 逐字保留)。
            if (syntaxError) r.Data["error"] = o.UnsupportedReason;
            return r;
        }

        private static string ChildHint(DebugActionNode node)
        {
            var sb = new StringBuilder("其子节点: ");
            int n = node.children.Count;
            int show = n > 10 ? 10 : n;
            for (int i = 0; i < show; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(PathSafe(node.children[i]));
            }
            if (n > show) sb.Append(" | …共 ").Append(n).Append(" 个");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 走查(GetNode 的等价、但不展开目标)

        /// <summary>
        /// 与 Dialog_Debug.GetNode(path) 逐步等价(按 label 逐段精确匹配、祖先逐层 TrySetupChildren),
        /// 唯一差别:**目标节点本身不展开**。
        /// 原版 GetNode 对每段(含最后一段)都调 TrySetupChildren ⇒ 指向动态子菜单时会把 childGetter
        /// 同步跑完 —— 正是 §4.4 说的 >60 s / AppHangB1。执行一个菜单路径本来也只是「导航」而非执行,
        /// 所以取消最后一步展开在语义上无损。
        /// 这里**没有** GetNode 兜底:两者的可命中集合完全相同(同一 label 谓词、同样展开祖先),
        /// 兜底不可达;审查轮已确认接受本替代(不再保留「换回 GetNode」的路标)。
        /// </summary>
        private static DebugActionNode FindNodeNoExpand(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Dialog_Debug.TrySetupNodeGraph();
            DebugActionNode node = Dialog_Debug.rootNode;
            if (node == null) return null;

            string[] parts = path.Split('\\');
            for (int i = 0; i < parts.Length; i++)
            {
                DebugActionNode next = null;
                List<DebugActionNode> kids = node.children;
                if (kids != null)
                {
                    for (int k = 0; k < kids.Count; k++)
                    {
                        DebugActionNode c = kids[k];
                        if (c != null && string.Equals(c.label, parts[i], StringComparison.Ordinal)) { next = c; break; }
                    }
                }
                if (next == null) return null;
                node = next;
                if (i < parts.Length - 1) node.TrySetupChildren();   // 只展开祖先
            }
            return node;
        }

        // ---------------------------------------------------------------- 执行/可用性 小工具

        /// <summary>返回 null = 动作体跑完没抛;非 null = 异常原文。executed 只反映「跑没跑完」。</summary>
        private static string InvokeGuarded(Action body, out bool executed)
        {
            try
            {
                body();
                executed = true;
                return null;
            }
            catch (Exception ex)
            {
                executed = false;
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>
        /// ToolMap 代鼠标:临时 prefix UI.MouseCell() → 跑动作 → finally 卸补丁。
        /// 返回 null 表示「动作体没抛 **且** 补丁已卸干净」。
        /// **卸补丁失败必须算失败**(审查 M1):补丁若留在进程里,之后所有 UI.MouseCell() 调用都会
        /// 返回本次注入的坐标(直到游戏重启)—— 这种状态绝不允许表现为「干净成功」,所以这里把
        /// executed 置 false、patchLeak 置 true,并同时写日志与返回文案。
        /// </summary>
        private static string InvokeWithMouseCell(IntVec3 cell, Action body, out bool executed, out bool patchLeak)
        {
            Harmony h = null;
            MethodInfo original = null;
            bool patched = false;
            string error = null;
            executed = false;
            patchLeak = false;

            try
            {
                original = typeof(UI).GetMethod("MouseCell", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (original == null) return "找不到 UI.MouseCell()(1.7 API 变更)";
                MethodInfo prefix = typeof(DebugActionCommand).GetMethod("MouseCellPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                if (prefix == null) return "找不到内部前缀 MouseCellPrefix(实现错误)";

                mouseCell = cell;
                h = new Harmony(HarmonyOwner);
                h.Patch(original, new HarmonyMethod(prefix), null, null, null);
                patched = true;
                error = InvokeGuarded(body, out executed);
            }
            catch (Exception ex)
            {
                executed = false;
                error = "鼠标代理(UI.MouseCell)不可用: " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (patched)
                {
                    try { h.Unpatch(original, HarmonyPatchType.Prefix, HarmonyOwner); }
                    catch (Exception ex)
                    {
                        patchLeak = true;
                        executed = false;   // 绝不报「干净成功」
                        string leak = "临时前缀补丁未能卸掉(UI.MouseCell 已被劫持:之后该进程内所有 UI.MouseCell() "
                            + "调用都会返回本次注入的坐标,直到游戏重启): " + ex.GetType().Name + ": " + ex.Message;
                        Verse.Log.Error("[Roslyn] /debug-action " + leak);
                        error = error == null ? leak : error + " ; 另外, " + leak;
                    }
                }
            }
            return error;
        }

        /// <summary>ToolWorld 代鼠标:临时 prefix GenWorld.MouseTile(bool) → 跑动作 → finally 卸补丁(口径同 InvokeWithMouseCell)。</summary>
        private static string InvokeWithWorldTile(int tileId, Action body, out bool executed, out bool patchLeak)
        {
            Harmony h = null;
            MethodInfo original = null;
            bool patched = false;
            string error = null;
            executed = false;
            patchLeak = false;

            try
            {
                original = typeof(RimWorld.GenWorld).GetMethod("MouseTile", BindingFlags.Public | BindingFlags.Static,
                    null, new Type[] { typeof(bool) }, null);
                if (original == null) return "找不到 GenWorld.MouseTile(bool)(1.7 API 变更)";
                MethodInfo prefix = typeof(DebugActionCommand).GetMethod("MouseTilePrefix", BindingFlags.NonPublic | BindingFlags.Static);
                if (prefix == null) return "找不到内部前缀 MouseTilePrefix(实现错误)";

                mouseWorldTile = new PlanetTile(tileId);
                h = new Harmony(HarmonyOwner);
                h.Patch(original, new HarmonyMethod(prefix), null, null, null);
                patched = true;
                error = InvokeGuarded(body, out executed);
            }
            catch (Exception ex)
            {
                executed = false;
                error = "鼠标代理(GenWorld.MouseTile)不可用: " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (patched)
                {
                    try { h.Unpatch(original, HarmonyPatchType.Prefix, HarmonyOwner); }
                    catch (Exception ex)
                    {
                        patchLeak = true;
                        executed = false;
                        string leak = "临时前缀补丁未能卸掉(GenWorld.MouseTile 已被劫持:之后该进程内所有 MouseTile() "
                            + "调用都会返回本次注入的瓦片,直到游戏重启): " + ex.GetType().Name + ": " + ex.Message;
                        Verse.Log.Error("[Roslyn] /debug-action " + leak);
                        error = error == null ? leak : error + " ; 另外, " + leak;
                    }
                }
            }
            return error;
        }

        // 供 Harmony 前缀读的注入值(执行与卸补丁都在游戏线程上串行完成,无并发)。
        private static IntVec3 mouseCell;
        private static PlanetTile mouseWorldTile;

        private static bool MouseCellPrefix(ref IntVec3 __result)
        {
            __result = mouseCell;
            return false;   // 跳过原方法
        }

        private static bool MouseTilePrefix(ref PlanetTile __result)
        {
            __result = mouseWorldTile;
            return false;
        }

        /// <summary>按 thingIDNumber 找 Pawn。error 非 null = **判定失败(异常)**,与「找不到」区分(审查 S6)。</summary>
        private static Pawn FindPawnById(int thingId, out string error)
        {
            error = null;
            try
            {
                List<Map> maps = Find.Maps;
                if (maps == null) return null;
                for (int i = 0; i < maps.Count; i++)
                {
                    Map m = maps[i];
                    if (m == null || m.mapPawns == null) continue;
                    foreach (Pawn p in m.mapPawns.AllPawnsSpawned)
                        if (p != null && p.thingIDNumber == thingId) return p;
                    foreach (Pawn p in m.mapPawns.AllPawns)
                        if (p != null && p.thingIDNumber == thingId) return p;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                Verse.Log.ErrorOnce("[Roslyn] /debug-action 查找 pawn 失败: " + ex, "RoslynProbe.DebugAction.FindPawn".GetHashCode());
            }
            return null;
        }

        /// <summary>VisibleNow 判定:error 非 null = **判不出(异常)**,不是「不可用」(审查 S6)。</summary>
        private static bool TryVisibleNow(DebugActionNode node, out string error)
        {
            error = null;
            try { return node.VisibleNow; }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        /// <summary>ActiveNow 判定:error 非 null = **判不出(异常)**,不是「不可用」(审查 S6)。</summary>
        private static bool TryActiveNow(DebugActionNode node, out string error)
        {
            error = null;
            try { return node.ActiveNow; }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        private static string PathSafe(DebugActionNode node)
        {
            try { return node.Path ?? ""; }
            catch (Exception) { return node.label ?? ""; }
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= ReasonDetailMax ? s : s.Substring(0, ReasonDetailMax) + "…";
        }

        private static string Pad(string s, int width)
        {
            s = s ?? "";
            return s.Length >= width ? s + " " : s + new string(' ', width - s.Length);
        }

        // 纯语法解析在**本文件的顶层类** DebugActionRequest(成员零 Verse 类型 ⇒ 单测进程
        // 不需要 Assembly-CSharp 也能加载它;用例见 tests/SlashHarness/DebugActionCommandTests.cs)。
    }

    /// <summary>
    /// /debug-action 的纯语法解析(零 Verse 依赖 ⇒ 可单测)。只回答「用户想干什么」;
    /// 节点存不存在、参数对哪个 actionType 有意义,一律由游戏侧判定(那里才有树)。
    /// 放在顶层而非 DebugActionCommand 内部:后者的静态字段含 Verse 值类型(IntVec3 / PlanetTile),
    /// 加载它需要 Assembly-CSharp —— 纯逻辑单测不该被这条依赖拖住。
    /// </summary>
    public sealed class DebugActionRequest
    {
        public enum Intent { Usage, List, Refresh, Execute }

        public Intent Mode = Intent.Usage;
        /// <summary>list:路径子串过滤(空 = 全量)。</summary>
        public string Filter = "";
        /// <summary>execute:候选路径(反斜杠分隔,与 node.Path 同形)。</summary>
        public string Path = "";
        /// <summary>execute:候选参数原文(尚未解释)。</summary>
        public string ArgsRaw = "";
        /// <summary>execute:是否用了 " -- " 显式分隔(位置式切分可能切错,游戏侧据此决定是否回落)。</summary>
        public bool ExplicitSeparator;

        public static bool TryParse(string rest, out DebugActionRequest r, out string error)
        {
            r = new DebugActionRequest();
            error = "";
            string s = (rest ?? "").Trim();
            if (s.Length == 0) { r.Mode = Intent.Usage; return true; }

            string[] t = Tokens(s);
            string first = t[0];

            if (first.Equals("refresh", StringComparison.OrdinalIgnoreCase))
            {
                if (t.Length != 1) { error = "refresh 不接受参数"; return false; }
                r.Mode = Intent.Refresh;
                return true;
            }

            if (first.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                r.Mode = Intent.List;
                r.Filter = t.Length > 1 ? Join(t, 1, t.Length - 1) : "";
                return true;
            }

            r.Mode = Intent.Execute;

            // ① 显式分隔:路径与参数之间放一个独立的 "--"。
            int sep = -1;
            for (int i = 0; i < t.Length; i++) if (t[i] == "--") { sep = i; break; }
            if (sep >= 0)
            {
                if (sep == 0) { error = "\" -- \" 之前缺少路径"; return false; }
                if (sep == t.Length - 1)
                {
                    error = "\" -- \" 之后缺少参数(ToolMap: x z;ToolWorld: tile <n>;ToolMapForPawns: pawn <id>)";
                    return false;
                }
                r.Path = Join(t, 0, sep);
                r.ArgsRaw = Join(t, sep + 1, t.Length - sep - 1);
                r.ExplicitSeparator = true;
                return true;
            }

            // ② 位置式(规格写法 [<路径> [x z|tile|pawnId]]):从右侧保守地认参数,剩下的是路径。
            string p, a;
            if (TrySplitPositional(t, out p, out a)) { r.Path = p; r.ArgsRaw = a; }
            else { r.Path = s; r.ArgsRaw = ""; }
            if (r.Path.Length == 0) { error = "路径为空"; return false; }
            return true;
        }

        private static bool TrySplitPositional(string[] t, out string path, out string args)
        {
            path = "";
            args = "";
            int n = t.Length;
            if (n < 2) return false;

            int suffix = 0;
            if (n >= 3 && IsInt(t[n - 1]) && IsInt(t[n - 2]) && IsCoordKeyword(t[n - 3])) suffix = 3;
            else if (n >= 2 && IsInt(t[n - 1]) && (IsInt(t[n - 2]) || IsArgKeyword(t[n - 2]))) suffix = 2;
            else if (n >= 2 && IsArgKeyword(t[n - 1])) suffix = 2;
            else if (n >= 2 && IsInt(t[n - 1])) suffix = 1;

            if (suffix == 0 || suffix >= n) return false;
            path = Join(t, 0, n - suffix);
            args = Join(t, n - suffix, suffix);
            return true;
        }

        /// <summary>execute 的参数解释(纯逻辑,零 Verse)。命名式与位置式不得混用。</summary>
        public sealed class Args
        {
            public bool HasMapCell;
            public int MapX;
            public int MapZ;
            public bool HasTile;
            public int Tile;
            public bool HasPawn;
            public int PawnId;
            /// <summary>位置式单个整数:按 actionType 决定当瓦片还是 pawnId(ToolMap 会拒绝)。</summary>
            public bool HasSingleInt;
            public int SingleInt;

            public bool HasAny { get { return HasMapCell || HasTile || HasPawn || HasSingleInt; } }

            public static readonly Args None = new Args();

            public static bool TryParse(string raw, out Args a, out string error)
            {
                a = new Args();
                error = "";
                string[] t = Tokens(raw ?? "");
                if (t.Length == 0) return true;

                bool named = false;
                var positional = new List<int>();

                for (int i = 0; i < t.Length; )
                {
                    string tok = t[i];
                    string lower = tok.ToLowerInvariant();

                    if (lower == "tile")
                    {
                        int v;
                        if (i + 1 >= t.Length || !TryInt(t[i + 1], out v)) { error = "tile 需要一个整数瓦片号"; return false; }
                        if (a.HasTile) { error = "tile 重复指定"; return false; }
                        a.HasTile = true; a.Tile = v; named = true; i += 2; continue;
                    }
                    if (lower == "pawn" || lower == "pawnid")
                    {
                        int v;
                        if (i + 1 >= t.Length || !TryInt(t[i + 1], out v)) { error = "pawn 需要一个整数 thingIDNumber"; return false; }
                        if (a.HasPawn) { error = "pawn 重复指定"; return false; }
                        a.HasPawn = true; a.PawnId = v; named = true; i += 2; continue;
                    }
                    if (lower == "map")
                    {
                        int x, z;
                        if (i + 2 >= t.Length || !TryInt(t[i + 1], out x) || !TryInt(t[i + 2], out z))
                        { error = "map 需要两个整数格坐标(map <x> <z>)"; return false; }
                        if (a.HasMapCell) { error = "地图格坐标重复指定"; return false; }
                        a.HasMapCell = true; a.MapX = x; a.MapZ = z; named = true; i += 3; continue;
                    }

                    int num;
                    if (TryInt(tok, out num)) { positional.Add(num); i++; continue; }

                    error = "无法识别的参数: \"" + tok + "\"(可用:tile <n> / pawn <id> / map <x> <z> / x z)";
                    return false;
                }

                if (positional.Count > 0 && named) { error = "位置式参数与命名式参数不得混用"; return false; }

                if (positional.Count == 1)
                {
                    a.HasSingleInt = true;
                    a.SingleInt = positional[0];
                }
                else if (positional.Count == 2)
                {
                    a.HasMapCell = true;
                    a.MapX = positional[0];
                    a.MapZ = positional[1];
                }
                else if (positional.Count > 2)
                {
                    error = "位置式参数最多两个整数(x z),实际 " + positional.Count + " 个";
                    return false;
                }
                return true;
            }
        }

        // ---------------------------------------------------------------- 纯文本小工具(零 Verse)

        private static string[] Tokens(string s)
        {
            return (s ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string Join(string[] t, int start, int count)
        {
            var sb = new StringBuilder();
            for (int i = start; i < start + count && i < t.Length; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(t[i]);
            }
            return sb.ToString();
        }

        private static bool IsInt(string s)
        {
            int v;
            return TryInt(s, out v);
        }

        private static bool TryInt(string s, out int v)
        {
            return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);
        }

        private static bool IsArgKeyword(string s)
        {
            string l = (s ?? "").ToLowerInvariant();
            return l == "tile" || l == "pawn" || l == "pawnid" || l == "map";
        }

        private static bool IsCoordKeyword(string s)
        {
            return (s ?? "").ToLowerInvariant() == "map";
        }
        }
}
