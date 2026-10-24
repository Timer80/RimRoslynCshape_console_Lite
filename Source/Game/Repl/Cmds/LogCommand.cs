// RoslynProbe —— /log [n] [filter]：最近 n 条原版游戏日志；管道模式下 agent 唯一的日志入口。
//
// 姿势 B（与 W1 StatusCommand 同款）：handler 只写 return ctx.CallLog(rest);
// provider 本体在本文件（public static SlashResult Provide(rest, ctx)）。
// Bootstrap 注入 ctx.Log = LogCommand.Provide —— 未注入时 CallLog 自己回
// 「能力未注册」行，这里不写 if (ctx.Log == null)。
//
// Data（小驼峰、稳定）：{ count:int, total:int, filter:string, entries:[{type,text,stack}] }
//   count  = 本次返回的条数（过滤 + 取尾 n 之后）；total = 原版日志缓冲总条数；filter = 过滤串（无则 ""）。
//   type   = "Error" / "Warning" / "Message"（Verse.LogMessageType 成员名，逐字取，不改写）。
//   stack  = LogMessage.StackTrace（原版对无栈条目给 "No stack trace."，本指令同样不改写）。
// Lines：首行汇总（总条数 / 命中数 / filter），随后逐条 "[E] / [W] / [M] + 正文"；
//   前缀图例只给 gui（人看），pipe 通道省掉这一行 token。
//   栈只进 Data —— 50 条 Error 各带一段栈会把管道输出撑到上千行；原版日志窗口也是"展开才看栈"。
//
// ---- 数据源偏离 brief（控制者已签字，非笔误）----
// brief 写「数据源就是 LogSnapshot」，本指令**不走**它，理由三条：
//   1) LogSnapshot 的读取面只有 CaptureCount() 与 CollectSince(before, after)，后者只回 text；
//      而本指令被冻结的 Data 形状要求 entries[{type,text,stack}] —— 拿不到 type/stack 就交不了差。
//   2) 它没有清空 API（见下"缺口"），本来就无法承担 /log clear。
//   3) 它当时没有回带 type/stack 的方法；直读 Verse.Log.Messages 不必给它加方法。
// 已定口径：**接受直读 Verse.Log.Messages**（本文件的 provider 就是实现体）。若将来给 LogSnapshot
// 补上带回 type/stack（与清空口径）的快照方法，可把本文件的 Capture() 换成调它，只留纯逻辑与 Data/Lines 渲染。
//
// 线程约定（沿用 Source/Game/Repl/LogSnapshot.cs:7，不另创加锁策略；判据见契约 v1.1 §3.4.1）：
//   读 Log.Messages 必须在游戏更新线程上、并用 Verse.Log.LockMessages() 保护（原版自身同款）。
//   LogMessageQueue.Messages 是活队列（Queue<LogMessage>），非主线程枚举时主线程的
//   DrainOffThreadMessages 仍会入队 ⇒ 读线程上裸读有 InvalidOperationException 风险。
//   线程判据**只认契约 v1.1 的事实位 ctx.IsGameThread**（壳层填；拿不准填 false）——
//   本文件**不自造启发式**、也不用 Channel 猜（v1.1 收口的就是「Channel=="gui" 否则
//   UnityData.IsInMainThread」那类判定，四套启发式必然漂移）：
//     ctx.IsGameThread == true       → 就地读（GUI 壳的 OnGUI 本来就在游戏线程）
//     false 且 ctx.Executor != null  → 经 Executor 队列 marshal 到游戏线程
//                                      （与 SessionEval.EnqueueEval / Commands.DoTexProbeOnMainThread 同款：
//                                       Enqueue + TCS 等待 + 超时 MarkTimedOut）
//     false 且 ctx.Executor == null  → 明确拒绝（RouteNoChannel），**绝不**躲回裸读
//
// 缺口（已核实 LogSnapshot 确无清空 API）：
//   LogSnapshot 只有 CaptureCount / CollectSince，没有清空 API ⇒ /log clear 只回报缺口、
//   绝不产生副作用（原版另有 public static Verse.Log.Clear()，若要接就是一行；
//   接入后登记项的 Dangerous 须同步置 true）。

