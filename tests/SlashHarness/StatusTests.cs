// RoslynProbe —— /status 的单测（零依赖：public static int Run() 返回失败数，由 SlashTestHarness 反射发现）。
//
// 覆盖（**如实分档**，勿把「守卫式用例」当离线强覆盖）：
//   离线强覆盖（纯逻辑，零 Verse）：GateStatus 串解析、stage 判定（ComputeStage）、probeA 通过判据、
//     ok 计算、RouteFor（就地/marshal/无通道）、MarshalFailureReason（含 "session poisoned" 原样带出）、
//     ChannelFlagsFromFacts + ChannelMatrix（逐通道可用性；事实位未安装→null 未知）、HarmonyStateOf 三态、
//     Compose 的 Data/Lines 形状、
//     **反射门：StatusCommand 除 const 外不得有静态字段**（防重新引入进程内冻结诊断所需的状态）。
//   触 Verse 的用例（在游戏同程序集 JIT 得到时才算数；离线 JIT 失败会被记为 NOTE 跳过、不算失败）：
//     SampleGameState 就地采样的「不抛 + 失败字段保持 null（不谎报）」不变量；
//     Sample/Provide 在 Channel="stdio" + Executor=null 下的**明确降级**分支（stage/四个布尔 = null）。
//   ⚠️ **本文件离线未覆盖**：① marshal 成功路径（Enqueue→Tick→TCS 回传→CopyGameState）；
//     ④ 场景/暂停的真实语义值 —— 这两项只在游戏进程里成立。
//   真机侧：① 的同类机制由 battery 48 项覆盖（每项都经 Executor 派发，见
//   docs/boundary-suite/t6/REPORT-T6-acceptance.md §6.3.1）；④ 在上述真机报告里没有对应小节 ⇒ 保留目录级指引。
//   另：**Bootstrap 真的写 ChannelFacts** 与 **壳层把 ctx.IsGameThread 填对**（契约 v1.1 的事实位）
//   已由 WiringCoverageTests 走真接线代码离线覆盖（BootstrapWiring.InstallChannelFacts / 两个壳的派发入口）；
//   本文件仍只验「读事实位」与「未安装=未知」两侧。
//   判据收口（契约 v1.1，README §3.4.1）：本文件的 provider **只读 ctx.IsGameThread**，
//     不再有 Channel=="gui" / UnityData.IsInMainThread 这类本地启发式（v1.1 点名要收口的四套启发式之一）。
//
// 控制者修订的回归门（两条）：
//   1) Data 不得含 stallHint（含探针失败与未采样路径）；
//   2) typeof(StatusCommand) 的静态字段只允许 const —— 抓「重新引入静态采样字段/时间戳」。

using System;
using System.Collections.Generic;
using System.Reflection;
using RoslynProbe;
using RoslynProbe.Repl;

namespace RoslynProbe.Tests
{
    /// <summary>StatusCommand 的断言套件。</summary>
    public static class StatusCommandTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";
        private static int checks;

