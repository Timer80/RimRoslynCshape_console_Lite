// RoslynProbe —— /dev 指令（调试设置三张表：列 / 翻转 / 置位 / 清位）。
//
// 规格来源：TEXT38\TOOLSTACK-TO-SLASH-1.7.md 的 /dev 行 + §1.1「调试设置三张表」；
// 原版行为逐条对照 LudeonTK\DebugTabMenu_Settings.cs（AddNode：翻转 + 调 <Name>Toggled）
// 与 LudeonTK\Dialog_Debug.cs:DoCheckbox（只在值真的变了时才写字段 + 调回调）。
//
// 三张表：Verse.DebugSettings / Verse.DebugViewSettings / Verse.DebugGenerationSettings 的
// public static **可写**字段（跳过 const 与 static readonly）。字段名当键（TOOLSTACK §4.3：唯一，安全）；
// 跨表重名时不猜 —— 列出歧义，要调用方用 类名.字段 指定。
//
// 语法（rest = "/dev" 之后的整串，已 Trim；内核半与 provider 半共用同一个 TryParse）：
//   ""                 用法（不碰游戏侧）
//   "list"             列出三张表全部可写字段
//   "list <filter>"    子串过滤（字段名或类名，大小写不敏感）
//   "<字段>"           翻转（同原版）
//   "<字段> on|off"    置位 / 清位
//   "<类名>.<字段>"     跨表重名时消歧（也接受 Verse.DebugSettings.godMode 这种全名）
//
// Data（小驼峰、稳定）：
//   变更：{ok, mode(flip|on|off), class, field, before, after, changed,
//          toggledCallback, toggledState, toggledError}
//     toggledState（六态，机器可读侧据此区分「成功」/「未确认」/「被拒」）：
//       not-changed  值没变 ⇒ 原版语义下不写、不调回调
//       no-callback  变更了，但没有同名 <Name>Toggled 回调
//       called       回调已调用且未抛
//       failed       回调被调用但抛异常（或回调查找失败）
//       unconfirmed  回调**未被确认执行**（marshal 超时 / 队列取消 / Executor 没跑 handler）
//       refused      **整个请求被拒**（不在游戏线程且没有 Executor ⇒ 到不了游戏线程）：字段也未改动
//     ok：只有 not-changed / no-callback / called 才为 true；failed / unconfirmed 一律 false，
//         即使字段已变更（Data 里的 changed / after 仍是真值）—— 别让「回调没跑成」看起来像成功。
//   列  ：{ok, count, total, filter, fields:[{class, field, value, type}]}
//   失败 / 用法：只有**稳定四键** changed / toggledCallback / toggledState / toggledError 在**所有非 list** 路径
//         都存在（值可为 null；changed 恒 false；拒绝路径 toggledState="refused"）—— 它是"不必按结果种类分支"的那部分，
//         不是"完整键集一致"：其余键按语义不同而不同（用法页 {ok, usage}；语法 / 未找到 / 读写失败 {ok, error[+field|class]}；
//         歧义另附 ambiguous:[{class, field}]；非 bool 另附 type）；list 有自己的稳定形状（见上一行，不含稳定四键）。
//
// 非 bool 字段：本机 1.7 实测三张表全是 bool，但本指令不赌这一点 —— 字段类型不是 bool 时，
// 翻转 / on / off 一律明确拒绝并点名类型（绝不把非 bool 硬当 bool；原版自己写死了 (bool) 强转）。
// const / static readonly 同理：**不进列表**、不可写，指定到它们时给精确文案（不是含混的「未找到」）。
//
// 姿势 B（README §3.5）：Bootstrap 注入 ctx.Dev = DevCommand.Provide。
//   内核半 Handle —— 零 Verse：解析语法、给用法与语法错误；合法请求原样交给 ctx.CallDev(rest)。
//   游戏侧半 Provide —— 解析三张表类型、反射字段、读写、调回调。类型名走字符串解析（不 typeof），
//   故本文件零 TypeRef 到 Assembly-CSharp：校验工程可编、无游戏环境下也能单测这段逻辑。
//
// 线程契约（契约 v1.1 §3.4.1：**判据只认 ctx.IsGameThread，不用 Channel 猜**）：
//   * 字段读写在任何线程都安全（静态 bool 的反射，不碰 Unity/Verse）⇒ **始终 inline**。
//     于是 before → after 是权威、确定的效果，不取决于游戏线程是否被占用，也不存在「排了队但超时」的半态。
//   * list 同理（只读反射）。
//   * 只有同名 <Name>Toggled 回调会碰 Unity/Verse（如 drawIndoorMaskToggled → mapDrawer），路由三态：
//       ctx.IsGameThread == true        → 就地调用
//       false 且 ctx.Executor != null   → ctx.Executor.Enqueue(...) marshal 到游戏线程 + TCS 等待
//       false 且 ctx.Executor == null   → **明确拒绝执行**（连字段都不写）
//     第三条是「绝不裸调」：没有 Executor 就没有任何办法把回调放上游戏线程，写完字段只会留下半态
//     （字段已改、UI 没联动），故整个请求拒绝，错误行写明原因。判据是事实位，不是 Channel。
//   * 回调失败 / 未确认不推翻字段已变更的事实：字段照样写进去（那是主线效果），但 ok=false，
//     状态落 Data.toggledState / toggledError 与那一行（原版此处是让异常直接炸进 UI）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using RoslynProbe.Repl;