using System;
using System.Collections.Generic;
using System.Globalization;
using RoslynProbe.Repl;   // Executor / ReplRequest / ReplResult：marshal 通道（姿势 B）
using Verse;              // Log / LogMessage / LogMessageType：provider 本体（线程判据不用 UnityData）

namespace RoslynProbe
{
    /// <summary>/log —— 原版日志快照的只读入口（姿势 B：游戏侧 provider 在本文件内）。</summary>
    public static class LogCommand
    {
        /// <summary>/log 不带参数时的条数。</summary>
        public const int DefaultN = 50;

        /// <summary>Data["gap"] 取值：LogSnapshot 无清空 API，故 /log clear 不可用。</summary>
        public const string GapNoClearApi = "log-clear-no-api";

        /// <summary>marshal 等待上限：与协议层 SessionLine.DefaultTimeoutMs 同源，不自造数字。</summary>
        public const int MarshalTimeoutMs = SessionLine.DefaultTimeoutMs;

        /// <summary>RouteFor 的返回值：已在游戏线程，就地读。</summary>
        public const int RouteInline = 0;
        /// <summary>RouteFor 的返回值：非游戏线程 + 有 Executor，marshal 到游戏线程再读。</summary>
        public const int RouteMarshal = 1;
        /// <summary>RouteFor 的返回值：非游戏线程 + 无 Executor，明确拒绝（不裸读）。</summary>
        public const int RouteNoChannel = 2;

        /// <summary>RouteNoChannel 的错误行（Data["error"] 与 Lines[0] 同文）。</summary>
        public const string NoChannelMessage =
            "读取日志失败：本次调用不在游戏线程（ctx.IsGameThread = false），且 ctx.Executor 未接线 —— "
            + "无法 marshal 到游戏更新线程；裸读 Verse.Log.Messages 会与主线程的 DrainOffThreadMessages 争用"
            + "（见文件头线程约定）。请由 Bootstrap 给 ctx.Executor 接线，或让壳层把 ctx.IsGameThread 填对。";

        /// <summary>一条日志（纯数据：既便于跨线程从游戏线程带回，也便于纯逻辑单测）。</summary>
        public sealed class Record
        {
            /// <summary>LogMessageType 成员名：Error / Warning / Message。</summary>
            public string Type;
            /// <summary>日志正文（LogMessage.text 逐字）。</summary>
            public string Text;
            /// <summary>堆栈（LogMessage.StackTrace 逐字；无栈时原版为 "No stack trace."）。</summary>
            public string Stack;
        }

        /// <summary>参数解析结果（纯逻辑，可单测）。</summary>
        public sealed class Query
        {
            public int N = DefaultN;
            public string Filter = "";
            public bool Clear;
            /// <summary>非 null = 用法错误（n 写了数字但不是 ≥ 1 的整数）。</summary>
            public string Error;
        }

