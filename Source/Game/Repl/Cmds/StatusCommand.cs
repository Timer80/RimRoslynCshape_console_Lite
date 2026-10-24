// RoslynProbe —— /status：控制台自检（只读；会话线程可答）。
//
// 刻意不做进程内冻结诊断 —— 断点冻结时本线程同样被冻、答案发不出去；冻结判定归客户端
// （launcher/agent 记录「最后收到输出的时间」，超时即判停滞）。因此本文件**没有** stallHint，
// 也没有为它准备的静态字段（上次时间戳 / 上次 ticks —— 本类除 const 外无任何静态字段，
// 见 StatusTests 的反射回归门）。ticks 与 paused 照常上报，客户端拿这两个值自行判断。
//
// 线程契约（README §3.4）：碰 ConsoleLog / Unity / Verse 的动作必须自己 marshal 到游戏线程。
// 本文件的取舍：
//   * 只读标量，任何线程可读：Executor 的 TickCount/Revision/PendingCount/Poisoned、
//     ConsoleLog.Count（List.Count 单字段读；**绝不遍历 Entries**，会与渲染线程的 foreach 争用）。
//   * 非标量游戏状态（GenScene.InEntryScene/InPlayScene 实为 SceneManager.GetActiveScene().name；
//     TickManager.Paused 会转 ForcePaused → 读 Find.WindowStack.WindowsForcePause / TilePicker /
//     GravshipController / MapGenerator）**只在游戏线程采样**：判据只认契约 v1.1 的事实位
//     ctx.IsGameThread（2026-10-15 增补，README §3.4.1「判据只认 ctx.IsGameThread」）——
//     本文件不自造启发式：不再以 Channel=="gui" 代替，也不调 UnityData.IsInMainThread
//     （那种「四套启发式并存」正是 v1.1 要收口的东西）。是 → 就地采；否 → 经
//     ctx.Executor.Enqueue + TCS 等待 + 超时 marshal。
//   * marshal 不可用 / 超时 / poisoned：那些字段一律置 **null（未知）**，并在行里说明为什么没有 ——
//     在会话线程裸读即便 try/catch 不抛，也会「静默降级」成假值（stage→stopped、paused→false），
//     那是最坏的失败：谎报。
//   * GateStatus()/ProbeA() 沿用 HTTP 时代的只读面（HTTP 时代就是从 HTTP 后台线程调的），不额外 marshal。
//   * 通道信息读 **ChannelFacts 事实位**（Bootstrap 在实际启动每条通道时写入），**不再重放命令行旗标** ——
//     重放是「可证伪字段靠猜」：程序化启动的通道会被报成没开。事实位**未安装**时通道字段一律 **null/未知**
//     （不默认 false），见 ChannelFacts 的三态纪律。
//
// 姿势 B：本文件自带 provider（游戏侧那半）。handler 只写 return ctx.CallStatus(rest);
// 集成时 Bootstrap 把 SlashContext.Status 注入为 StatusCommand.Provide。
//
// Data 键（小驼峰、稳定）：
//   stage / inGame / loading / mainMenuReady / paused / ticks / revision / pending / poisoned /
//   consoleLog / gate{loaded,count,failure,diag,scripting,evalThread} /
//   probeA{positive,negative,details} / capabilities[{cap,provider,ready,reason}] /
//   channels[{channel,available,reason}] / ok / retrievedAt / channel
//   * stage 与四个布尔在「未能 marshal 到游戏线程」时为 **null = 未知**（不是 false）；
//     playDataLoaded/programState/mapCount 同理。
//   * ok = 载荷已装载 且 probeA 正负探针都通过（不含任何冻结项，也不依赖游戏状态）。
//
// 已删的 HTTP 时代字段：恒定 true 的 ok、旧语义 stage=running、consoleOpen（后者归 /ui open 的 alreadyOpen）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using RoslynProbe.Repl;
using Verse;

namespace RoslynProbe
{
    /// <summary>/status —— 控制台自检：阶段 / 执行器 / 载荷 / 探针 / 能力（只读，会话线程安全）。</summary>
    public static class StatusCommand
    {
        // 本类除 const 之外**不得有静态字段**。反射回归门在 StatusTests，
        // 防的是「重新引入进程内冻结诊断所需的静态采样状态」。故这里只能是 const。

        /// <summary>载荷期望件数（Roslyn.RoslynLoader.ExpectedPayloadCount，PLAN-S0 任务 8 实测 8）。仅用于文案，权威值是 gate.count。</summary>
        public const int ExpectedPayloadCount = 8;

        /// <summary>marshal 等待上限：与协议层 SessionLine.DefaultTimeoutMs 同源，不自造数字。</summary>
        public const int MarshalTimeoutMs = SessionLine.DefaultTimeoutMs;