namespace RoslynProbe
{
    /// <summary>
    /// /dev —— 调试设置三张表（DebugSettings / DebugViewSettings / DebugGenerationSettings）的
    /// public static 可写字段：列 / 翻转 / 置位 / 清位；变更后调同名 &lt;Name&gt;Toggled 回调（原版行为）。
    /// </summary>
    public static class DevCommand
    {
        /// <summary>指令 verb 与 SlashResult.Kind（小写无空格）。</summary>
        private const string Kind = "dev";

        /// <summary>
        /// marshal 到游戏线程的等待上限。**不硬编码**：与 SessionEval / SessionLine 同源（对方改了这里跟着走）。
        /// </summary>
        private const int MarshalTimeoutMs = SessionLine.DefaultTimeoutMs;

        /// <summary>线程路由三态（契约 v1.1 §3.4.1）。</summary>
        private const int RouteInline = 0;
        private const int RouteMarshal = 1;
        private const int RouteRefuse = 2;   // 到不了游戏线程（不在游戏线程且没有 Executor）⇒ 整个请求拒绝，字段都不写

        /// <summary>回调状态六态（Data.toggledState；ok 只在 not-changed / no-callback / called 时为 true）。</summary>
        private const string StateNotChanged = "not-changed";
        private const string StateNoCallback = "no-callback";
        private const string StateCalled = "called";
        private const string StateFailed = "failed";
        private const string StateUnconfirmed = "unconfirmed";
        private const string StateRefused = "refused";

        /// <summary>三张表的全名（都在 Assembly-CSharp；按此顺序解析与呈现）。</summary>
        private static readonly string[] TableTypeNames =
        {
            "Verse.DebugSettings",
            "Verse.DebugViewSettings",
            "Verse.DebugGenerationSettings"
        };

        /// <summary>Usage 整串（「参数 —— 说明」，分隔符是 U+2014 ×2，见 SlashEntry.Usage）。</summary>
        private const string UsageText =
            "[list [filter]|<字段> [on|off]] —— DebugSettings / DebugViewSettings / DebugGenerationSettings "
            + "三张表的 public static 字段：列 / 翻转 / 置位 / 清位；不带 on|off 则翻转（同原版）";