        /// <summary>RegisterAll 反射调用（全程序集 public static void Register()，无参、返回 void）。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = "log",
                Usage = "[n] [filter] —— 最近 n 条原版游戏日志（默认 " + DefaultN + "；filter 为大小写不敏感子串）",
                Group = "运行时",
                Kind = "log",
                Dangerous = false,   // 只读；/log clear 未实现（见 GapNoClearApi），落实后须置 true
                Handler = Handle
            });
        }

        /// <summary>handler：只把调用转给姿势 B provider（未注入时 CallLog 自己回 missing-capability 行）。</summary>
        private static SlashResult Handle(string rest, SlashContext ctx)
        {
            return ctx.CallLog(rest);
        }

        // ================= 姿势 B provider（游戏侧；集成时 Bootstrap 注入 ctx.Log = LogCommand.Provide） =================

        /// <summary>
        /// /log 的 provider：解析 → clear 缺口 / 用法错误 / 取快照 → 渲染。本方法绝不抛。
        /// 取数路径由 <see cref="RouteFor"/> 定：游戏线程就地读；否则 marshal；再否则明确拒绝。
        /// </summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            Query q = ParseArgs(rest);
            if (q.Clear) return ClearGap();
            if (!string.IsNullOrEmpty(q.Error)) return UsageResult(q.Error);

            SlashContext c = ctx ?? new SlashContext();
            bool includeLegend = LegendFor(c.Channel);
            int route = RouteFor(c);   // 判据只认 ctx.IsGameThread（契约 v1.1 §3.4.1）

            if (route == RouteInline)
                return BuildResult(Capture(), q, includeLegend);
            if (route == RouteNoChannel)
                return SlashResults.Error("log", NoChannelMessage);

            // route == RouteMarshal：读线程 → 经 Executor 队列 marshal 到游戏更新线程。
            List<Record> captured = null;
            ReplRequest req = c.Executor.Enqueue(
                delegate { captured = Capture(); return ReplResult.Success(null); }, MarshalTimeoutMs);

            bool done;
            string waitError = null;
            try { done = req.Tcs.Task.Wait(MarshalTimeoutMs); }
            catch (Exception ex)
            {
                done = false;
                waitError = ex.GetType().Name + ": "
                    + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            }
            if (!done) c.Executor.MarkTimedOut(req.Id);   // 与 SessionEval 同款：排队请求收口，别让它变成孤儿

            if (captured == null)
                return SlashResults.Error("log", MarshalFailureReason(OutcomeOf(req), waitError));
            return BuildResult(captured, q, includeLegend);
        }

        /// <summary>
        /// 取数路径判定（纯逻辑，可单测）。onGameThread 由调用方传 **ctx.IsGameThread**
        /// —— 契约 v1.1 §3.4.1 的事实位；本文件不自带启发式（不用 Channel 猜、不查 UnityData）。
        /// </summary>
        public static int RouteFor(bool onGameThread, bool hasExecutor)
        {
            if (onGameThread) return RouteInline;
            return hasExecutor ? RouteMarshal : RouteNoChannel;
        }

        /// <summary>
        /// 按 ctx 选路：**唯一读线程判据的地方**，读的就是契约 v1.1 的 <c>ctx.IsGameThread</c>
        /// （普通 bool 事实位，壳层填、拿不准填 false）。单列这个重载是为了让「判据是不是事实位」
        /// 能被离线单测钉住 —— 谁把它改回 <c>Channel == "gui" / UnityData.IsInMainThread</c> 猜，
        /// LogCommandTests 的 C1 用例立刻变红（已做过变异负向对照）。
        /// </summary>
        public static int RouteFor(SlashContext ctx)
        {
            SlashContext c = ctx ?? new SlashContext();   // 空上下文：IsGameThread=false、Executor=null
            return RouteFor(c.IsGameThread, c.Executor != null);
        }

        /// <summary>
        /// marshal 结束但没拿到快照时把原因折算成一行（纯逻辑，可单测）。优先级：
        /// TimedOut → 超时文案；Error 非空 → **原样带出**（Executor.Poisoned 时 Tcs 上是
        /// "session poisoned"，handler 根本没跑，吞掉它就只剩一句无用泛化）；Wait 抛出的异常 →
        /// 原样带出；都没有 → 泛化文案。独立成纯函数正是为了让 "session poisoned" 这条有回归测试。
        /// </summary>
        public static string MarshalFailureReason(ReplResult outcome, string waitError)
        {
            if (outcome != null && outcome.TimedOut)
                return "读取日志超时（游戏更新线程 " + MarshalTimeoutMs + "ms 内未排空队列）";
            if (outcome != null && !outcome.Ok && !string.IsNullOrEmpty(outcome.Error))
                return "读取日志失败: " + outcome.Error;
            if (!string.IsNullOrEmpty(waitError))
                return "读取日志失败: " + waitError;
            return "读取日志失败：游戏更新线程未取到快照（handler 未执行或未排空）";
        }

        /// <summary>前缀图例只给 gui（人看）；pipe 通道省掉这行 token。</summary>
        public static bool LegendFor(string channel)
        {
            return !string.Equals(channel, "pipe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>取 marshal 请求的终值；未完成 / 已取消 / 取值抛异常一律回 null（不发散）。</summary>
        private static ReplResult OutcomeOf(ReplRequest req)
        {
            try
            {
                if (req == null || req.Tcs == null || !req.Tcs.Task.IsCompleted) return null;
                return req.Tcs.Task.Result;
            }
            catch { return null; }
        }

        /// <summary>
        /// 取原版日志全量快照。**只能在游戏更新线程上调用**；读取用 Log.LockMessages() 保护
        /// （与 Source/Game/Repl/LogSnapshot.cs 同款，不另创加锁策略）。
        /// 若给 LogSnapshot 补上带回 type/stack 的快照方法，本方法改为委托给它。
        /// </summary>
        internal static List<Record> Capture()
        {
            var list = new List<Record>();
            using (Log.LockMessages())
            {
                foreach (LogMessage m in Log.Messages)
                {
                    if (m == null) continue;
                    list.Add(new Record
                    {
                        Type = m.type.ToString(),
                        Text = m.text ?? "",
                        Stack = m.StackTrace
                    });
                }
            }
            return list;
        }

        // ================= 纯逻辑（可单测） =================

        /// <summary>
        /// 解析 rest。规则（写死，避免歧义）：
        /// 整段等于 "clear"（大小写不敏感）→ clear；首个词是整数 ⇒ 它是 n，其余是 filter；
        /// 否则整段都是 filter（例：/log Roslyn、/log Some error text）。
        /// </summary>
        public static Query ParseArgs(string rest)
        {
            var q = new Query();
            string r = (rest ?? "").Trim();
            if (r.Length == 0) return q;

            if (r.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                q.Clear = true;
                return q;
            }

            string first = r, tail = "";
            // 分隔空白 = 任意空白（与内核 verb/rest 切分、/usings 别名子动词切分同一口径：
            // SlashRegistry.IndexOfWhitespace 是唯一出口）—— 只认 ASCII 空格会让 "/log 50<TAB>Roslyn"
            // 把 "50" 连同 filter 当成一个过滤器，静默走错分支。
            int sp = SlashRegistry.IndexOfWhitespace(r);
            if (sp >= 0)
            {
                first = r.Substring(0, sp).Trim();
                tail = r.Substring(sp + 1).Trim();
            }

            if (LooksLikeNumber(first))
            {
                int n;
                if (!int.TryParse(first, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) || n < 1)
                {
                    q.Error = "n 必须是 ≥ 1 的整数（收到 \"" + first + "\"）";
                    return q;
                }
                q.N = n;
                q.Filter = tail;
                return q;
            }

            q.Filter = r;
            return q;
        }

        /// <summary>首个词形如可选符号 + 纯数字（决定"它是不是 n"，不是就整体当 filter）。</summary>
        private static bool LooksLikeNumber(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int i = (s[0] == '-' || s[0] == '+') ? 1 : 0;
            if (i >= s.Length) return false;
            for (; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        /// <summary>类型前缀：Error → E，Warning → W，其余（Message）→ M。未知类型按 M 兜底。</summary>
        public static string Prefix(string type)
        {
            if (string.Equals(type, "Error", StringComparison.Ordinal)) return "E";
            if (string.Equals(type, "Warning", StringComparison.Ordinal)) return "W";
            return "M";
        }

        /// <summary>
        /// 过滤判定：对正文做大小写不敏感子串匹配（filter 为空 = 全通过）。
        /// null 记录**先**判掉（哪怕 filter 为空也不放行），否则它会在 Prefix 处 NRE。
        /// </summary>
        public static bool Matches(Record rec, string filter)
        {
            if (rec == null) return false;
            if (string.IsNullOrEmpty(filter)) return true;
            return (rec.Text ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>汇总行：总条数 / 命中数 / filter，以及本次显示多少条。</summary>
        public static string Header(Query q, int total, int matched, int shown)
        {
            if (total == 0) return "日志: 共 0 条（原版日志缓冲为空）";
            bool hasFilter = !string.IsNullOrEmpty(q.Filter);
            if (hasFilter && matched == 0)
                return "日志: 共 " + total + " 条，无匹配 \"" + q.Filter + "\" 的日志";
            return "日志: 共 " + total + " 条"
                + (hasFilter ? "，filter=\"" + q.Filter + "\" 命中 " + matched + " 条" : "")
                + "，显示最近 " + shown + " 条";
        }

        /// <summary>Data["entries"]：List&lt;Dictionary&gt;，键 type / text / stack（小驼峰、稳定）。</summary>
        public static List<Dictionary<string, object>> EntriesData(List<Record> shown)
        {
            var list = new List<Dictionary<string, object>>();
            if (shown == null) return list;
            foreach (Record rec in shown)
                list.Add(new Dictionary<string, object>
                {
                    ["type"] = rec.Type ?? "",
                    ["text"] = rec.Text ?? "",
                    ["stack"] = rec.Stack ?? ""
                });
            return list;
        }

        /// <summary>BuildResult 的默认口径：带前缀图例（gui 的人读形态）。</summary>
        public static SlashResult BuildResult(List<Record> all, Query q)
        {
            return BuildResult(all, q, true);
        }

        /// <summary>
        /// 由"全量快照 + 查询"渲染结果：过滤 → 取尾部 n 条（最近优先保持时间序）→
        /// Lines（汇总 + 可选图例 + 逐条前缀）+ Data（count / total / filter / entries）。
        /// includeLegend=false 用于 pipe 通道（省一行 token）。
        /// </summary>
        public static SlashResult BuildResult(List<Record> all, Query q, bool includeLegend)
        {
            if (q == null) q = new Query();
            int total = all == null ? 0 : all.Count;

            var matched = new List<Record>();
            if (all != null)
                foreach (Record rec in all)
                    if (Matches(rec, q.Filter)) matched.Add(rec);

            int n = q.N < 1 ? 1 : q.N;
            int start = matched.Count > n ? matched.Count - n : 0;
            var shown = new List<Record>();
            for (int i = start; i < matched.Count; i++) shown.Add(matched[i]);

            var r = new SlashResult { Kind = "log" };
            r.Lines.Add(Header(q, total, matched.Count, shown.Count));
            if (shown.Count > 0)
            {
                if (includeLegend) r.Lines.Add("（前缀: [E]=Error [W]=Warning [M]=Message）");
                foreach (Record rec in shown)
                    r.Lines.Add("[" + Prefix(rec.Type) + "] " + (rec.Text ?? ""));
            }
            r.Data = new Dictionary<string, object>
            {
                ["count"] = shown.Count,
                ["total"] = total,
                ["filter"] = q.Filter ?? "",
                ["entries"] = EntriesData(shown)
            };
            return r;
        }

        /// <summary>用法页（n 非法时带上错误行）。</summary>
        public static SlashResult UsageResult(string error)
        {
            var lines = new List<string> { "用法: /log [n] [filter]" };
            if (!string.IsNullOrEmpty(error)) lines.Add("错误: " + error);
            lines.Add("n      —— 返回最近多少条（默认 " + DefaultN + "）");
            lines.Add("filter —— 大小写不敏感子串，只匹配日志正文 text（例: /log 50 Roslyn）");
            lines.Add("/log clear —— 尚无实现：日志快照没有清空 API，故该子命令只回报这个缺口（不做任何副作用）");
            return SlashResults.Usage("log", lines.ToArray());
        }

        /// <summary>
        /// /log clear 的缺口回报：**不做任何副作用**。日志快照没有清空 API，故本指令只回报缺口
        /// （原版 <c>Verse.Log.Clear()</c> 可一行接入，是否接入留给后续决定）。
        /// </summary>
        public static SlashResult ClearGap()
        {
            var r = new SlashResult { Kind = "log" };
            r.Lines.Add("「/log clear」尚无实现：日志快照没有清空 API，故该子命令只回报这个缺口（不做任何副作用）。");
            r.Lines.Add("原版另有 public static Verse.Log.Clear()；接入后须把登记项的 Dangerous 置 true。");
            r.Data = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["gap"] = GapNoClearApi,
                ["readOnly"] = true,
                ["vanillaApi"] = "Verse.Log.Clear()"
            };
            return r;
        }
    }
}