        /// <summary>RouteFor 返回值：已在游戏线程，就地采样。</summary>
        public const int RouteInline = 0;
        /// <summary>RouteFor 返回值：非游戏线程 + 有 Executor，marshal 到游戏线程再采。</summary>
        public const int RouteMarshal = 1;
        /// <summary>RouteFor 返回值：非游戏线程 + 无 Executor，游戏状态置 null（**不**裸读、**不**填假值）。</summary>
        public const int RouteNoChannel = 2;

        /// <summary>RouteNoChannel 的说明行（Data / Lines 同源）。</summary>
        public const string NoChannelNote =
            "本调用不在游戏更新线程上，且 ctx.Executor 未接线 —— 无法 marshal 到游戏线程；"
            + "按线程契约绝不裸读场景/暂停等非标量状态，故 stage 与四个布尔置 null（未知）";

        /// <summary>RegisterAll 反射发现并调用（无参、public static、返回 void）。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = "status",
                Usage = "—— 控制台自检（阶段/执行器/载荷/探针/能力）",
                Group = "只读自检",
                Kind = "status",
                Handler = Handle
            });
        }

        /// <summary>handler：只把调用转给姿势 B provider（provider 未注册时 CallStatus 自己回 missing-capability 行）。</summary>
        private static SlashResult Handle(string rest, SlashContext ctx)
        {
            return ctx.CallStatus(rest);
        }

        // ================= 姿势 B provider =================

        /// <summary>
        /// 姿势 B provider（集成时 Bootstrap 注入 ctx.Status = StatusCommand.Provide）。
        /// rest 无参数语义（/status 不吃参数），多余文本容忍；本方法绝不抛。
        /// </summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            return Compose(Sample(ctx ?? new SlashContext()));
        }

        /// <summary>
        /// 一次采样的全部事实（纯数据容器）。游戏侧访问集中在 <see cref="Sample"/> /
        /// <see cref="SampleGameState"/>；通道信息读 <see cref="ChannelFlagsFromFacts"/>（零 Verse）；
        /// <see cref="Compose"/> 零 Verse 依赖 —— 于是数据形状可以离线单测。
        /// **未知一律是 null**：绝不把「没采到」写成 false/stopped（谎报）。
        /// </summary>
        public sealed class StatusEnvironment
        {
            // ---- 游戏生命周期（null = 未知；只有游戏线程采样成功才有值）----
            /// <summary>true = 游戏状态采样成功（就地或 marshal 完成）。</summary>
            public bool GameStateSampled;
            /// <summary>非空 = 为什么 stage/四个布尔没有值（超时 / poisoned / Executor 未接线 / 单项读失败）。</summary>
            public string GameStateNote = "";
            /// <summary>stopped | starting | main_menu | in_game；null = 未知。</summary>
            public string Stage;
            public bool? InGame;
            public bool? Loading;
            public bool? MainMenuReady;
            public bool? Paused;
            public string ProgramState;
            public int? MapCount;
            public bool? InEntry;
            public bool? InPlay;
            public bool? PlayDataLoaded;

            // ---- 载荷 / 探针 ----
            public Dictionary<string, object> Gate = ParseGateStatus(null);
            public Dictionary<string, object> ProbeA = new Dictionary<string, object>
            {
                ["positive"] = false,
                ["negative"] = false,
                ["details"] = ""
            };

            // ---- 执行器 / 控制台缓冲（只读标量）----
            public int Ticks;
            public int Revision;
            public int Pending;
            public bool Poisoned;
            /// <summary>-1 = ConsoleLog 不可读（Core 未接线/异常）。</summary>
            public int ConsoleLogCount = -1;

            // ---- 通道 / 能力前置 ----
            public string Channel = "";
            /// <summary>逐通道可用性： [{channel,available,reason}]；available 为 null = 未知（事实位未安装）。</summary>
            public List<Dictionary<string, object>> Channels = new List<Dictionary<string, object>>();
            /// <summary>非 GUI 通道是否开（pipe v2）—— capabilities.channels.ready 用它；未知时 false 且 reason 说明。</summary>
            public bool AgentChannelOpen;
            /// <summary>事实位未安装时的说明（非空 = 未知，不报成「未开」）。</summary>
            public string ChannelsNote = "";
            /// <summary>loaded | not-loaded | unknown（三态；判不出来不报成未装载）。</summary>
            public string HarmonyState = "unknown";
            public string HarmonyDetail = "";

            /// <summary>采样失败点名（Compose 只把它们渲染成 [提示] 行；不改任何字段语义）。</summary>
            public readonly List<string> Failures = new List<string>();
        }

        /// <summary>通道开关（来自 ChannelFacts 事实位；纯数据）。</summary>
        public sealed class ChannelFlags
        {
            /// <summary>**事实位是否已安装**：false ⇒ 两通道在矩阵里一律 unknown（不是 false）。</summary>
            public bool Installed;
            /// <summary>GUI 控制台装配（Bootstrap: ConsoleLayer.Wire）。</summary>
            public bool GuiOpen;
            /// <summary>命名管道 v2 已启动（Bootstrap: PipeConsole.Start）。</summary>
            public bool PipeOpen;
            public string PipeName = "";
            /// <summary>事实位来源/未安装原因（人读；未知时进 reason）。</summary>
            public string Note = "";
        }

        /// <summary>游戏侧采样编排：只读标量就地读；非标量游戏状态按 RouteFor 就地/marshal/置 null。绝不抛。</summary>
        public static StatusEnvironment Sample(SlashContext ctx)
        {
            var env = new StatusEnvironment();
            if (ctx == null) ctx = new SlashContext();

            // ---- 载荷（门）与探针：HTTP 时代的只读面（当时就是从 HTTP 后台线程调），不额外 marshal ----
            string gateRaw = null;
            if (ctx.Host == null)
                env.Failures.Add("ReplHost 未接线（ctx.Host 为 null）");
            else
            {
                try { gateRaw = ctx.Host.GateStatus(); }
                catch (Exception ex) { env.Failures.Add("ReplHost.GateStatus: " + Short(ex)); }
            }
            env.Gate = ParseGateStatus(gateRaw);
            if (ctx.Host == null)
                env.Gate["failure"] = "ReplHost 未接线（ctx.Host 为 null）";

            if (ctx.Host == null)
            {
                env.ProbeA = new Dictionary<string, object>
                {
                    ["positive"] = false,
                    ["negative"] = false,
                    ["details"] = "ReplHost 未接线，探针未跑"
                };
            }
            else
            {
                try
                {
                    Dictionary<string, object> probe = ctx.Host.ProbeA();
                    if (probe != null) env.ProbeA = probe;
                }
                catch (Exception ex)
                {
                    env.ProbeA = new Dictionary<string, object>
                    {
                        ["positive"] = false,
                        ["negative"] = false,
                        ["details"] = "ProbeA 抛出: " + Short(ex)
                    };
                    env.Failures.Add("ReplHost.ProbeA: " + Short(ex));
                }
            }

            // ---- 执行器：只读标量，任何线程安全 ----
            if (ctx.Executor == null)
                env.Failures.Add("Executor 未接线（ctx.Executor 为 null）");
            else
            {
                try { env.Ticks = ctx.Executor.TickCount; }
                catch (Exception ex) { env.Failures.Add("Executor.TickCount: " + Short(ex)); }
                try { env.Revision = ctx.Executor.Revision; }
                catch (Exception ex) { env.Failures.Add("Executor.Revision: " + Short(ex)); }
                try { env.Pending = ctx.Executor.PendingCount; }
                catch (Exception ex) { env.Failures.Add("Executor.PendingCount: " + Short(ex)); }
                try { env.Poisoned = ctx.Executor.Poisoned; }
                catch (Exception ex) { env.Failures.Add("Executor.Poisoned: " + Short(ex)); }
            }

            // ---- 只读标量：List.Count 单字段读，绝不遍历 Entries（会与渲染线程争用）----
            try { env.ConsoleLogCount = ConsoleLog.Count; }
            catch (Exception ex) { env.Failures.Add("ConsoleLog.Count: " + Short(ex)); }

            // ---- 通道矩阵 / 能力前置 ----
            env.Channel = ctx.Channel ?? "";
            ChannelFlags flags = ChannelFlagsFromFacts();
            env.Channels = ChannelMatrix(flags, out env.AgentChannelOpen);
            env.ChannelsNote = flags.Installed ? "" : flags.Note;
            string harmonyDetail;
            env.HarmonyState = HarmonyStateOf(out harmonyDetail);
            env.HarmonyDetail = harmonyDetail;

            // ---- 非标量游戏状态：只在游戏线程采样（就地或 marshal）----
            // 判据只认 ctx.IsGameThread（契约 v1.1）；壳层在管道下必须自己填，拿不准就 false。
            int route = RouteFor(ctx.IsGameThread, ctx.Executor != null);
            if (route == RouteInline)
            {
                SampleGameState(env);
                env.GameStateSampled = true;
            }
            else if (route == RouteMarshal)
            {
                // 独立容器：超时后闭包仍可能被 Tick 执行，绝不回头污染已返回的对象。
                var gameEnv = new StatusEnvironment();
                ReplRequest req = ctx.Executor.Enqueue(
                    delegate { SampleGameState(gameEnv); return ReplResult.Success(null); }, MarshalTimeoutMs);

                bool done;
                string waitError = null;
                try { done = req.Tcs.Task.Wait(MarshalTimeoutMs); }
                catch (Exception ex) { done = false; waitError = Short(ex); }
                if (!done) ctx.Executor.MarkTimedOut(req.Id);   // 与 SessionEval 同款：排队请求收口，别让它变成孤儿

                ReplResult outcome = OutcomeOf(req);
                if (done && outcome != null && outcome.Ok)
                {
                    CopyGameState(gameEnv, env);
                    env.GameStateSampled = true;
                }
                else
                {
                    env.GameStateNote = MarshalFailureReason(outcome, waitError);
                    env.Failures.Add("游戏状态采样失败: " + env.GameStateNote);
                }
            }
            else
            {
                env.GameStateNote = NoChannelNote;
                env.Failures.Add("游戏状态采样失败: " + env.GameStateNote);
            }

            return env;
        }

        /// <summary>
        /// 取数路径判定（纯逻辑，可单测）：已在游戏线程 → 就地采；否则有 Executor → marshal；
        /// 再否则 → 置 null 并说明（**不**裸读、**不**填假值）。
        /// onGameThread 由调用方传 **ctx.IsGameThread**（契约 v1.1 的事实位）—— 本文件不自带启发式。
        /// </summary>
        public static int RouteFor(bool onGameThread, bool hasExecutor)
        {
            if (onGameThread) return RouteInline;
            return hasExecutor ? RouteMarshal : RouteNoChannel;
        }

        /// <summary>
        /// marshal 结束但没拿到采样时把原因折算成一行（纯逻辑，可单测）。优先级：
        /// TimedOut → 超时文案；Error 非空 → **原样带出**（Executor.Poisoned 时 Tcs 上是
        /// "session poisoned"，handler 根本没跑，吞掉它就只剩一句无用泛化）；Wait 抛出的异常 →
        /// 原样带出；都没有 → 泛化文案。独立成纯函数正是为了让 "session poisoned" 这条有回归测试。
        /// </summary>
        public static string MarshalFailureReason(ReplResult outcome, string waitError)
        {
            const string tail = "（stage 与四个布尔置 null = 未知，不给假值）";
            if (outcome != null && outcome.TimedOut)
                return "marshal 超时：游戏更新线程 " + MarshalTimeoutMs + "ms 内未排空队列" + tail;
            if (outcome != null && !outcome.Ok && !string.IsNullOrEmpty(outcome.Error))
                return "marshal 失败: " + outcome.Error + tail;
            if (!string.IsNullOrEmpty(waitError))
                return "marshal 失败: " + waitError + tail;
            return "marshal 失败：游戏更新线程未取到采样（handler 未执行或未排空）" + tail;
        }

        /// <summary>取 marshal 请求的终值；未完成 / 已取消 / 取值抛异常一律回 null（不发散）。</summary>
        public static ReplResult OutcomeOf(ReplRequest req)
        {
            try
            {
                if (req == null || req.Tcs == null || !req.Tcs.Task.IsCompleted) return null;
                return req.Tcs.Task.Result;
            }
            catch { return null; }
        }

        /// <summary>
        /// **只能在游戏更新线程上调用**：逐项 try/catch，单项失败时那个字段保持 null（未知）并把
        /// 点名记进 Failures —— 不把「读失败」写成 false。
        /// </summary>
        public static void SampleGameState(StatusEnvironment e)
        {
            if (e == null) return;

            bool? loading = null, inEntry = null, inPlay = null, playDataLoaded = null, paused = null;
            string programState = null;
            int? mapCount = null;
            ProgramState ps = ProgramState.Entry;
            bool psKnown = false;

            try { loading = LongEventHandler.ShouldWaitForEvent; }
            catch (Exception ex) { e.Failures.Add("LongEventHandler.ShouldWaitForEvent: " + Short(ex)); }

            try { inEntry = GenScene.InEntryScene; }
            catch (Exception ex) { e.Failures.Add("GenScene.InEntryScene: " + Short(ex)); }

            try { inPlay = GenScene.InPlayScene; }
            catch (Exception ex) { e.Failures.Add("GenScene.InPlayScene: " + Short(ex)); }

            try { playDataLoaded = PlayDataLoader.Loaded; }
            catch (Exception ex) { e.Failures.Add("PlayDataLoader.Loaded: " + Short(ex)); }

            try { ps = Current.ProgramState; psKnown = true; programState = ps.ToString(); }
            catch (Exception ex) { e.Failures.Add("Current.ProgramState: " + Short(ex)); }

            // Find.Maps => Current.Game?.Maps：Game 为 null 时是 null（不是空表），必须判空。
            try
            {
                List<Map> maps = Find.Maps;
                mapCount = maps == null ? 0 : maps.Count;
            }
            catch (Exception ex) { e.Failures.Add("Find.Maps: " + Short(ex)); }

            // 暂停只在游戏实例存在时可读；主菜单/加载期 Current.Game 为 null → false（加载中由 loading 表达）。
            // TickManager.Paused 含 ForcePaused（长事件/窗口强制暂停），正是「paused 表示长事件/加载中」的口径。
            try
            {
                Game game = Current.Game;
                paused = game != null && game.tickManager != null && game.tickManager.Paused;
            }
            catch (Exception ex) { e.Failures.Add("Current.Game.tickManager.Paused: " + Short(ex)); }

            e.Loading = loading;
            e.InEntry = inEntry;
            e.InPlay = inPlay;
            e.PlayDataLoaded = playDataLoaded;
            e.ProgramState = programState;
            e.MapCount = mapCount;
            e.Paused = paused;

            e.Stage = (loading.HasValue && playDataLoaded.HasValue && inEntry.HasValue && inPlay.HasValue)
                ? ComputeStage(loading.Value, playDataLoaded.Value, inEntry.Value, inPlay.Value)
                : null;
            // inGame：需要 Play 场景 + ProgramState（两件事实缺一即未知）。
            e.InGame = (inPlay.HasValue && psKnown) ? (bool?)(inPlay.Value && ps == ProgramState.Playing) : null;
            // mainMenuReady：语义是「主菜单且可交互」，故含 !loading —— 它与 loading **互补**（不可能同时为真），
            // 不是「正交」意义上的独立布尔（brief 里的四个布尔说的是它们不是互斥枚举，不是两两独立）。
            e.MainMenuReady = (inEntry.HasValue && loading.HasValue && playDataLoaded.HasValue)
                ? (bool?)(inEntry.Value && !loading.Value && playDataLoaded.Value)
                : null;
        }

        /// <summary>把 marshal 容器里的游戏状态搬进主容器（含 marshal 期间产生的采样失败点名）。</summary>
        public static void CopyGameState(StatusEnvironment from, StatusEnvironment to)
        {
            if (from == null || to == null) return;
            to.Stage = from.Stage;
            to.InGame = from.InGame;
            to.Loading = from.Loading;
            to.MainMenuReady = from.MainMenuReady;
            to.Paused = from.Paused;
            to.ProgramState = from.ProgramState;
            to.MapCount = from.MapCount;
            to.InEntry = from.InEntry;
            to.InPlay = from.InPlay;
            to.PlayDataLoaded = from.PlayDataLoaded;
            foreach (string f in from.Failures) to.Failures.Add(f);
        }

        /// <summary>
        /// 读 **ChannelFacts 事实位**（Bootstrap 在实际启动每条通道时写入）—— 纯 BCL，零 Verse。
        /// 事实位未安装（离线单测 / Bootstrap 尚未跑到）⇒ <c>Installed=false</c>，通道一律未知；
        /// **绝不**回退到重放命令行旗标（那正是被替换掉的「可证伪字段靠猜」）。
        /// </summary>
        public static ChannelFlags ChannelFlagsFromFacts()
        {
            var f = new ChannelFlags();
            ChannelFacts.Snapshot s = ChannelFacts.Current;
            if (s == null)
            {
                f.Installed = false;
                f.Note = "通道事实位未安装（ChannelFacts 未被 Bootstrap 写入：离线单测，或 Bootstrap 尚未跑到那段）—— 两通道一律未知";
                return f;
            }
            f.Installed = true;
            f.GuiOpen = s.Gui;
            f.PipeOpen = s.Pipe;
            f.PipeName = s.PipeName ?? "";
            f.Note = "事实位：Bootstrap 启动每条通道时写入";
            return f;
        }

        /// <summary>
        /// 逐通道可用性（纯逻辑，可单测）。有信息量的是「本实例实际开了哪些通道」，
        /// 不是「当前这条请求的通道是否开着」—— 后者对 /status 而言恒真（指令只能被通道调用）。
        /// 事实位未安装 ⇒ available 为 **null（未知）**，不是 false。
        /// </summary>
        public static List<Dictionary<string, object>> ChannelMatrix(ChannelFlags f, out bool agentChannelOpen)
        {
            if (f == null) f = new ChannelFlags();
            var list = new List<Dictionary<string, object>>();

            if (!f.Installed)
            {
                string why = NonEmpty(f.Note) ? f.Note : "通道事实位未安装 —— 未知";
                list.Add(Channel("gui", null, why));
                list.Add(Channel("pipe", null, why));
                agentChannelOpen = false;
                return list;
            }

            list.Add(Channel("gui", f.GuiOpen,
                f.GuiOpen ? "Bootstrap 装配了 GUI 控制台（ConsoleLayer.Wire）"
                          : "本实例未装配 GUI 控制台"));
            list.Add(Channel("pipe", f.PipeOpen,
                f.PipeOpen ? "Bootstrap 启动了命名管道 v2（PipeConsole.Start: " + OneLine(f.PipeName, 40) + "）"
                           : "本实例未启动命名管道 v2"));
            agentChannelOpen = f.PipeOpen;
            return list;
        }

        /// <summary>0Harmony 的三态：loaded | not-loaded | unknown（判不出来**不**报成未装载）。</summary>
        public static string HarmonyStateOf(out string detail)
        {
            detail = "";
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name;
                    try { name = asm.GetName().Name; }
                    catch (Exception ex) { detail = "程序集名读取失败: " + Short(ex); return "unknown"; }
                    if (name == "0Harmony") { detail = "已在已加载程序集清单中"; return "loaded"; }
                }
                detail = "已加载程序集清单里没有 0Harmony";
                return "not-loaded";
            }
            catch (Exception ex)
            {
                detail = "枚举程序集失败: " + Short(ex);
                return "unknown";
            }
        }

        // ================= 渲染（纯函数，零 Verse 依赖） =================

        /// <summary>把采样结果渲染成 SlashResult（Data + Lines 同源）。纯函数，零 Verse 依赖。</summary>
        public static SlashResult Compose(StatusEnvironment env)
        {
            if (env == null) env = new StatusEnvironment();

            var r = new SlashResult { Kind = "status" };

            bool payloadLoaded = (bool)env.Gate["loaded"];
            int payloadCount = (int)env.Gate["count"];
            string gateFailure = (string)env.Gate["failure"];
            string gateDiag = (string)env.Gate["diag"];
            string gateScripting = (string)env.Gate["scripting"];
            int gateEvalThread = (int)env.Gate["evalThread"];

            bool probePositive = ProbePositive(env.ProbeA);
            bool probeNegative = ProbeNegative(env.ProbeA);
            bool probePassed = probePositive && probeNegative;
            bool ok = ComputeOk(payloadLoaded, probePassed);
            string retrievedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

            // ---- 能力矩阵：至少四项 payload / autoUsing / harmony / channels ----
            string payloadReason = payloadLoaded
                ? "Roslyn 载荷已装载（count=" + payloadCount + "，期望 " + ExpectedPayloadCount
                  + "；权威值 = gate.count）；/eval 可用"
                : "Roslyn 载荷未装载"
                  + (NonEmpty(gateFailure) ? "：" + OneLine(gateFailure, 160) : "")
                  + "；/eval 会以门失败返回";

            string harmonyReason;
            bool harmonyReady = env.HarmonyState == "loaded";
            if (env.HarmonyState == "loaded")
                harmonyReason = "0Harmony 已装载（/hook 的前置必要条件；补丁清单归 /hook list）";
            else if (env.HarmonyState == "not-loaded")
                harmonyReason = "0Harmony 未装载（/hook 不可用）";
            else
                harmonyReason = "0Harmony 未判定（" + NullToDash(env.HarmonyDetail)
                    + "）—— 判不出来就不下结论（ready=false 表示「不可断言」，不表示「没装」）";

            string channelsReason = "本实例对外通道: gui=" + ChannelOpenText(env.Channels, "gui")
                + "; pipe=" + ChannelOpenText(env.Channels, "pipe")
                + "；来源=ChannelFacts 事实位（Bootstrap 启动每条通道时写入）"
                + (NonEmpty(env.ChannelsNote) ? "；" + OneLine(env.ChannelsNote, 160) : "");

            var capabilities = new List<Dictionary<string, object>>
            {
                Capability("payload", "ReplHost.GateStatus", payloadLoaded, payloadReason),
                Capability("autoUsing", "RoslynEngine（T5 内建）", payloadLoaded,
                    payloadLoaded
                        ? "编译失败时按类型短名自动补 using（单次上限 5 条，不写回持久表）"
                        : "编译器不可用（载荷未装载）"),
                Capability("harmony", "0Harmony 程序集", harmonyReady, harmonyReason),
                Capability("channels", "ChannelFacts（Bootstrap 写入）", env.AgentChannelOpen, channelsReason)
            };

            var data = new Dictionary<string, object>
            {
                ["stage"] = env.Stage,
                ["inGame"] = env.InGame,
                ["loading"] = env.Loading,
                ["mainMenuReady"] = env.MainMenuReady,
                ["paused"] = env.Paused,
                ["ticks"] = env.Ticks,
                ["revision"] = env.Revision,
                ["pending"] = env.Pending,
                ["poisoned"] = env.Poisoned,
                ["consoleLog"] = env.ConsoleLogCount,
                ["gate"] = env.Gate,
                ["probeA"] = env.ProbeA,
                ["capabilities"] = capabilities,
                ["channels"] = env.Channels,
                ["ok"] = ok,
                ["retrievedAt"] = retrievedAt,
                ["channel"] = env.Channel
            };
            r.Data = data;

            // ---- 人类可读行：阶段 / 执行器 / 载荷 / 探针 / 能力 / 提示 ----
            r.Lines.Add("[阶段] stage=" + StageText(env)
                + "  inGame=" + B(env.InGame) + " loading=" + B(env.Loading)
                + " mainMenuReady=" + B(env.MainMenuReady) + " paused=" + B(env.Paused));
            r.Lines.Add("[阶段] 场景 Entry=" + B(env.InEntry) + " Play=" + B(env.InPlay)
                + "；PlayDataLoader.Loaded=" + B(env.PlayDataLoaded)
                + "；ProgramState=" + NullToDash(env.ProgramState)
                + "；Maps=" + (env.MapCount.HasValue ? env.MapCount.Value.ToString(CultureInfo.InvariantCulture) : "?"));
            string missingFacts = MissingFacts(env);
            if (!env.GameStateSampled)
                r.Lines.Add("[阶段] 未采样: " + OneLine(env.GameStateNote, 200));
            else if (missingFacts.Length > 0)
                r.Lines.Add("[阶段] 已采样但部分事实读失败: " + missingFacts + "（点名见 [提示]）");

            r.Lines.Add("[执行器] ticks=" + env.Ticks + " revision=" + env.Revision
                + " pending=" + env.Pending + " poisoned=" + B(env.Poisoned)
                + " consoleLog=" + (env.ConsoleLogCount < 0 ? "(不可读)" : env.ConsoleLogCount.ToString(CultureInfo.InvariantCulture)));

            // M1：N/8 会在载荷件数变化时误导 —— 报 count=N（期望 N）并点名权威值。
            r.Lines.Add("[载荷] loaded=" + B(payloadLoaded)
                + " count=" + payloadCount + "（期望 " + ExpectedPayloadCount + "；权威值 = gate.count）"
                + " scripting=" + Dash(gateScripting) + " evalThread=" + gateEvalThread);
            if (NonEmpty(gateFailure))
                r.Lines.Add("[载荷] failure=" + OneLine(gateFailure, 200));
            if (NonEmpty(gateDiag))
                r.Lines.Add("[载荷] diag=" + OneLine(gateDiag, 200));

            r.Lines.Add("[探针] probeA positive=" + B(probePositive) + " negative=" + B(probeNegative)
                + " → " + (probePassed ? "通过" : "未通过"));
            string probeDetails = DetailOf(env.ProbeA);
            if (NonEmpty(probeDetails))
                r.Lines.Add("[探针] details=" + OneLine(probeDetails, 200));

            foreach (Dictionary<string, object> cap in capabilities)
                r.Lines.Add("[能力] " + (string)cap["cap"] + " ready=" + B((bool)cap["ready"])
                    + " provider=" + (string)cap["provider"] + " —— " + (string)cap["reason"]);
            foreach (Dictionary<string, object> ch in env.Channels)
                r.Lines.Add("[能力] 通道 " + (string)ch["channel"] + " available=" + B((bool?)ch["available"])
                    + " —— " + (string)ch["reason"]);

            r.Lines.Add("[提示] ok=" + B(ok) + " retrievedAt=" + retrievedAt
                + " channel=" + (NonEmpty(env.Channel) ? env.Channel : "未接线"));
            r.Lines.Add("[提示] 冻结/停滞判定归客户端（本进程不做冻结诊断 —— 断点冻结时会话线程同样被冻）");
            if (env.Failures.Count > 0)
            {
                var parts = new List<string>();
                foreach (string f in env.Failures) parts.Add(OneLine(f, 120));
                r.Lines.Add("[提示] 采样失败 " + env.Failures.Count + " 项: " + string.Join("；", parts));
            }

            return r;
        }

        // ================= 纯函数（可离线单测） =================

        /// <summary>
        /// stage：stopped | starting | main_menu | in_game。
        /// 判定序（进程内事实）：长事件/加载中 → starting；游玩数据未装载（启动期）→ starting；
        /// Entry 场景 → main_menu；Play 场景 → in_game；其余（场景切换空档/启动极早期）→ stopped。
        /// 事实不全时不要调它 —— 调用方（SampleGameState）保持 stage=null（未知）。
        /// </summary>
        public static string ComputeStage(bool loading, bool playDataLoaded, bool inEntry, bool inPlay)
        {
            if (loading) return "starting";
            if (!playDataLoaded) return "starting";
            if (inEntry) return "main_menu";
            if (inPlay) return "in_game";
            return "stopped";
        }

        /// <summary>
        /// 已采样但仍是 null 的游戏事实名单（纯逻辑，可单测）。Compose 用它区分
        /// 「未采样（根本没采，原因见 GameStateNote）」与「采了但某项读失败（点名见 [提示]）」——
        /// 不区分就会渲染出 stage=(未采样) 与 Entry=true 并存的自相矛盾行。
        /// </summary>
        public static string MissingFacts(StatusEnvironment env)
        {
            if (env == null) return "";
            var missing = new List<string>();
            if (!env.Loading.HasValue) missing.Add("Loading");
            if (!env.PlayDataLoaded.HasValue) missing.Add("PlayDataLoaded");
            if (!env.InEntry.HasValue) missing.Add("InEntry");
            if (!env.InPlay.HasValue) missing.Add("InPlay");
            if (env.ProgramState == null) missing.Add("ProgramState");
            if (!env.MapCount.HasValue) missing.Add("MapCount");
            if (!env.Paused.HasValue) missing.Add("Paused");
            return string.Join(", ", missing);
        }

        /// <summary>ok = 载荷已装载 且 probeA 正负探针都通过（无冻结项 —— 见文件头）。</summary>
        public static bool ComputeOk(bool payloadLoaded, bool probePassed)
        {
            return payloadLoaded && probePassed;
        }

        /// <summary>正负探针都通过才算 probeA 通过（缺字段/类型不符一律算不通过）。</summary>
        public static bool ProbePassed(Dictionary<string, object> probeA)
        {
            return ProbePositive(probeA) && ProbeNegative(probeA);
        }

        public static bool ProbePositive(Dictionary<string, object> probeA)
        {
            return BoolField(probeA, "positive");
        }

        public static bool ProbeNegative(Dictionary<string, object> probeA)
        {
            return BoolField(probeA, "negative");
        }

        public static string DetailOf(Dictionary<string, object> probeA)
        {
            object v;
            if (probeA != null && probeA.TryGetValue("details", out v)) return v as string ?? "";
            return "";
        }

        private static bool BoolField(Dictionary<string, object> dict, string key)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v) && v is bool) return (bool)v;
            return false;
        }

        /// <summary>
        /// 解析 ReplHost.GateStatus() 的 "k=v;k=v" 串（键固定：loaded/count/failure/diag/scripting/evalThread）。
        /// 值里可能含 ';' 与 '='（diag 是任意诊断文本，scripting 形如 "X=已装载(前置)"）：
        /// 不是已知键的段并入上一段的值（用 ';' 还原），已知键只按**首个** '=' 切分。
        /// </summary>
        public static Dictionary<string, object> ParseGateStatus(string text)
        {
            var gate = new Dictionary<string, object>
            {
                ["loaded"] = false,
                ["count"] = 0,
                ["failure"] = "",
                ["diag"] = "",
                ["scripting"] = "",
                ["evalThread"] = 0
            };
            if (string.IsNullOrEmpty(text)) return gate;

            string[] keys = { "loaded", "count", "failure", "diag", "scripting", "evalThread" };
            var raw = new Dictionary<string, string>(StringComparer.Ordinal);
            string current = null;
            foreach (string seg in text.Split(';'))
            {
                int eq = seg.IndexOf('=');
                string key = eq > 0 ? seg.Substring(0, eq).Trim() : null;
                if (key != null && Array.IndexOf(keys, key) >= 0)
                {
                    current = key;
                    raw[key] = seg.Substring(eq + 1);
                }
                else if (current != null)
                {
                    raw[current] = raw[current] + ";" + seg;
                }
            }

            string s;
            if (raw.TryGetValue("loaded", out s))
                gate["loaded"] = string.Equals(s.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            if (raw.TryGetValue("count", out s))
                gate["count"] = ParseInt(s, 0);
            if (raw.TryGetValue("evalThread", out s))
                gate["evalThread"] = ParseInt(s, 0);
            if (raw.TryGetValue("failure", out s))
                gate["failure"] = s;
            if (raw.TryGetValue("diag", out s))
                gate["diag"] = s;
            if (raw.TryGetValue("scripting", out s))
                gate["scripting"] = s;
            return gate;
        }

        // ================= 小工具 =================

        private static Dictionary<string, object> Capability(string cap, string provider, bool ready, string reason)
        {
            return new Dictionary<string, object>
            {
                ["cap"] = cap,
                ["provider"] = provider,
                ["ready"] = ready,
                ["reason"] = reason ?? ""
            };
        }

        private static Dictionary<string, object> Channel(string channel, bool? available, string reason)
        {
            return new Dictionary<string, object>
            {
                ["channel"] = channel,
                ["available"] = available,
                ["reason"] = reason ?? ""
            };
        }

        /// <summary>从通道矩阵里取某个通道的「开/未开/未知」文案（纯逻辑）。</summary>
        public static string ChannelOpenText(List<Dictionary<string, object>> channels, string channel)
        {
            if (channels != null)
            {
                foreach (Dictionary<string, object> c in channels)
                {
                    if (c == null || (string)c["channel"] != channel) continue;
                    bool? available = (bool?)c["available"];
                    if (!available.HasValue) return "未知";
                    return available.Value ? "开" : "未开";
                }
            }
            return "未知";
        }

        private static int ParseInt(string s, int fallback)
        {
            int n;
            if (int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return fallback;
        }

        private static bool NonEmpty(string s) { return !string.IsNullOrEmpty(s); }

        private static string B(bool v) { return v ? "true" : "false"; }

        /// <summary>三态渲染：true / false / ?（未采样 —— 不是 false）。</summary>
        private static string B(bool? v) { return v.HasValue ? (v.Value ? "true" : "false") : "?"; }

        private static string Dash(string s) { return NonEmpty(s) ? s : "-"; }

        private static string NullToDash(string s) { return NonEmpty(s) ? s : "-"; }

        /// <summary>stage 的三态渲染：值 / (读失败) / (未采样) —— 后两者不可混用，否则与同组事实行自相矛盾。</summary>
        private static string StageText(StatusEnvironment env)
        {
            if (env != null && NonEmpty(env.Stage)) return env.Stage;
            return (env != null && env.GameStateSampled) ? "(读失败)" : "(未采样)";
        }

        private static string Short(Exception ex)
        {
            return ex == null ? "unknown" : ex.GetType().Name + ": " + ex.Message;
        }

        /// <summary>把任意文本压成一行（换行/制表折叠，超长截断）——Data 里保留原文，只有人类行压平。</summary>
        private static string OneLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Replace("\r\n", " ⏎ ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            t = t.Trim();
            if (max > 0 && t.Length > max) t = t.Substring(0, max) + "…";
            return t;
        }
    }
}