        /// <summary>RegisterAll 反射调用（public static void Register()：无参、返回 void）。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = Kind,
                Usage = UsageText,
                Group = "调试",
                Kind = Kind,
                Dangerous = true,
                Handler = Handle
            });
        }

        // ---------------- 内核半（零 Verse） ----------------

        /// <summary>
        /// 指令 handler：只做语法解析（TryParse）与用法 / 语法错误，合法请求原样转给
        /// ctx.CallDev(rest)（未注册 → 内核自动回「能力未注册」行，不抛）。
        /// </summary>
        private static SlashResult Handle(string rest, SlashContext ctx)
        {
            DevRequest request;
            SlashResult early;
            if (!TryParse(rest, out request, out early)) return early;
            return ctx.CallDev(rest);
        }

        /// <summary>
        /// 语法解析（内核半与 provider 半共用，杜绝两份语法漂移）。
        /// 返回 false 时 error 是「用法页」或「语法错误」结果（都不需要游戏侧 provider）。
        /// </summary>
        private static bool TryParse(string rest, out DevRequest request, out SlashResult error)
        {
            request = null;
            error = null;
            string text = (rest ?? "").Trim();

            if (text.Length == 0)
            {
                error = Usage("缺少参数：本指令是 /dev list [filter] 或 /dev <字段> [on|off]。");
                return false;
            }

            if (string.Equals(text, "list", StringComparison.OrdinalIgnoreCase))
            {
                request = new DevRequest { List = true };
                return true;
            }
            if (text.Length > 4 && char.IsWhiteSpace(text[4])
                && text.Substring(0, 4).Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                request = new DevRequest { List = true, Filter = text.Substring(5).Trim() };
                return true;
            }

            string[] tokens = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 2)
            {
                error = Usage("参数过多（收到 " + tokens.Length + " 段：" + text + "）。");
                return false;
            }

            if (tokens.Length == 2)
            {
                string word = tokens[1];
                if (word.Equals("on", StringComparison.OrdinalIgnoreCase))
                    request = new DevRequest { Spec = tokens[0], Desired = true };
                else if (word.Equals("off", StringComparison.OrdinalIgnoreCase))
                    request = new DevRequest { Spec = tokens[0], Desired = false };
                else
                {
                    error = StableKeys(SlashResults.Error(Kind,
                        "第二个参数只能是 on 或 off（收到 \"" + word + "\"）；不带 on|off 则翻转。"));
                    return false;
                }
                return true;
            }

            request = new DevRequest { Spec = tokens[0] };
            return true;
        }

        private static SlashResult Usage(string reason)
        {
            return StableKeys(SlashResults.Usage(Kind,
                reason,
                "用法: /dev list [filter] —— 列出三张表字段（所属类、字段名、当前值）",
                "      /dev <字段> [on|off] —— 不带 on|off 则翻转（同原版）；跨表重名用 <类名>.<字段>",
                "例:   /dev list draw    /dev godMode    /dev DebugSettings.godMode on"));
        }

        // ---------------- 游戏侧半（姿势 B provider） ----------------

        /// <summary>
        /// 姿势 B provider：Bootstrap 注入 <c>ctx.Dev = DevCommand.Provide</c>（契约 v1.1 §3.5 统一命名）。
        /// 解析三张表类型后交给 <see cref="Run"/>；一个类型都解析不到时响亮报错，绝不抛。
        /// </summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            var tables = new List<Type>();
            var missing = new List<string>();
            foreach (string name in TableTypeNames)
            {
                Type t = FindType(name);
                if (t != null) tables.Add(t);
                else missing.Add(name);
            }

            if (tables.Count == 0)
                return Fail("调试设置三张表一个都没解析到（" + string.Join(", ", missing)
                    + "）—— 它们都在 Assembly-CSharp，/dev 只能在游戏进程内使用。", null);

            SlashResult result = Run(rest, tables, ctx);
            if (missing.Count > 0 && result != null && result.Data != null)
                result.Data["missingTables"] = missing;
            return result;
        }

        /// <summary>
        /// 纯逻辑核心：三张表由参数给出（生产 = Provide 解析到的三个类型；单测 = 合成类型），
        /// 因此无游戏环境下也能覆盖 字段解析 / 歧义 / 非 bool 与 readonly 拒绝 / 回调路由 / 列表 全部分支。
        /// <para>**单测缝**：这是本文件除 Register / Provide 之外的唯一 public 成员，只为让单测能在
        /// 无游戏环境下注入合成表。收 internal 本来也可行 —— SlashHarness 把 Source 与用例编进**同一个**
        /// 程序集；与它不同程序集的是主仓 tests/RoslynProbe.Tests.csproj（不含本文件）。这里保持 public。</para>
        /// </summary>
        public static SlashResult Run(string rest, IList<Type> tables, SlashContext ctx)
        {
            DevRequest request;
            SlashResult early;
            if (!TryParse(rest, out request, out early)) return early;

            if (tables == null || tables.Count == 0)
                return Fail("没有可用的调试设置表（三张表都没解析到类型）。", null);

            // list 只读反射，不碰 Unity/Verse ⇒ 任何线程就地（不需要 marshal）
            if (request.List) return DoList(request, tables);

            FieldHit hit;
            string resolveError;
            Dictionary<string, object> resolveData;
            if (!ResolveField(request.Spec, tables, out hit, out resolveError, out resolveData))
                return Fail(resolveError, resolveData);

            if (hit.Field.FieldType != typeof(bool))
            {
                string message = "字段 " + hit.Display + " 的类型是 " + TypeLabel(hit.Field.FieldType)
                    + "（不是 bool）—— 本指令只做布尔字段的列 / 翻转 / 置位 / 清位，不把非 bool 硬当 bool；"
                    + "要改它请用 /eval 直接赋值。";
                Dictionary<string, object> data = FieldKeys(hit);
                data["type"] = TypeLabel(hit.Field.FieldType);
                return Fail(message, data);
            }

            bool before;
            try { before = (bool)hit.Field.GetValue(null); }
            catch (Exception ex)
            {
                return Fail("字段 " + hit.Display + " 读取失败: " + Describe(ex), FieldKeys(hit));
            }

            bool willChange = !request.Desired.HasValue || request.Desired.Value != before;
            MethodInfo callback = null;
            string lookupError = null;
            if (willChange)
            {
                callback = FindToggledCallback(hit, out lookupError);
                if (callback == null && lookupError != null) willChange = false;   // 查找失败：不当成"有回调要调"
            }

            SlashContext c = ctx ?? new SlashContext();
            int route = RouteFor(willChange && callback != null, c);
            if (route == RouteRefuse)
            {
                Dictionary<string, object> data = FieldKeys(hit);
                data["changed"] = false;
                data["toggledCallback"] = callback.Name;
                data["toggledState"] = StateRefused;
                data["toggledError"] = null;
                return Fail("字段 " + hit.Display + " 的同名回调 " + callback.Name + " 必须在游戏线程上执行，"
                    + "但本次调用既不在游戏线程（ctx.IsGameThread = false）又没有 ctx.Executor 可 marshal —— "
                    + "为避免留下半态（字段已改而回调没跑），本次拒绝执行（字段未改动）。"
                    + "请由 Bootstrap 给 ctx.Executor 接线，或让壳层把 ctx.IsGameThread 填对。", data);
            }

            DevOutcome outcome = ApplyField(hit, request.Desired, callback, lookupError);
            if (outcome.Error != null)
                return Fail("字段 " + hit.Display + " 读写失败: " + outcome.Error, FieldKeys(hit));

            ApplyCallback(outcome, route, c);
            return Render(request, hit, outcome);
        }

        // ---------------- 线程路由（契约 v1.1 §3.4.1） ----------------

        /// <summary>
        /// 只有 <c>&lt;Name&gt;Toggled</c> 回调会碰 Unity/Verse，故只对"必须调回调"的请求做路由：
        /// 已在游戏线程 → 就地；否则有 Executor → marshal；再否则 → 明确拒绝（**绝不裸调**）。
        /// 判据是事实位 <c>ctx.IsGameThread</c>，**不用 Channel 猜**（那正是 v1.1 要收口的四套启发式之一）。
        /// </summary>
        private static int RouteFor(bool needsGameThread, SlashContext ctx)
        {
            if (!needsGameThread) return RouteInline;
            if (ctx != null && ctx.IsGameThread) return RouteInline;
            if (ctx != null && ctx.Executor != null) return RouteMarshal;
            return RouteRefuse;
        }

        // ---------------- 变更 ----------------

        /// <summary>
        /// 字段读写本体：只碰反射，不碰 Unity ⇒ 任何线程都安全（故始终 inline，见文件头线程契约）。
        /// 回调的 MethodInfo 已在调用点解析好并传入（路由需要提前知道"这次要不要游戏线程"）。
        /// </summary>
        private static DevOutcome ApplyField(FieldHit hit, bool? desired, MethodInfo callback, string lookupError)
        {
            var outcome = new DevOutcome { Callback = callback };
            if (lookupError != null) outcome.CallbackError = lookupError;
            try
            {
                object before = hit.Field.GetValue(null);
                outcome.Before = before;
                bool current = (bool)before;
                bool after = desired.HasValue ? desired.Value : !current;
                outcome.Changed = after != current;
                outcome.After = after;
                if (!outcome.Changed) return outcome;   // 原版：值没变就不写、也不调回调

                hit.Field.SetValue(null, after);
            }
            catch (Exception ex)
            {
                outcome.Error = Describe(ex);
            }
            return outcome;
        }

        /// <summary>同名回调 <c>&lt;Name&gt;Toggled</c>：public static、无参（与原版同一签名查找）。</summary>
        private static MethodInfo FindToggledCallback(FieldHit hit, out string error)
        {
            error = null;
            try
            {
                return hit.Owner.GetMethod(hit.Field.Name + "Toggled",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            }
            catch (Exception ex)
            {
                error = "回调查找失败: " + Describe(ex);
                return null;
            }
        }

        /// <summary>
        /// 调回调（原版在字段变更后调用，用来联动 UI / 重建网格）。这是本指令唯一碰 Unity/Verse 的地方。
        /// route 由 <see cref="RouteFor"/> 定：RouteInline 就地；RouteMarshal 经队列到游戏线程并等待；
        /// RouteRefuse 到不了这里（调用点已明确拒绝）。
        /// </summary>
        private static void ApplyCallback(DevOutcome outcome, int route, SlashContext ctx)
        {
            if (outcome == null || !outcome.Changed) return;                        // 未变更：原版不调回调
            if (outcome.CallbackError != null && outcome.Callback == null) return;  // 回调查找失败：错误已记下
            if (outcome.Callback == null) return;                                   // 没有同名回调

            if (route == RouteInline)
            {
                outcome.CallbackError = InvokeCallbackNow(outcome.Callback);
                return;
            }

            var holder = new ErrorHolder();
            ReplRequest request;
            try
            {
                request = ctx.Executor.Enqueue(delegate
                {
                    holder.Error = InvokeCallbackNow(outcome.Callback);
                    return ReplResult.Success(null);
                }, MarshalTimeoutMs);
            }
            catch (Exception ex)
            {
                outcome.CallbackUnconfirmed = true;
                outcome.CallbackError = "marshal 失败: " + Describe(ex);
                return;
            }

            bool completed;
            string waitError = null;
            try { completed = request.Tcs.Task.Wait(MarshalTimeoutMs); }
            catch (Exception ex) { completed = false; waitError = Describe(ex); }

            if (!completed)
            {
                ctx.Executor.MarkTimedOut(request.Id);   // 与 SessionEval 同款：排队请求收口，别留成孤儿
                outcome.CallbackUnconfirmed = true;
                outcome.CallbackError = "等待游戏线程超时 / 未完成（上限 " + MarshalTimeoutMs + "ms）"
                    + (waitError == null ? "" : "：" + waitError)
                    + "；回调未确认（游戏线程恢复后可能仍会执行）";
                return;
            }

            // 队列完成 ≠ handler 跑过：Executor.Poisoned 时 Tcs 上直接是 "session poisoned"。
            ReplResult requestOutcome = OutcomeOf(request);
            if (requestOutcome == null || !requestOutcome.Ok)
            {
                outcome.CallbackUnconfirmed = true;
                outcome.CallbackError = "marshal 未执行（"
                    + (requestOutcome == null ? "取不到结果" : (requestOutcome.Error ?? "Ok=false"))
                    + "）：回调未确认";
                return;
            }

            outcome.CallbackError = holder.Error;   // null = 调用成功
        }

        /// <summary>就地调用回调；返回 null = 成功，否则是异常描述（拆掉 TargetInvocationException 外壳）。</summary>
        private static string InvokeCallbackNow(MethodInfo callback)
        {
            try
            {
                callback.Invoke(null, null);
                return null;
            }
            catch (Exception ex)
            {
                return Describe(ex);
            }
        }

        /// <summary>取 marshal 请求的终值；未完成 / 已取消 / 取值抛异常一律回 null（不发散）。</summary>
        private static ReplResult OutcomeOf(ReplRequest request)
        {
            try
            {
                if (request == null || request.Tcs == null || !request.Tcs.Task.IsCompleted) return null;
                return request.Tcs.Task.Result;
            }
            catch (Exception) { return null; }
        }

        // ---------------- 渲染 ----------------

        /// <summary>回调六态（ok 只在 not-changed / no-callback / called 时为 true）。</summary>
        private static string StateOf(DevOutcome outcome)
        {
            if (!outcome.Changed) return StateNotChanged;
            if (outcome.CallbackUnconfirmed) return StateUnconfirmed;
            if (outcome.CallbackError != null) return StateFailed;   // 含 回调查找失败 / 调用抛异常 / marshal 失败
            return outcome.Callback == null ? StateNoCallback : StateCalled;
        }

        private static SlashResult Render(DevRequest request, FieldHit hit, DevOutcome outcome)
        {
            var r = new SlashResult { Kind = Kind };
            string state = StateOf(outcome);
            bool ok = state == StateNotChanged || state == StateNoCallback || state == StateCalled;

            string line = hit.Display + ": " + Format(outcome.Before) + " → " + Format(outcome.After);
            if (!outcome.Changed)
                line += request.Desired.HasValue
                    ? "（已经是 " + Format(outcome.Before) + "，未变更，未调用回调）"
                    : "（未变更，未调用回调）";
            else
                line += "（" + (request.Desired == null ? "已翻转" : (request.Desired.Value ? "已置位" : "已清位")) + "）";
            r.Lines.Add(line);

            if (state == StateNoCallback)
                r.Lines.Add("回调: 无（" + hit.Field.Name + "Toggled 不存在）");
            else if (state == StateCalled)
                r.Lines.Add("回调: " + outcome.Callback.Name + " 已调用");
            else if (state == StateFailed)
                r.Lines.Add("回调: " + (outcome.Callback == null ? hit.Field.Name + "Toggled" : outcome.Callback.Name)
                    + " 失败 —— " + outcome.CallbackError + "（字段已变更，但联动未完成）");
            else if (state == StateUnconfirmed)
                r.Lines.Add("回调: " + (outcome.Callback == null ? hit.Field.Name + "Toggled" : outcome.Callback.Name)
                    + " 未确认 —— " + outcome.CallbackError);

            r.Data = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["mode"] = request.Mode,
                ["class"] = hit.ClassName,
                ["field"] = hit.Field.Name,
                ["before"] = outcome.Before,
                ["after"] = outcome.After,
                ["changed"] = outcome.Changed,
                ["toggledCallback"] = outcome.Callback == null ? null : outcome.Callback.Name,
                ["toggledState"] = state,
                ["toggledError"] = outcome.CallbackError
            };
            return r;
        }

        private static SlashResult DoList(DevRequest request, IList<Type> tables)
        {
            string filter = request.Filter ?? "";
            var fields = new List<Dictionary<string, object>>();
            var lines = new List<string>();
            var perClass = new List<string>();
            int total = 0;

            foreach (Type table in tables)
            {
                List<FieldInfo> all = PublicStaticFields(table);
                total += all.Count;
                perClass.Add(table.Name + " " + all.Count);
                all.Sort(delegate(FieldInfo a, FieldInfo b) { return string.CompareOrdinal(a.Name, b.Name); });

                foreach (FieldInfo field in all)
                {
                    if (filter.Length > 0
                        && field.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                        && table.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    object value;
                    try { value = field.GetValue(null); }
                    catch (Exception ex) { value = "<读取失败: " + Describe(ex) + ">"; }

                    string label = TypeLabel(field.FieldType);
                    fields.Add(new Dictionary<string, object>
                    {
                        ["class"] = table.Name,
                        ["field"] = field.Name,
                        ["value"] = value,
                        ["type"] = label
                    });
                    lines.Add("  " + table.Name + "." + field.Name + " = " + Format(value)
                        + (field.FieldType == typeof(bool) ? "" : "  (" + label + ")"));
                }
            }

            if (filter.Length > 0)
                lines.Insert(0, "调试设置字段: 匹配 " + fields.Count + "/" + total + " 条（filter=\"" + filter + "\"）");
            else
                lines.Insert(0, "调试设置字段: " + total + " 条（" + string.Join(" / ", perClass) + "）");
            if (fields.Count == 0)
                lines.Add("（无匹配字段；/dev list 看全部）");

            var r = new SlashResult { Kind = Kind };
            r.Lines.AddRange(lines);
            r.Data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["count"] = fields.Count,
                ["total"] = total,
                ["filter"] = filter.Length == 0 ? null : filter,
                ["fields"] = fields
            };
            return r;
        }

        // ---------------- 字段解析 ----------------

        /// <summary>
        /// <c>&lt;字段&gt;</c> / <c>&lt;类名&gt;.&lt;字段&gt;</c> → 唯一 FieldInfo。
        /// 两个分支用**同一个匹配口径**（<see cref="MatchFields"/>：先精确、再忽略大小写），命中多个即歧义。
        /// </summary>
        private static bool ResolveField(string spec, IList<Type> tables, out FieldHit hit,
            out string error, out Dictionary<string, object> data)
        {
            hit = null;
            error = null;
            data = null;
            string text = (spec ?? "").Trim();
            if (text.Length == 0)
            {
                error = "字段名为空。";
                return false;
            }

            int dot = text.LastIndexOf('.');
            if (dot >= 0)
            {
                string classPart = text.Substring(0, dot).Trim();
                string fieldPart = text.Substring(dot + 1).Trim();
                if (classPart.Length == 0 || fieldPart.Length == 0)
                {
                    error = "字段指定 \"" + text + "\" 格式不对：应为 <字段> 或 <类名>.<字段>。";
                    data = new Dictionary<string, object> { ["field"] = text };
                    return false;
                }

                var classMatches = new List<Type>();
                foreach (Type t in tables) if (ClassMatches(t, classPart)) classMatches.Add(t);
                if (classMatches.Count == 0)
                {
                    error = "未找到类 \"" + classPart + "\"；三张表是 " + ClassList(tables) + "。";
                    data = new Dictionary<string, object> { ["class"] = classPart, ["field"] = fieldPart };
                    return false;
                }

                List<FieldHit> found = MatchFields(classMatches, fieldPart);
                if (found.Count == 0)
                    return NoSuchField(fieldPart, classMatches[0].Name, classMatches, out error, out data);
                if (found.Count > 1)
                    return Ambiguous(fieldPart, found, out error, out data);

                hit = found[0];
                return true;
            }

            List<FieldHit> candidates = MatchFields(tables, text);
            if (candidates.Count == 0)
                return NoSuchField(text, null, tables, out error, out data);
            if (candidates.Count > 1)
                return Ambiguous(text, candidates, out error, out data);

            hit = candidates[0];
            return true;
        }

        /// <summary>
        /// 查无此（可写）字段：先问"是不是 const / static readonly 这些**故意不列**的字段"（给精确文案），
        /// 再退回通用的"未找到 + 怎么查 / 怎么消歧"。
        /// </summary>
        private static bool NoSuchField(string name, string className, IEnumerable<Type> scope,
            out string error, out Dictionary<string, object> data)
        {
            string reason = UnsupportedReason(scope, name);
            if (reason != null)
            {
                error = reason + " —— 本指令只列 / 只改可写字段，这类字段**刻意不出现在 /dev list 里**"
                    + "（写它只会得到底层 FieldAccessException）。";
                data = className == null
                    ? new Dictionary<string, object> { ["field"] = name }
                    : new Dictionary<string, object> { ["class"] = className, ["field"] = name };
                return false;
            }

            if (className != null)
            {
                error = "类 " + className + " 里没有 public static 可写字段 \"" + name + "\"（用 /dev list <过滤> 查）。";
                data = new Dictionary<string, object> { ["class"] = className, ["field"] = name };
                return false;
            }

            error = "未找到字段 \"" + name + "\"（" + ClassList(scope) + " 的 public static 可写字段共 "
                + CountFields(scope) + " 条；用 /dev list <过滤> 查）；跨表重名时用 <类名>.<字段> 指定。";
            data = new Dictionary<string, object> { ["field"] = name };
            return false;
        }

        /// <summary>歧义：绝不猜一个，列出候选并教调用方怎么消歧。</summary>
        private static bool Ambiguous(string name, List<FieldHit> candidates, out string error,
            out Dictionary<string, object> data)
        {
            error = "字段名 \"" + name + "\" 在三张表里不唯一，拒绝猜：" + HitList(candidates)
                + "；请用 <类名>.<字段> 指定（例：/dev " + candidates[0].Display + " on）。";
            data = new Dictionary<string, object>
            {
                ["field"] = name,
                ["ambiguous"] = HitData(candidates)
            };
            return false;
        }

        /// <summary>
        /// 字段名匹配（**两个分支同一口径**）：先精确，精确一个都没有才忽略大小写；
        /// 命中多个由调用点按歧义处理（绝不猜）。
        /// </summary>
        private static List<FieldHit> MatchFields(IEnumerable<Type> tables, string name)
        {
            var exact = new List<FieldHit>();
            var loose = new List<FieldHit>();
            foreach (Type table in tables)
            {
                foreach (FieldInfo field in PublicStaticFields(table))
                {
                    if (field.Name == name) exact.Add(new FieldHit(table, field));
                    else if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
                        loose.Add(new FieldHit(table, field));
                }
            }
            return exact.Count > 0 ? exact : loose;
        }

        /// <summary>public static **可写**字段（跳过 const / IsLiteral 与 static readonly / IsInitOnly）。</summary>
        private static List<FieldInfo> PublicStaticFields(Type table)
        {
            var list = new List<FieldInfo>();
            if (table == null) return list;
            foreach (FieldInfo f in table.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                list.Add(f);
            }
            return list;
        }

        /// <summary>名字对得上、但本指令刻意不支持的字段（const / static readonly）→ 精确文案；否则 null。</summary>
        private static string UnsupportedReason(IEnumerable<Type> tables, string name)
        {
            foreach (Type table in tables)
            {
                if (table == null) continue;
                foreach (FieldInfo f in table.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (f.Name != name) continue;
                    if (f.IsLiteral)
                        return "字段 " + table.Name + "." + f.Name + " 是 const（编译期常量，反射不可写）";
                    if (f.IsInitOnly)
                        return "字段 " + table.Name + "." + f.Name + " 是 static readonly（反射不可写）";
                }
            }
            return null;
        }

        private static bool ClassMatches(Type table, string part)
        {
            return string.Equals(table.Name, part, StringComparison.OrdinalIgnoreCase)
                || string.Equals(table.FullName, part, StringComparison.OrdinalIgnoreCase);
        }

        private static int CountFields(IEnumerable<Type> tables)
        {
            int n = 0;
            foreach (Type t in tables) n += PublicStaticFields(t).Count;
            return n;
        }

        private static string ClassList(IEnumerable<Type> tables)
        {
            var names = new List<string>();
            foreach (Type t in tables) names.Add(t.Name);
            return names.Count == 0 ? "（无）" : string.Join(" / ", names);
        }

        private static string HitList(List<FieldHit> hits)
        {
            var names = new List<string>();
            foreach (FieldHit h in hits) names.Add(h.Display);
            return string.Join("、", names);
        }

        private static List<Dictionary<string, object>> HitData(List<FieldHit> hits)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (FieldHit h in hits)
                list.Add(new Dictionary<string, object> { ["class"] = h.ClassName, ["field"] = h.Field.Name });
            return list;
        }

        // ---------------- 类型解析（字符串，不 typeof） ----------------

        /// <summary>
        /// 按全名找类型：先 "全名, Assembly-CSharp"，再扫已加载程序集。
        /// 找不到返回 null（→ 响亮的错误行）而不抛 —— 无游戏环境下也可安全调用。
        /// </summary>
        private static Type FindType(string fullName)
        {
            Type t = Type.GetType(fullName + ", Assembly-CSharp", false);
            if (t != null) return t;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = assembly.GetType(fullName, false); }
                catch (Exception) { t = null; }
                if (t != null) return t;
            }
            return null;
        }

        // ---------------- 小工具 ----------------

        private static SlashResult Fail(string message, Dictionary<string, object> extra)
        {
            SlashResult r = SlashResults.Error(Kind, message);
            if (extra != null)
            {
                foreach (KeyValuePair<string, object> pair in extra)
                {
                    if (pair.Key == "ok" || pair.Key == "error") continue;   // 不覆盖 SlashResults.Error 的形状
                    r.Data[pair.Key] = pair.Value;
                }
            }
            return StableKeys(r);
        }

        /// <summary>
        /// 补**稳定四键**（F5）：changed / toggledCallback / toggledState / toggledError —— 失败与用法结果也带它们，
        /// 值可为 null，于是工具消费方不必按结果种类分支取键。**只保证这四键**；其余键按语义不同而不同
        /// （error / usage / mode / before·after / class·field·type / ambiguous）。已存在的键一律不覆盖
        /// （例如拒绝路径已写了 changed=false + toggledState="refused"）。
        /// </summary>
        private static SlashResult StableKeys(SlashResult r)
        {
            if (r == null || r.Data == null) return r;
            if (!r.Data.ContainsKey("changed")) r.Data["changed"] = false;
            if (!r.Data.ContainsKey("toggledCallback")) r.Data["toggledCallback"] = null;
            if (!r.Data.ContainsKey("toggledState")) r.Data["toggledState"] = null;
            if (!r.Data.ContainsKey("toggledError")) r.Data["toggledError"] = null;
            return r;
        }

        private static Dictionary<string, object> FieldKeys(FieldHit hit)
        {
            return new Dictionary<string, object> { ["class"] = hit.ClassName, ["field"] = hit.Field.Name };
        }

        private static string Format(object value)
        {
            if (value == null) return "null";
            if (value is bool) return ((bool)value) ? "true" : "false";
            var formattable = value as IFormattable;
            return formattable != null ? formattable.ToString(null, CultureInfo.InvariantCulture) : value.ToString();
        }

        private static string TypeLabel(Type type) { return type == null ? "?" : type.Name; }

        /// <summary>异常 → 一行描述；拆掉反射的 TargetInvocationException 外壳（回调的真实异常在里面）。</summary>
        private static string Describe(Exception ex)
        {
            Exception inner = (ex is TargetInvocationException && ex.InnerException != null) ? ex.InnerException : ex;
            return inner.GetType().Name + ": " + inner.Message;
        }

        // ---------------- 内部类型 ----------------

        private sealed class DevRequest
        {
            public bool List;
            public string Filter = "";
            public string Spec = "";
            public bool? Desired;
            public string Mode
            {
                get
                {
                    if (List) return "list";
                    if (!Desired.HasValue) return "flip";
                    return Desired.Value ? "on" : "off";
                }
            }
        }

        private sealed class FieldHit
        {
            public readonly Type Owner;
            public readonly FieldInfo Field;

            public FieldHit(Type owner, FieldInfo field)
            {
                Owner = owner;
                Field = field;
            }

            public string ClassName { get { return Owner.Name; } }
            public string Display { get { return Owner.Name + "." + Field.Name; } }
        }

        private sealed class DevOutcome
        {
            public string Error;             // 字段读写失败（GetValue / SetValue 抛）
            public object Before;
            public object After;
            public bool Changed;
            public MethodInfo Callback;
            public string CallbackError;
            public bool CallbackUnconfirmed; // true = 回调**未被确认执行**（超时 / 取消 / Executor 没跑 handler）
        }

        /// <summary>跨线程回传回调结果；volatile 让"游戏线程写 → 读线程读"有序（不共享 outcome 对象）。</summary>
        private sealed class ErrorHolder
        {
            public volatile string Error;
        }
    }
}
