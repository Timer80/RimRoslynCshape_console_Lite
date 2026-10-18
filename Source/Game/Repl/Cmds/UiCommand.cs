// RoslynProbe —— /ui open：打开游戏内 GUI 控制台窗口。
//
// 姿势 B 的合规样板见本目录的 LogCommand.cs。
//
// ---- verb ----
// 表内 verb 是 "ui-open"。"ui" 归**两段式别名项**（Source/Game/Repl/SlashCommands.cs 的 UiAliasHandler），
// 它剥掉 "open" 后反查本表并委托：
//     SlashEntry e; if (SlashRegistry.TryGet("ui-open", out e)) result = e.Handler(sub, ctx);
// 于是 Provide 同时接受 rest 为空（/ui-open 直呼）与 "open"（/ui open 反查）两种形态 ——
// 两个形态等价，表内 verb 是机器面名字。
//
// ---- 姿势 B ----
// handler 只写 return ctx.CallUiOpen(rest);  provider 本体在本文件：
// public static SlashResult Provide(string rest, SlashContext ctx)。
// Bootstrap 注入 ctx.UiOpen = UiCommand.Provide；未注入时 CallUiOpen 自己回「能力未注册」行。
//
// ---- 线程规则（全舰队，与 /dev、/log、/status 同款；契约 v1.1 §3.4.1）----
// ConsoleLayer.Open() 会 new Verse.Window + Find.WindowStack.Add，IsOpen 读静态 instance —— 全是
// 游戏线程的东西。判据**只认 ctx.IsGameThread**（契约 v1.1 的普通 bool 事实位，壳层必须填；
// 拿不准填 false = 宁可多一次 marshal），规则逐字如下：
//     if (ctx.IsGameThread) 就地执行;
//     else if (ctx.Executor != null) 经 Executor.Enqueue + TCS + 超时 marshal;
//     else **明确拒绝**（绝不直调 Unity/Verse —— 直调正是历史上被判 Critical 的形态）。
// **不再用 Channel 猜**（那正是契约 v1.1 要收口的启发式之一）。
// 注：早期「Executor == null 时就地」的宽松口径**已作废** —— W6(/dev) 曾在未接线/未知通道时直调 Unity，
// 被审出 Critical；全舰队统一为上列三分支。本文件一开始就是这个形状。
//
// ---- 诚实性（项目级裁定 2 + W10.md）----
// opened    = 调用 ConsoleLayer.Open() **之后** ConsoleLayer.IsOpen 的真值；
// alreadyOpen = 调用**之前** ConsoleLayer.IsOpen 的真值。
// ConsoleLayer.Open() 把"未接线"（Log.ErrorOnce）与"WindowStack.Add 失败"（Log.Error + instance=null）
// 都吞成不抛 —— 这两种失败在调用后表现为 IsOpen == false，本 provider 据此回错误行 + opened=false，
// **绝不**照抄 Bootstrap 那个恒真的 HTTP 替身
// （Source/Game/Bootstrap.cs:99 `() => { ConsoleLayer.Open(); return true; }` —— 它把 alreadyOpen 也
//  永远算成 false，是本指令明确拒绝复制的谎报点）。
// 幂等：已开着时 Open() 第一行就 return，opened=true、alreadyOpen=true（与 HTTP /ui/open 口径一致）。
//
// ---- Data ----
// 成功：{ opened:bool, alreadyOpen:bool }（W10.md 指定；两键都有区分度，成功路径不放恒 true 的 ok）。
// 失败：SlashResults.Error 的 { ok:false, error } + opened:false + alreadyOpen（**真值**；拿不到时 null）。
//   两条"拿不到开窗终值"的失败路（非 gui+无 Executor、marshal 超时/poisoned）原先填死 alreadyOpen=false，
//   而真实值是**未知** —— 已改读 ConsoleLayer.IsOpen 真值（ReadIsOpenBestEffort），未知才回 null。

using System;
using System.Collections.Generic;
using RoslynProbe.Repl;   // Executor / ReplRequest / ReplResult：marshal 通道（姿势 B）

namespace RoslynProbe
{
    /// <summary>/ui open —— 打开游戏内控制台窗口（姿势 B：游戏侧 provider 在本文件内）。</summary>
    public static class UiCommand
    {
        /// <summary>表内登记的 verb。</summary>
        public const string Verb = "ui-open";

        /// <summary>SlashResult.Kind（小写无空格）。</summary>
        public const string Kind = "ui-open";

        /// <summary>marshal 等待上限：与协议层 SessionLine.DefaultTimeoutMs 同源，不自造数字。</summary>
        public const int MarshalTimeoutMs = SessionLine.DefaultTimeoutMs;