        /// <summary>返回失败数；由 SlashTestHarness 反射发现并调用。</summary>
        public static int Run()
        {
            Failures.Clear();
            checks = 0;
            currentCase = "(none)";

            Case("GateStatus 串解析：基本字段 + 值里自带 ';' / '='", ParseGateCases);
            Case("GateStatus 串解析：空串/缺字段/非数字的兜底", ParseGateFallbackCases);
            Case("stage 判定：starting / main_menu / in_game / stopped", StageCases);
            Case("probeA 通过判据：正负都要 true，缺字段算不通过", ProbeCases);
            Case("ok 计算：载荷已装载 且 探针通过（无冻结项）", OkCases);
            Case("取数路径：就地 / marshal / 无通道（不裸读）", RouteCases);
            Case("marshal 失败原因：超时/poisoned/Wait 异常/泛化，且都说明置 null", MarshalReasonCases);
            Case("通道矩阵：逐通道可用性（未安装→null 未知，装了→按事实；不报成未开）", ChannelMatrixCases);
            Case("ChannelFacts 事实位：未安装=未知、逐条 Install、重置后可重复跑", ChannelFactsCases);
            Case("Harmony 三态：loaded/not-loaded/unknown（unknown 不报成未装载）", HarmonyStateCases);
            Case("反射门：StatusCommand 除 const 外不得有静态字段", NoStaticStateCases);
            Case("分派：provider 未注册 → missing-capability 行", DispatchUnregisteredCases);
            Case("Compose：Data 键完整、能力矩阵四项、通道矩阵、无 stallHint/consoleOpen", ComposeDataCases);
            Case("Compose：Lines 单行 + 六组前缀 + count=N（期望 8）措辞", ComposeLineCases);
            Case("Compose：未采样时 stage/四个布尔为 null 且行里说明为什么", UnsampledCases);
            Case("Compose：已采样但部分事实读失败 → stage=(读失败) + 缺失名单（不写 (未采样)）", PartialFactCases);
            Case("MissingFacts：缺失名单只列真正没采到的事实", MissingFactsCases);
            Case("就地采样不变量：不抛，且失败字段保持 null（不写成 false）", InPlaceSampleCases);
            Case("降级分支：stdio + Executor=null → 明确置 null（不裸读、不假值）", DegradeCases);
            Case("判据只认 ctx.IsGameThread（契约 v1.1）：不看 Channel、不依赖本地启发式", CtxFactRouteCases);
            Case("Provide 冒烟：未接线也不抛，Data 键齐全", ProvideSmokeCases);

            // 收尾：把表还原成"只含真实登记"的状态（别的写手的测试也这么做）。
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();

            Console.WriteLine("StatusCommandTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- 用例 ----------------

        private static void ParseGateCases()
        {
            Dictionary<string, object> g = StatusCommand.ParseGateStatus(
                "loaded=True;count=8;failure=;diag=WARN a=1;b=2;scripting=Microsoft.CodeAnalysis.Scripting=已装载(前置);evalThread=42");
            Check((bool)g["loaded"] == true, "loaded 应为 true，实际 " + Show(g["loaded"]));
            Check((int)g["count"] == 8, "count 应为 8，实际 " + Show(g["count"]));
            Check((string)g["failure"] == "", "failure 应为空串，实际 " + Show(g["failure"]));
            Check((string)g["diag"] == "WARN a=1;b=2", "diag 里的 ';' 应还原，实际 " + Show(g["diag"]));
            Check((string)g["scripting"] == "Microsoft.CodeAnalysis.Scripting=已装载(前置)",
                "scripting 里的 '=' 应保留，实际 " + Show(g["scripting"]));
            Check((int)g["evalThread"] == 42, "evalThread 应为 42，实际 " + Show(g["evalThread"]));

            Check((bool)StatusCommand.ParseGateStatus("loaded=true")["loaded"] == true, "小写 true 也应识别");
            Check((bool)StatusCommand.ParseGateStatus("loaded=False")["loaded"] == false, "False 应为 false");
            Check((int)StatusCommand.ParseGateStatus("count=notanint")["count"] == 0, "count 非数字应兜底 0");
        }

        private static void ParseGateFallbackCases()
        {
            foreach (string text in new[] { null, "", "garbage", "loaded" })
            {
                Dictionary<string, object> g = StatusCommand.ParseGateStatus(text);
                Check((bool)g["loaded"] == false, "兜底 loaded 应为 false（输入 " + Show(text) + "）");
                Check((int)g["count"] == 0, "兜底 count 应为 0（输入 " + Show(text) + "）");
                Check((string)g["failure"] == "" && (string)g["diag"] == "" && (string)g["scripting"] == "",
                    "兜底文本字段应为空串（输入 " + Show(text) + "）");
                Check((int)g["evalThread"] == 0, "兜底 evalThread 应为 0");
                Check(g.Count == 6, "gate 应恰好 6 个键，实际 " + g.Count);
            }
        }

        private static void StageCases()
        {
            Check(StatusCommand.ComputeStage(true, true, true, false) == "starting", "长事件中应为 starting");
            Check(StatusCommand.ComputeStage(false, false, true, false) == "starting", "游玩数据未装载应为 starting");
            Check(StatusCommand.ComputeStage(false, true, true, false) == "main_menu", "Entry 场景应为 main_menu");
            Check(StatusCommand.ComputeStage(false, true, false, true) == "in_game", "Play 场景应为 in_game");
            Check(StatusCommand.ComputeStage(false, true, false, false) == "stopped", "都不在应为 stopped");
            Check(StatusCommand.ComputeStage(true, false, false, false) == "starting", "加载优先级高于场景");
        }

        private static void ProbeCases()
        {
            Check(!StatusCommand.ProbePassed(null), "null 探针不算通过");
            Check(!StatusCommand.ProbePassed(new Dictionary<string, object>()), "缺字段不算通过");
            Check(!StatusCommand.ProbePassed(new Dictionary<string, object>
            {
                ["positive"] = true,
                ["negative"] = false
            }), "只有正向不算通过");
            Check(!StatusCommand.ProbePassed(new Dictionary<string, object>
            {
                ["positive"] = "yes",
                ["negative"] = true
            }), "类型不符不算通过");
            Check(StatusCommand.ProbePassed(new Dictionary<string, object>
            {
                ["positive"] = true,
                ["negative"] = true,
                ["details"] = "x"
            }), "正负都通过才算通过");
            Check(StatusCommand.DetailOf(null) == "", "DetailOf(null) 应为空串");
        }

        private static void OkCases()
        {
            Check(StatusCommand.ComputeOk(true, true), "载荷+探针 → ok");
            Check(!StatusCommand.ComputeOk(true, false), "探针不通过 → 不 ok");
            Check(!StatusCommand.ComputeOk(false, true), "载荷未装载 → 不 ok");
            Check(!StatusCommand.ComputeOk(false, false), "都不满足 → 不 ok");
        }

        private static void RouteCases()
        {
            Check(StatusCommand.RouteFor(true, false) == StatusCommand.RouteInline, "游戏线程应就地采（哪怕没有 Executor）");
            Check(StatusCommand.RouteFor(true, true) == StatusCommand.RouteInline, "游戏线程应就地采");
            Check(StatusCommand.RouteFor(false, true) == StatusCommand.RouteMarshal, "非游戏线程 + 有 Executor 应 marshal");
            Check(StatusCommand.RouteFor(false, false) == StatusCommand.RouteNoChannel, "非游戏线程 + 无 Executor 应无通道（置 null）");
        }

        private static void MarshalReasonCases()
        {
            string poisoned = StatusCommand.MarshalFailureReason(new ReplResult { Ok = false, Error = "session poisoned" }, null);
            Check(poisoned.IndexOf("session poisoned", StringComparison.Ordinal) >= 0,
                "poisoned 文案应原样带出 \"session poisoned\"，实际 " + Show(poisoned));
            Check(poisoned.IndexOf("null", StringComparison.Ordinal) >= 0, "poisoned 文案应说明置 null");

            string timeout = StatusCommand.MarshalFailureReason(new ReplResult { TimedOut = true }, null);
            Check(timeout.IndexOf("超时", StringComparison.Ordinal) >= 0, "TimedOut 文案应含「超时」，实际 " + Show(timeout));
            Check(timeout.IndexOf(StatusCommand.MarshalTimeoutMs.ToString(), StringComparison.Ordinal) >= 0,
                "超时文案应带上限 " + StatusCommand.MarshalTimeoutMs);

            string waitErr = StatusCommand.MarshalFailureReason(null, "TaskCanceledException: x");
            Check(waitErr.IndexOf("TaskCanceledException", StringComparison.Ordinal) >= 0, "Wait 异常应原样带出");

            string generic = StatusCommand.MarshalFailureReason(null, null);
            Check(generic.Length > 0 && generic.IndexOf("null", StringComparison.Ordinal) >= 0, "泛化文案也应说明置 null");
        }

        private static void ChannelMatrixCases()
        {
            bool agent;

            // (1) 事实位**未安装** ⇒ 两通道一律 unknown（null），**不是** false —— 未知是一等公民。
            List<Dictionary<string, object>> unknown = StatusCommand.ChannelMatrix(
                new StatusCommand.ChannelFlags { Note = "事实位未安装" }, out agent);
            Check(unknown.Count == 2, "通道矩阵应两条（gui/pipe），实际 " + unknown.Count);
            Check((bool?)unknown[0]["available"] == null && (bool?)unknown[1]["available"] == null,
                "事实位未安装时两通道 available 都应为 null（未知），不能报成未开");
            Check(((string)unknown[0]["reason"]).IndexOf("事实位未安装", StringComparison.Ordinal) >= 0,
                "未安装应写进 reason，实际 " + Show(unknown[0]["reason"]));
            Check(!agent, "未知不得当作开（agentChannelOpen=false）");

            // (2) 装了事实位、只开 gui ⇒ gui 真、pipe 假、agentChannelOpen=false
            List<Dictionary<string, object>> guiOnly = StatusCommand.ChannelMatrix(
                new StatusCommand.ChannelFlags { Installed = true, GuiOpen = true }, out agent);
            Check((bool?)guiOnly[0]["available"] == true, "gui 应报真");
            Check((bool?)guiOnly[1]["available"] == false,
                "未启动的 pipe 应报假（这次是真的「未开」，不是未知）");
            Check(!agent, "只有 gui 时 agentChannelOpen=false");

            // (3) 装了事实位、带名管道 ⇒ pipe 真且 reason 点名管道名
            List<Dictionary<string, object>> pipe = StatusCommand.ChannelMatrix(
                new StatusCommand.ChannelFlags { Installed = true, GuiOpen = true, PipeOpen = true, PipeName = "sess" }, out agent);
            Check((bool?)pipe[1]["available"] == true && agent, "带名管道 → pipe 真且 agentChannelOpen");
            Check(((string)pipe[1]["reason"]).IndexOf("sess", StringComparison.Ordinal) >= 0, "pipe 说明应点名管道名");

            Check(StatusCommand.ChannelOpenText(unknown, "pipe") == "未知", "未安装 → ChannelOpenText 未知");
            Check(StatusCommand.ChannelOpenText(pipe, "pipe") == "开", "ChannelOpenText 开");
            Check(StatusCommand.ChannelOpenText(guiOnly, "pipe") == "未开", "装了事实位且真没开 → 未开");
            Check(StatusCommand.ChannelOpenText(null, "gui") == "未知", "空矩阵应为未知");
        }

        private static void ChannelFactsCases()
        {
            // 事实位本身：未安装=未知；逐条 Install 累加；ResetForTests 后可重复跑（§8.4 幂等）。
            ChannelFacts.ResetForTests();
            Check(!ChannelFacts.Installed && ChannelFacts.Current == null, "重置后事实位应为未安装");

            StatusCommand.ChannelFlags f0 = StatusCommand.ChannelFlagsFromFacts();
            Check(!f0.Installed, "未安装时 ChannelFlagsFromFacts 应报 Installed=false");
            Check(!string.IsNullOrEmpty(f0.Note), "未安装应给出原因（未知是一等公民），实际 " + Show(f0.Note));

            ChannelFacts.InstallGui();
            StatusCommand.ChannelFlags f1 = StatusCommand.ChannelFlagsFromFacts();
            Check(f1.Installed && f1.GuiOpen && !f1.PipeOpen, "只装 gui 时事实应为 gui 真、pipe 假");

            // F2：事实位记的是**可连接全名**（roslyn-console-<短名>），不是命令行短名 ——
            // 消费方按 /status 拿到的名字必须能直接连上。
            ChannelFacts.InstallPipe(PipeConsole.FullPipeName("sess-7"));
            StatusCommand.ChannelFlags f3 = StatusCommand.ChannelFlagsFromFacts();
            Check(f3.PipeOpen && f3.PipeName == "roslyn-console-sess-7",
                "装管道后应带可连接全名，实际 " + Show(f3.PipeName));
            Check(f3.PipeName == PipeConsole.PipeNamePrefix + "sess-7",
                "全名应由 PipeConsole 的唯一拼接点给出，实际 " + Show(f3.PipeName));

            StatusCommand.StatusEnvironment env = StatusCommand.Sample(new SlashContext { Channel = "pipe" });
            Check(env.AgentChannelOpen, "装了 pipe 后 Sample 的 AgentChannelOpen 应为真");
            Check(StatusCommand.ChannelOpenText(env.Channels, "pipe") == "开", "Sample 的通道矩阵应读事实位");

            // 收尾还原：不把安装痕迹留给同进程的其它用例（两遍 RunAll 必须一样绿）。
            ChannelFacts.ResetForTests();
            Check(!ChannelFacts.Installed, "收尾应还原为未安装");
        }

        private static void HarmonyStateCases()
        {
            string detail;
            string state = StatusCommand.HarmonyStateOf(out detail);
            Check(state == "loaded" || state == "not-loaded" || state == "unknown",
                "HarmonyStateOf 只能是三态之一，实际 " + Show(state));
            Check(!string.IsNullOrEmpty(detail), "detail 应非空（可诊断）");

            // unknown 不得被渲染成「未装载」，且 ready=false
            Dictionary<string, object> capUnknown = CapabilityOf("harmony",
                new StatusCommand.StatusEnvironment { HarmonyState = "unknown", HarmonyDetail = "枚举程序集失败: X" });
            Check(!(bool)capUnknown["ready"], "unknown → ready=false");
            Check(((string)capUnknown["reason"]).IndexOf("未判定", StringComparison.Ordinal) >= 0,
                "unknown 的 reason 应说「未判定」，实际 " + Show(capUnknown["reason"]));
            Check(((string)capUnknown["reason"]).IndexOf("未装载", StringComparison.Ordinal) < 0,
                "unknown 绝不能被说成「未装载」");

            Dictionary<string, object> capNo = CapabilityOf("harmony",
                new StatusCommand.StatusEnvironment { HarmonyState = "not-loaded", HarmonyDetail = "清单里没有" });
            Check(!(bool)capNo["ready"] && ((string)capNo["reason"]).IndexOf("未装载", StringComparison.Ordinal) >= 0,
                "not-loaded → ready=false 且 reason 说未装载");

            Dictionary<string, object> capYes = CapabilityOf("harmony",
                new StatusCommand.StatusEnvironment { HarmonyState = "loaded", HarmonyDetail = "在" });
            Check((bool)capYes["ready"] && ((string)capYes["reason"]).IndexOf("已装载", StringComparison.Ordinal) >= 0,
                "loaded → ready=true 且 reason 说已装载");
        }

        private static void NoStaticStateCases()
        {
            FieldInfo[] fields = typeof(StatusCommand).GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Check(fields.Length > 0, "应至少有 ExpectedPayloadCount 等 const 字段");
            foreach (FieldInfo f in fields)
                Check(f.IsLiteral,
                    "StatusCommand 不得有非 const 静态字段（会重新引入进程内冻结诊断所需的静态采样状态）: " + f.Name);
            FieldInfo expected = typeof(StatusCommand).GetField("ExpectedPayloadCount");
            Check(expected != null && expected.IsLiteral, "ExpectedPayloadCount 应是 const");
        }

        private static void DispatchUnregisteredCases()
        {
            SlashRegistry.ResetForTests();
            StatusCommand.Register();

            SlashResult r;
            Check(SlashRegistry.TryDispatch("/status", new SlashContext(), out r), "/status 应被识别");
            Check(r != null && r.Kind == "missing-capability",
                "provider 未注册时 Kind 应为 missing-capability，实际 " + (r == null ? "<null>" : r.Kind));
            Check(r != null && (string)r.Data["capability"] == "status", "Data[\"capability\"] 应点名 status");
        }

        private static void ComposeDataCases()
        {
            StatusCommand.StatusEnvironment env = GoodEnvironment();
            SlashResult r = StatusCommand.Compose(env);

            Check(r != null && r.Kind == "status", "Kind 应为 status");
            Check(r.Data != null, "Data 不得为 null");

            string[] expect =
            {
                "stage", "inGame", "loading", "mainMenuReady", "paused",
                "ticks", "lastTickAgoMs", "revision", "pending", "poisoned", "consoleLog",
                "gate", "probeA", "capabilities", "channels", "ok", "retrievedAt", "channel"
            };
            foreach (string k in expect)
                Check(r.Data.ContainsKey(k), "Data 缺键: " + k);

            Check(!r.Data.ContainsKey("stallHint"), "控制者修订：Data 不得含 stallHint");
            Check(!r.Data.ContainsKey("consoleOpen"), "禁止：Data 不得含 consoleOpen（归 /ui open 的 alreadyOpen）");

            Check((string)r.Data["stage"] == "main_menu", "stage 应为 main_menu，实际 " + Show(r.Data["stage"]));
            Check((bool)r.Data["ok"] == true, "载荷+探针都满足时 ok 应为 true");
            Check((string)r.Data["channel"] == "pipe", "channel 应为 pipe");
            Check(((string)r.Data["retrievedAt"]).EndsWith("Z", StringComparison.Ordinal),
                "retrievedAt 应为 ISO8601(UTC)，实际 " + Show(r.Data["retrievedAt"]));

            Dictionary<string, object> gate = (Dictionary<string, object>)r.Data["gate"];
            Check(gate.Count == 6, "gate 应恰好 6 个键，实际 " + gate.Count);
            Check((bool)gate["loaded"] == true && (int)gate["count"] == 8, "gate 的 loaded/count 应来自 GateStatus 串");

            Dictionary<string, object> probe = (Dictionary<string, object>)r.Data["probeA"];
            Check(probe.ContainsKey("positive") && probe.ContainsKey("negative") && probe.ContainsKey("details"),
                "probeA 应含 positive/negative/details");

            List<Dictionary<string, object>> caps = (List<Dictionary<string, object>>)r.Data["capabilities"];
            Check(caps.Count >= 4, "能力矩阵至少四项，实际 " + caps.Count);
            var names = new List<string>();
            foreach (Dictionary<string, object> c in caps)
            {
                Check(c.ContainsKey("cap") && c.ContainsKey("provider") && c.ContainsKey("ready") && c.ContainsKey("reason"),
                    "能力项应含 {cap,provider,ready,reason}");
                Check(c["reason"] is string && ((string)c["reason"]).Length > 0, "能力项 reason 应非空: " + Show(c["cap"]));
                names.Add((string)c["cap"]);
            }
            Check(names.Contains("payload") && names.Contains("autoUsing") && names.Contains("harmony") && names.Contains("channels"),
                "能力矩阵应含 payload/autoUsing/harmony/channels，实际 " + string.Join(",", names));

            // capabilities.channels.ready 必须有区分度：= 是否开了非 GUI 通道。
            var capsFalse = (List<Dictionary<string, object>>)StatusCommand.Compose(
                new StatusCommand.StatusEnvironment { AgentChannelOpen = false }).Data["capabilities"];
            Check(!(bool)CapabilityIn(capsFalse, "channels")["ready"], "没有非 GUI 通道时 channels.ready 应为 false");
            Check((bool)CapabilityIn(caps, "channels")["ready"], "有非 GUI 通道时 channels.ready 应为 true");

            List<Dictionary<string, object>> channels = (List<Dictionary<string, object>>)r.Data["channels"];
            Check(channels.Count == 2, "通道矩阵应两条，实际 " + channels.Count);
            foreach (Dictionary<string, object> ch in channels)
                Check(ch.ContainsKey("channel") && ch.ContainsKey("available") && ch.ContainsKey("reason"),
                    "通道项应含 {channel,available,reason}");

            // 探针不通过 → ok=false，且绝不多出 stallHint 之类的冻结字段。
            SlashResult bad = StatusCommand.Compose(new StatusCommand.StatusEnvironment
            {
                Gate = StatusCommand.ParseGateStatus("loaded=True;count=8"),
                ProbeA = new Dictionary<string, object> { ["positive"] = true, ["negative"] = false }
            });
            Check((bool)bad.Data["ok"] == false, "探针未通过时 ok 应为 false");
            Check(!bad.Data.ContainsKey("stallHint"), "探针失败路径也不得有 stallHint");
        }

        private static void ComposeLineCases()
        {
            SlashResult r = StatusCommand.Compose(GoodEnvironment());
            Check(r.Lines.Count > 0, "Lines 不得为空");
            foreach (string l in r.Lines)
            {
                Check(!string.IsNullOrEmpty(l), "行不得为空");
                Check(l.StartsWith("[", StringComparison.Ordinal), "行应以 '[' 开头，实际 " + Show(l));
                Check(l.IndexOf('\n') < 0 && l.IndexOf('\r') < 0, "行应为单行（无换行），实际 " + Show(l));
            }

            string joined = string.Join("\n", r.Lines);
            foreach (string group in new[] { "[阶段]", "[执行器]", "[载荷]", "[探针]", "[能力]", "[提示]" })
                Check(joined.Contains(group), "Lines 应含分组 " + group);

            Check(joined.Contains("stage=main_menu"), "阶段行应带 stage");
            Check(joined.Contains("count=8（期望 8；权威值 = gate.count）"),
                "M1：载荷行应为 count=N（期望 N）+ 点名权威值，实际行=" + Show(LineStarting(r.Lines, "[载荷] loaded=")));
            Check(joined.Contains("probeA positive=true negative=true"), "探针行应带正负结果");
            Check(joined.Contains("通道 gui available=true"), "通道矩阵应逐条进能力组");
            Check(joined.Contains("冻结/停滞判定归客户端"), "应有一行说明冻结判定归客户端（替代已删的 stallHint）");

            // 采样失败只进 [提示] 行，不得改变 Data 语义
            var env = GoodEnvironment();
            env.Failures.Add("Find.Maps: NullReferenceException: boom");
            SlashResult withFail = StatusCommand.Compose(env);
            Check(string.Join("\n", withFail.Lines).Contains("采样失败 1 项"), "失败点名应出现在 [提示] 行");
            Check((string)withFail.Data["stage"] == "main_menu", "点名不改变 Data 的 stage");
        }

        private static void UnsampledCases()
        {
            // 未采样（marshal 不可用/超时/poisoned）→ stage 与四个布尔必须为 null，**不是** stopped/false。
            var env = new StatusCommand.StatusEnvironment
            {
                GameStateSampled = false,
                GameStateNote = StatusCommand.NoChannelNote,
                AgentChannelOpen = true,
                Channels = StatusCommand.ChannelMatrix(
                    new StatusCommand.ChannelFlags { Installed = true, GuiOpen = true, PipeOpen = true, PipeName = "s" }, out _)
            };
            SlashResult r = StatusCommand.Compose(env);

            Check(r.Data["stage"] == null, "未采样时 stage 应为 null，实际 " + Show(r.Data["stage"]));
            Check(r.Data["inGame"] == null, "未采样时 inGame 应为 null");
            Check(r.Data["loading"] == null, "未采样时 loading 应为 null");
            Check(r.Data["mainMenuReady"] == null, "未采样时 mainMenuReady 应为 null");
            Check(r.Data["paused"] == null, "未采样时 paused 应为 null");
            Check(!r.Data.ContainsKey("programState"),
                "brief 的 Data 键里没有 programState（它只出现在人类行）；不得私自加顶层键");
            Check(!r.Data.ContainsKey("stallHint"), "未采样路径也不得有 stallHint");

            string joined = string.Join("\n", r.Lines);
            Check(joined.Contains("stage=(未采样)"), "阶段行应显式写 (未采样)，实际 " + Show(LineStarting(r.Lines, "[阶段] stage=")));
            Check(joined.Contains("inGame=? loading=? mainMenuReady=? paused=?"), "四个布尔应显式写 ?（不是 false）");
            Check(joined.Contains("[阶段] 未采样:"), "应有一行说明为什么没有");
            Check(joined.Contains("Executor 未接线"), "说明里应点名原因");
        }

        private static void InPlaceSampleCases()
        {
            var env = new StatusCommand.StatusEnvironment();
            bool threw = false;
            try { StatusCommand.SampleGameState(env); }
            catch (Exception ex) { threw = true; Console.WriteLine("  NOTE SampleGameState 离线不可用: " + ex.GetType().Name); }
            if (threw) return;   // 离线 JIT 不可用 → NOTE 跳过（真机/集成环境覆盖）

            Check(env.Stage == null || env.Stage == "stopped" || env.Stage == "starting"
                || env.Stage == "main_menu" || env.Stage == "in_game",
                "stage 只能是四值或 null，实际 " + Show(env.Stage));
            Check(env.MapCount == null || env.MapCount.Value >= 0, "Maps 应 null 或非负");

            // 不变量：单项失败 → 该字段必须是 null（未知），绝不能是 false（谎报）
            FieldNullIfFailed(env, "GenScene.InEntryScene", env.InEntry.HasValue);
            FieldNullIfFailed(env, "GenScene.InPlayScene", env.InPlay.HasValue);
            FieldNullIfFailed(env, "PlayDataLoader.Loaded", env.PlayDataLoaded.HasValue);
            FieldNullIfFailed(env, "LongEventHandler.ShouldWaitForEvent", env.Loading.HasValue);
            FieldNullIfFailed(env, "Current.ProgramState", env.ProgramState != null);
            FieldNullIfFailed(env, "Find.Maps", env.MapCount.HasValue);
            FieldNullIfFailed(env, "Current.Game.tickManager.Paused", env.Paused.HasValue);

            // Stage 只在四件事实都到手时才有值
            bool allFacts = env.Loading.HasValue && env.PlayDataLoaded.HasValue && env.InEntry.HasValue && env.InPlay.HasValue;
            Check(allFacts == (env.Stage != null), "stage 应有值恰好当四件事实都到手（allFacts=" + allFacts + "）");
        }

        private static void DegradeCases()
        {
            // 非游戏线程（stdio）+ 无 Executor → 走 RouteNoChannel：**绝不裸读**，游戏状态置 null 并说明。
            var ctx = new SlashContext { Channel = "stdio" };
            StatusCommand.StatusEnvironment env = null;
            bool threw = false;
            try { env = StatusCommand.Sample(ctx); }
            catch (Exception ex) { threw = true; Console.WriteLine("  NOTE Sample 离线不可用: " + ex.GetType().Name); }
            if (threw) return;

            Check(!env.GameStateSampled, "无 Executor 时不应声称采样成功");
            Check(!string.IsNullOrEmpty(env.GameStateNote), "应说明为什么没有游戏状态");
            Check(env.Stage == null, "降级时 stage 必须为 null（不是 stopped）");
            Check(env.InGame == null && env.Loading == null && env.MainMenuReady == null && env.Paused == null,
                "降级时四个布尔必须为 null（不是 false）");
            Check(env.ProgramState == null && env.MapCount == null, "降级时 programState/mapCount 也必须为 null");
            Check(HasFailure(env.Failures, "游戏状态采样失败"), "失败原因应进 Failures 点名");

            SlashResult r = StatusCommand.Compose(env);
            Check(r.Data["stage"] == null && r.Data["paused"] == null, "Compose 应原样带出 null");
            Check(!r.Data.ContainsKey("stallHint"), "降级路径也不得有 stallHint");
        }

        private static void PartialFactCases()
        {
            // 就地采样「部分成功」：GameStateSampled=true，但 PlayDataLoaded 读失败 → Stage=null。
            // 修 Low 前这里会渲染成 stage=(未采样) 却又 Entry=true（自相矛盾）。
            var env = new StatusCommand.StatusEnvironment
            {
                GameStateSampled = true,
                Stage = null,
                Loading = false,
                PlayDataLoaded = null,
                InEntry = true,
                InPlay = false,
                ProgramState = "Entry",
                MapCount = 0,
                Paused = false,
                InGame = false,
                MainMenuReady = null,
                AgentChannelOpen = true
            };
            SlashResult r = StatusCommand.Compose(env);

            Check(r.Data["stage"] == null, "部分失败时 stage 仍应为 null（未知）");
            string stageLine = LineStarting(r.Lines, "[阶段] stage=");
            Check(stageLine.Contains("stage=(读失败)"), "部分失败时 stage 应渲染 (读失败)，实际 " + Show(stageLine));
            Check(!stageLine.Contains("(未采样)"), "部分失败时不得说 (未采样)，实际 " + Show(stageLine));
            string factsLine = LineStarting(r.Lines, "[阶段] 场景 Entry=");
            Check(factsLine.Contains("Entry=true"), "同组事实行仍在（Entry=true），实际 " + Show(factsLine));

            string joined = string.Join("\n", r.Lines);
            Check(joined.Contains("[阶段] 已采样但部分事实读失败: PlayDataLoaded"),
                "应有一行列出缺失事实，实际 " + Show(LineStarting(r.Lines, "[阶段] 已采样但部分事实读失败")));
            Check(!joined.Contains("[阶段] 未采样:"), "已采样路径不得再出现「未采样」说明行");

            // 对照：真未采样（GameStateSampled=false）→ (未采样) + 原因行，且不出现缺失名单行
            SlashResult u = StatusCommand.Compose(new StatusCommand.StatusEnvironment
            {
                GameStateSampled = false,
                GameStateNote = StatusCommand.NoChannelNote
            });
            Check(LineStarting(u.Lines, "[阶段] stage=").Contains("stage=(未采样)"), "未采样应渲染 (未采样)");
            Check(string.Join("\n", u.Lines).Contains("[阶段] 未采样: "), "未采样应有原因行");
            Check(!string.Join("\n", u.Lines).Contains("已采样但部分事实读失败"), "未采样不得出现缺失名单行");

            // 三态里 stage 有值时不出现任何降级说明
            SlashResult v = StatusCommand.Compose(GoodEnvironment());
            Check(LineStarting(v.Lines, "[阶段] stage=").Contains("stage=main_menu"), "正常路径 stage 有值");
            Check(!string.Join("\n", v.Lines).Contains("未采样"), "正常路径不应有未采样说明");
        }

        private static void MissingFactsCases()
        {
            Check(StatusCommand.MissingFacts(null) == "", "null 环境回空串");

            var all = new StatusCommand.StatusEnvironment
            {
                Loading = false,
                PlayDataLoaded = true,
                InEntry = true,
                InPlay = false,
                ProgramState = "Entry",
                MapCount = 0,
                Paused = false
            };
            Check(StatusCommand.MissingFacts(all) == "", "事实齐全时名单应为空");

            var partial = new StatusCommand.StatusEnvironment
            {
                Loading = false,
                PlayDataLoaded = true,
                InEntry = null,
                InPlay = null,
                ProgramState = "Entry",
                MapCount = null,
                Paused = false
            };
            string missing = StatusCommand.MissingFacts(partial);
            Check(missing.Contains("InEntry") && missing.Contains("InPlay") && missing.Contains("MapCount"),
                "名单应含真正缺失的事实，实际 " + Show(missing));
            Check(!missing.Contains("Loading") && !missing.Contains("Paused"),
                "已到手的事实不得进名单，实际 " + Show(missing));
        }

        private static void CtxFactRouteCases()
        {
            // 契约 v1.1：判据只认 ctx.IsGameThread。stdio 通道 + IsGameThread=true → 就地采（不需要 Executor）；
            // gui 通道 + IsGameThread=false + 无 Executor → 降级置 null（**不**因 Channel=="gui" 就裸读）。
            // 两块**各自独立守卫**（threwInline / threwDegraded）：共用同一个 threw 时，第一块抛异常会把
            // 第二块整块跳过 —— 而第二块正是「因通道名裸读」的直接探测器，跳过就变成静默失效的空门
            // （harness 只记 NOTE、不计失败）。独立守卫保证第一块怎么红都不影响第二块给出判定。
            bool threwInline = false;
            StatusCommand.StatusEnvironment inline = null;
            try { inline = StatusCommand.Sample(new SlashContext { Channel = "stdio", IsGameThread = true }); }
            catch (Exception ex) { threwInline = true; Console.WriteLine("  NOTE Sample(IsGameThread=true) 离线不可用: " + ex.GetType().Name); }
            if (!threwInline)
                Check(inline.GameStateSampled, "IsGameThread=true 应走就地采样（不需要 Executor）");

            bool threwDegraded = false;
            StatusCommand.StatusEnvironment degraded = null;
            try { degraded = StatusCommand.Sample(new SlashContext { Channel = "gui", IsGameThread = false }); }
            catch (Exception ex) { threwDegraded = true; Console.WriteLine("  NOTE Sample(gui,IsGameThread=false) 离线不可用: " + ex.GetType().Name); }
            if (!threwDegraded)
            {
                Check(!degraded.GameStateSampled, "Channel=gui 但 IsGameThread=false → 不得声称就地采样");
                Check(degraded.Stage == null && degraded.InGame == null && degraded.Paused == null,
                    "此时游戏状态应为 null（不裸读、不假值）");
                Check(!string.IsNullOrEmpty(degraded.GameStateNote), "应说明为什么没有");
            }
        }

        private static void ProvideSmokeCases()
        {
            // 最弱接线：无 Host / 无 Executor / stdio 通道 —— 不得抛，Data 键齐全，游戏状态为 null。
            SlashContext ctx = new SlashContext { Channel = "stdio" };
            SlashResult r = null;
            bool threw = false;
            try { r = StatusCommand.Provide("", ctx); }
            catch (Exception ex) { threw = true; Console.WriteLine("  NOTE Provide 离线不可用: " + ex.GetType().Name); }
            if (threw) return;

            Check(r != null && r.Kind == "status", "Provide 应回 Kind=status");
            foreach (string k in new[]
            {
                "stage", "inGame", "loading", "mainMenuReady", "paused", "ticks", "revision", "pending",
                "poisoned", "consoleLog", "gate", "probeA", "capabilities", "channels", "ok", "retrievedAt", "channel"
            })
                Check(r.Data.ContainsKey(k), "Provide 的 Data 缺键: " + k);
            Check(r.Data["stage"] == null, "无 Executor 的 Provide 应给 stage=null");
            Check(!r.Data.ContainsKey("stallHint"), "Provide 路径也不得有 stallHint");
            Check(r.Lines.Count > 0, "Provide 应给出人类行");
        }

        // ---------------- 辅助 ----------------

        private static void FieldNullIfFailed(StatusCommand.StatusEnvironment env, string needle, bool hasValue)
        {
            if (!HasFailure(env.Failures, needle)) return;
            Check(!hasValue, needle + " 读失败时该字段必须是 null（不能是假值）");
        }

        private static bool HasFailure(List<string> failures, string needle)
        {
            foreach (string f in failures)
                if (f != null && f.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static Dictionary<string, object> CapabilityOf(string cap, StatusCommand.StatusEnvironment env)
        {
            if (string.IsNullOrEmpty(env.Channel)) env.Channel = "pipe";
            SlashResult r = StatusCommand.Compose(env);
            return CapabilityIn((List<Dictionary<string, object>>)r.Data["capabilities"], cap);
        }

        private static Dictionary<string, object> CapabilityIn(List<Dictionary<string, object>> caps, string cap)
        {
            foreach (Dictionary<string, object> c in caps)
                if ((string)c["cap"] == cap) return c;
            return null;
        }

        private static string LineStarting(List<string> lines, string prefix)
        {
            foreach (string l in lines)
                if (l.StartsWith(prefix, StringComparison.Ordinal)) return l;
            return "(缺)";
        }

        private static StatusCommand.StatusEnvironment GoodEnvironment()
        {
            bool agent;
            return new StatusCommand.StatusEnvironment
            {
                GameStateSampled = true,
                Stage = "main_menu",
                InEntry = true,
                InPlay = false,
                PlayDataLoaded = true,
                Loading = false,
                MainMenuReady = true,
                Paused = false,
                InGame = false,
                ProgramState = "Entry",
                MapCount = 0,
                Ticks = 1234,
                Revision = 2,
                Pending = 1,
                Poisoned = false,
                ConsoleLogCount = 7,
                Channel = "pipe",
                Channels = StatusCommand.ChannelMatrix(
                    new StatusCommand.ChannelFlags { Installed = true, GuiOpen = true, PipeOpen = true, PipeName = "sess" }, out agent),
                AgentChannelOpen = agent,
                HarmonyState = "loaded",
                HarmonyDetail = "在",
                Gate = StatusCommand.ParseGateStatus(
                    "loaded=True;count=8;failure=;diag=;scripting=Microsoft.CodeAnalysis.Scripting=已装载(前置);evalThread=42"),
                ProbeA = new Dictionary<string, object>
                {
                    ["positive"] = true,
                    ["negative"] = true,
                    ["details"] = "positive: typeof(Verse.Pawn) OK; negative: ghost correctly unresolved;"
                }
            };
        }

        private static void Case(string name, Action body)
        {
            currentCase = name;
            try { body(); }
            catch (Exception ex)
            {
                checks++;
                Failures.Add("[" + name + "] 用例本身抛出: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Check(bool condition, string message)
        {
            checks++;
            if (!condition) Failures.Add("[" + currentCase + "] " + message);
        }

        private static string Show(object o) { return o == null ? "<null>" : "\"" + o + "\""; }
    }
}