        /// <summary>RouteFor 的返回值：在游戏线程（ctx.IsGameThread == true），就地执行。</summary>
        public const int RouteInline = 0;
        /// <summary>RouteFor 的返回值：非游戏线程 + 有 Executor，marshal 到游戏线程再执行。</summary>
        public const int RouteMarshal = 1;
        /// <summary>RouteFor 的返回值：非游戏线程 + 无 Executor，明确拒绝（不裸碰 Unity）。</summary>
        public const int RouteNoChannel = 2;

        /// <summary>一次开窗尝试的终值（纯数据：既便于从游戏线程带回，也便于纯逻辑单测）。</summary>
        public sealed class OpenOutcome
        {
            /// <summary>调用 ConsoleLayer.Open() **之前** IsOpen 的真值。</summary>
            public bool AlreadyOpen;
            /// <summary>调用 ConsoleLayer.Open() **之后** IsOpen 的真值（唯一可信的 opened 依据）。</summary>
            public bool Opened;
            /// <summary>非 null = 这次尝试失败的可读原因（Open() 抛异常时的原文）。</summary>
            public string Error;
        }

        /// <summary>RegisterAll 反射调用（全程序集 public static void Register()，无参、返回 void）。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = Verb,
                // F5：无参数规格 —— 参数段留空（渲染成 "  /ui-open"）；两段式别名由别名项 /ui 的那行
                // "  /ui open —— …" 提供可发现性，不再把 "open" 塞进本项的 args。
                Usage = "—— 打开游戏内 GUI 控制台窗口（幂等：已开着时也算 opened=true）",
                Group = "界面",
                Kind = Kind,
                // 有副作用：会往 WindowStack 压一个窗口（用户可见的界面变化）。
                Dangerous = true,
                Handler = Handle
            });
        }

        /// <summary>handler：只把调用转给姿势 B provider（未注入时 CallUiOpen 自己回 missing-capability 行）。</summary>
        private static SlashResult Handle(string rest, SlashContext ctx)
        {
            return ctx.CallUiOpen(rest);
        }

        // ================= 姿势 B provider（游戏侧；集成时 Bootstrap 注入 ctx.UiOpen = UiCommand.Provide） =================

        /// <summary>
        /// /ui open 的 provider：解析 → 用法页 / 就地 / marshal / 明确拒绝 → 渲染。本方法绝不抛。
        /// </summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            string sub = (rest ?? "").Trim();
            if (sub.Length != 0 && !sub.Equals("open", StringComparison.OrdinalIgnoreCase))
                return UsageResult("未知子命令: " + sub);

            SlashContext c = ctx ?? new SlashContext();
            int route = RouteFor(c.IsGameThread, c.Executor != null);

            if (route == RouteInline)
                return Build(ApplyOpen());

            if (route == RouteNoChannel)
                return Failure(
                    "打开控制台失败：本次调用不在游戏线程（ctx.IsGameThread = false；ctx.Channel = "
                    + (c.Channel == null ? "<null>" : "\"" + c.Channel + "\"")
                    + "），且 ctx.Executor 未接线 —— 无法 marshal 到游戏更新线程。"
                    + "在会话读线程上直接 new Verse.Window / WindowStack.Add 是未定义行为，故拒绝执行。", ReadIsOpenBestEffort());

            // route == RouteMarshal：读线程 → 经 Executor 队列 marshal 到游戏更新线程。
            OpenOutcome captured = null;
            ReplRequest req = c.Executor.Enqueue(
                delegate { captured = ApplyOpen(); return ReplResult.Success(captured.Opened ? "opened" : "not-opened"); },
                MarshalTimeoutMs);

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
                return Failure(MarshalFailureReason(OutcomeOf(req), waitError), ReadIsOpenBestEffort());
            return Build(captured);
        }

        /// <summary>
        /// 取数路径判定（纯逻辑，可单测）：**契约 v1.1 §3.4.1 的线程事实位** <c>isGameThread</c> 为 true
        /// → 就地；否则有 Executor → marshal；再否则 → 明确拒绝（**不**裸碰 Unity）。
        /// 判据只认事实位，**不用 Channel 猜**（那正是契约 v1.1 要收口的四套启发式之一）。
        /// </summary>
        public static int RouteFor(bool isGameThread, bool hasExecutor)
        {
            if (isGameThread) return RouteInline;
            return hasExecutor ? RouteMarshal : RouteNoChannel;
        }

        /// <summary>
        /// marshal 结束但没拿到开窗终值时把原因折算成一行（纯逻辑，可单测）。优先级与 LogCommand
        /// 同款：TimedOut → 超时文案；Error 非空 → **原样带出**（Executor.Poisoned 时 Tcs 上是
        /// "session poisoned"，handler 根本没跑，吞掉它就只剩一句无用泛化）；Wait 抛出的异常 →
        /// 原样带出；都没有 → 泛化文案。
        /// </summary>
        public static string MarshalFailureReason(ReplResult outcome, string waitError)
        {
            if (outcome != null && outcome.TimedOut)
                return "打开控制台超时（游戏更新线程 " + MarshalTimeoutMs + "ms 内未排空队列）—— 窗口状态未知，按未打开报告（不谎报 opened=true）。";
            if (outcome != null && !outcome.Ok && !string.IsNullOrEmpty(outcome.Error))
                return "打开控制台失败: " + outcome.Error;
            if (!string.IsNullOrEmpty(waitError))
                return "打开控制台失败: " + waitError;
            return "打开控制台失败：游戏更新线程未取到结果（handler 未执行或未排空）。";
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
        /// 跨线程读一次 ConsoleLayer.IsOpen 真值 —— **仅在"拿不到开窗终值"的失败路上**用。
        /// IsOpen 是**纯静态标量**（instance != null），且 HTTP 时期 Commands.cs 的 DoStatus
        /// 本来就在 HTTP 线程读它（既有口径、有先例）—— Commands.cs 随 T5 删除，那个 consoleOpen 字段
        /// 也已从 /status 移除（现归本文件的 alreadyOpen，见 Source/Game/Repl/Cmds/StatusCommand.cs:39），
        /// 故这里读真值而不是填死 false。
        /// 读不到（极端情况）回 null：**未知就报未知，不填假值**。
        /// </summary>
        public static bool? ReadIsOpenBestEffort()
        {
            try { return ConsoleLayer.IsOpen; }
            catch { return null; }
        }

        /// <summary>
        /// 开窗动作本体：**只能在游戏线程上执行**（由上面的「线程规则」三分支之一把调用送到游戏线程后）。
        /// 顺序固定为"先读 alreadyOpen → 调 Open() → 再读 opened"，两者都是 ConsoleLayer.IsOpen 的真值；
        /// Open() 不抛（内部吞掉未接线/添加失败）与抛异常两条路都在这里收敛成 OpenOutcome，绝不外抛。
        /// </summary>
        public static OpenOutcome ApplyOpen()
        {
            var o = new OpenOutcome();
            try
            {
                o.AlreadyOpen = ConsoleLayer.IsOpen;
                ConsoleLayer.Open();
                o.Opened = ConsoleLayer.IsOpen;
            }
            catch (Exception ex)
            {
                o.Opened = false;
                o.Error = "ConsoleLayer 调用抛出异常: " + ex.GetType().Name + ": " + ex.Message;
            }
            return o;
        }

        /// <summary>
        /// 由开窗终值渲染结果（纯逻辑，可单测）。opened == false 一律走错误行 + opened=false；
        /// ConsoleLayer 把未接线/添加失败吞成了"没抛出但也没打开"，这里补出点名原因（不谎报 true）。
        /// </summary>
        public static SlashResult Build(OpenOutcome o)
        {
            if (o == null)
                return Failure("打开控制台失败：未取到窗口状态（内部逻辑错误）。", null);

            if (!o.Opened)
            {
                string why = o.Error;
                if (string.IsNullOrEmpty(why))
                    why = "ConsoleLayer.Open() 之后 ConsoleLayer.IsOpen 仍为 false —— 控制台未接线"
                        + "（Bootstrap 未调用 ConsoleLayer.Wire），或 Find.WindowStack.Add 失败被 ConsoleLayer"
                        + " 吞掉（原版日志里有 [Roslyn] 记录）。";
                return Failure(why, o.AlreadyOpen);
            }

            var r = new SlashResult { Kind = Kind };
            r.Lines.Add(o.AlreadyOpen ? "控制台已处于打开状态（幂等：未重复打开）。" : "已打开游戏内控制台窗口。");
            r.Data = new Dictionary<string, object>
            {
                ["opened"] = true,
                ["alreadyOpen"] = o.AlreadyOpen
            };
            return r;
        }

        /// <summary>
        /// 失败结果：错误行（SlashResults.Error 的 ok:false + error）+ opened=false +
        /// alreadyOpen 的**真值或 null**（未知就报未知 —— 绝不为了凑 schema 填假 false）。
        /// </summary>
        public static SlashResult Failure(string message, bool? alreadyOpen)
        {
            SlashResult r = SlashResults.Error(Kind, message);
            r.Data["opened"] = false;
            r.Data["alreadyOpen"] = alreadyOpen.HasValue ? (object)alreadyOpen.Value : null;
            return r;
        }

        /// <summary>用法页（未知子命令时带上错误行）。</summary>
        public static SlashResult UsageResult(string error)
        {
            var lines = new List<string> { "用法: /ui open" };
            if (!string.IsNullOrEmpty(error)) lines.Add("错误: " + error);
            lines.Add("open —— 打开游戏内 GUI 控制台窗口（幂等：已开着时 opened=true 且 alreadyOpen=true）");
            lines.Add("本表登记名 /ui-open；两段式别名 /ui open 与它等价（同一条实现，见 /help）");
            return SlashResults.Usage(Kind, lines.ToArray());
        }
    }
}
