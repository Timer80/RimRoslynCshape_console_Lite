// RoslynProbe —— /ui open（表内 verb ui-open；两段式别名项为 /ui）的纯逻辑单测。
//
// 零依赖（不用 xunit：SlashHarness.csproj 必须保持零 PackageReference，见 tests/SlashHarness/README.md）：
// public static int Run() 返回失败数，由 tests/SlashHarness/SlashRegistryTests.cs 的 SlashTestHarness 反射发现。
//
// 覆盖：RouteFor（就地 / marshal / 明确拒绝三分支）/ MarshalFailureReason（超时、session poisoned、
//   Wait 异常、泛化）/ Build（opened × alreadyOpen 四种组合 + 失败点名）/ Failure 形状 / UsageResult /
//   登记元数据 + 姿势 B 接线。
// **不覆盖** ApplyOpen 与 Provide 的就地/marshal 路径：它们要碰 ConsoleLayer → Unity/Verse，只能在游戏里跑
//   （真机侧:docs/boundary-suite/t6b/REPORT-T6b.md §2.5 在最终树上实测 /ui open 与 /ui-open 双通;
//   两段式别名与表内 verb 的等价另见 s11-8/REPORT-specials.md §8）；正因如此，路由判定与 marshal 失败原因都被抽成了可离线测的纯函数。
// 刻意覆盖"非游戏线程 + 无 Executor → 明确拒绝"（舰队规则第三分支）：证明它给出诚实的 opened=false，
//   且**从不直调** ConsoleLayer.Open()。
// ⚠️ 但拒绝路**会读一次** ConsoleLayer.IsOpen（W10-fix1 / d28e668 起，为 alreadyOpen 取真值；纯静态标量读，
//   与 Commands.DoStatus 的既有口径一致）—— 所以本文件**不再是"完全不碰 ConsoleLayer"**：离线运行时需要
//   能解析 UnityEngine（UnityStubs.dll 放 exe 旁 —— 官方跑手自带载荷，见 tests/SlashHarness/README.md）。

using System;
using System.Collections.Generic;
using System.Reflection;
using RoslynProbe;
using RoslynProbe.Repl;

namespace RoslynProbe.Tests
{
    public static class UiCommandTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";
        private static int checks;

        public static int Run()
        {
            Failures.Clear();
            checks = 0;
            currentCase = "(none)";
            SlashRegistry.ResetForTests();   // 隔离：避免和别的写手的登记撞名

            Case("RouteFor：IsGameThread 就地 / 有 Executor marshal / 都没有明确拒绝", RouteCases);
            Case("MarshalFailureReason：超时 / session poisoned / Wait 异常 / 泛化", ReasonCases);
            Case("Build：opened × alreadyOpen 四种组合（opened 取调用后真值）", BuildCases);
            Case("Failure：ok=false + error + opened=false（不谎报 true）", FailureCases);
            Case("Provide：接受 open / 未知子命令给用法页 / 非 gui 无 Executor 明确拒绝", ProvideCases);
            Case("登记 + 姿势 B 接线：verb/Usage/Dangerous + handler 经 ctx.CallUiOpen", RegisterCases);

            // 收尾：还原成"只含真实登记"的状态，避免影响同进程其它写手的测试。
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();

            Console.WriteLine("UiCommandTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- 用例 ----------------

        private static void RouteCases()
        {
            Check(UiCommand.RouteFor(true, false) == UiCommand.RouteInline, "IsGameThread=true → 就地（只认事实位）");
            Check(UiCommand.RouteFor(true, true) == UiCommand.RouteInline, "IsGameThread=true 即使有 Executor 也就地");
            Check(UiCommand.RouteFor(false, true) == UiCommand.RouteMarshal, "非游戏线程 + Executor → marshal");
            Check(UiCommand.RouteFor(false, false) == UiCommand.RouteNoChannel, "非游戏线程 + 无 Executor → 明确拒绝");
        }

        private static void ReasonCases()
        {
            string timedOut = UiCommand.MarshalFailureReason(new ReplResult { TimedOut = true }, null);
            Check(timedOut.IndexOf("超时", StringComparison.Ordinal) >= 0, "TimedOut 应给超时文案");
            Check(timedOut.IndexOf("不谎报", StringComparison.Ordinal) >= 0, "超时文案应声明不谎报 opened=true");

            string poisoned = UiCommand.MarshalFailureReason(new ReplResult { Ok = false, Error = "session poisoned" }, null);
            Check(poisoned.IndexOf("session poisoned", StringComparison.Ordinal) >= 0,
                "outcome.Error 应原样带出（吞掉就只剩泛化），实际 " + Show(poisoned));

            string waited = UiCommand.MarshalFailureReason(null, "AggregateException: boom");
            Check(waited.IndexOf("boom", StringComparison.Ordinal) >= 0, "Wait 抛出的异常应原样带出");

            string generic = UiCommand.MarshalFailureReason(null, null);
            Check(generic.IndexOf("打开控制台失败", StringComparison.Ordinal) == 0, "兜底应是泛化失败文案");
        }

        private static void BuildCases()
        {
            // 新开：调用前关、调用后开。
            SlashResult r = UiCommand.Build(new UiCommand.OpenOutcome { AlreadyOpen = false, Opened = true });
            Check(r != null && r.Kind == "ui-open", "Kind 应为 ui-open");
            Check(r.Data != null && r.Data.Count == 2, "Data 恰好两键 {opened,alreadyOpen}，实际 " + (r.Data == null ? 0 : r.Data.Count));
            Check((bool)r.Data["opened"] == true && (bool)r.Data["alreadyOpen"] == false, "新开：opened=true / alreadyOpen=false");
            Check(r.Lines.Count == 1 && r.Lines[0].IndexOf("已打开", StringComparison.Ordinal) >= 0, "新开应给已打开一行");

            // 幂等：调用前就开着 → opened 仍 true、alreadyOpen 也 true。
            SlashResult r2 = UiCommand.Build(new UiCommand.OpenOutcome { AlreadyOpen = true, Opened = true });
            Check((bool)r2.Data["opened"] == true && (bool)r2.Data["alreadyOpen"] == true, "幂等：两键都应 true");
            Check(r2.Lines[0].IndexOf("幂等", StringComparison.Ordinal) >= 0, "幂等行应点名幂等");

            // 失败：Open() 没抛出但窗口没打开（未接线 / WindowStack.Add 被吞）→ 点名原因 + opened=false。
            SlashResult r3 = UiCommand.Build(new UiCommand.OpenOutcome { AlreadyOpen = false, Opened = false });
            Check(r3.Kind == "ui-open", "失败态 Kind 仍是 ui-open");
            Check((bool)r3.Data["opened"] == false, "失败态 opened 必须 false（绝不谎报）");
            Check((bool)r3.Data["alreadyOpen"] == false, "失败态 alreadyOpen 取调用前真值");
            Check((bool)r3.Data["ok"] == false, "失败态 ok 应为 false");
            string err3 = (string)r3.Data["error"];
            Check(err3.IndexOf("ConsoleLayer.Wire", StringComparison.Ordinal) >= 0, "未接线原因应点名 ConsoleLayer.Wire");
            Check(err3.IndexOf("false", StringComparison.Ordinal) >= 0, "原因应说明 IsOpen 仍为 false");

            // 失败：Open() 抛异常 → Error 原文带出。
            SlashResult r4 = UiCommand.Build(new UiCommand.OpenOutcome { Opened = false, Error = "NullReferenceException: boom" });
            Check(((string)r4.Data["error"]).IndexOf("boom", StringComparison.Ordinal) >= 0, "异常原文应带出");

            SlashResult r5 = UiCommand.Build(null);
            Check((bool)r5.Data["opened"] == false, "null outcome 也必须 opened=false");
            Check(r5.Data["alreadyOpen"] == null, "null outcome 的 alreadyOpen 必须是 null（未知），不能填 false");
        }

        private static void FailureCases()
        {
            SlashResult r = UiCommand.Failure("测试原因", false);
            Check(r != null && r.Kind == "ui-open", "Failure Kind 应为 ui-open");
            Check((bool)r.Data["ok"] == false, "Failure ok 应为 false");
            Check((string)r.Data["error"] == "测试原因", "Failure error 应为原文");
            Check((bool)r.Data["opened"] == false, "Failure opened 必须 false");
            Check((bool)r.Data["alreadyOpen"] == false, "Failure alreadyOpen 应原样带上");
            Check(r.Lines.Count == 1 && r.Lines[0] == "测试原因", "Failure 应只有一行错误行");

            // bool? 的三种取值都要能原样带出：true / false / null（未知绝不填假 false）。
            SlashResult rt = UiCommand.Failure("测试原因", true);
            Check((bool)rt.Data["alreadyOpen"] == true, "Failure alreadyOpen=true 应原样带上");
            SlashResult rn = UiCommand.Failure("测试原因", null);
            Check(rn.Data != null && rn.Data.ContainsKey("alreadyOpen") && rn.Data["alreadyOpen"] == null,
                "Failure 未知态 alreadyOpen 必须是 null（不填假 false）");

            // 真值读取器：纯静态标量读。**本断言的前提是运行器能解析 UnityEngine**（ConsoleLayer 派生自
            // MonoBehaviour ⇒ 需 UnityStubs.dll 在 exe 旁；官方跑手自带载荷，见 tests/SlashHarness/README.md）。
            // 该前提下离线也应读到值（不得抛成 null）；若运行器缺引擎 DLL，这一条会失败 —— 那是**环境**前提
            // 不满足，不是本指令的行为问题（官方运行器下 61/61）。
            Check(UiCommand.ReadIsOpenBestEffort().HasValue,
                "ReadIsOpenBestEffort 应读到值（前提：运行器可解析 UnityEngine / UnityStubs.dll 在 exe 旁）");
        }

        private static void ProvideCases()
        {
            // 未知子命令 → 用法页（在路由之前，故不碰 ConsoleLayer / Executor）。
            SlashResult bad = UiCommand.Provide("close", new SlashContext { IsGameThread = true });
            Check(bad != null && bad.Data != null && (bool)bad.Data["usage"] == true, "未知子命令应给用法页");
            Check(bad.Lines.Count >= 2 && bad.Lines[1].IndexOf("close", StringComparison.Ordinal) >= 0, "用法页应回显未知子命令");

            // 用法页形状。
            SlashResult usage = UiCommand.UsageResult(null);
            Check((bool)usage.Data["ok"] == false && (bool)usage.Data["usage"] == true, "用法页应为 ok=false/usage=true");
            Check(usage.Lines[0].IndexOf("用法: /ui open", StringComparison.Ordinal) == 0, "首行应是 /ui open 用法");

            // 非游戏线程 + 无 Executor → 明确拒绝（**不碰** Unity），opened=false 且说明为什么。
            SlashResult refused = UiCommand.Provide("", new SlashContext { IsGameThread = false });
            Check(refused != null && refused.Kind == "ui-open", "拒绝时 Kind 仍是 ui-open");
            Check((bool)refused.Data["opened"] == false, "拒绝时 opened 必须 false");
            Check((bool)refused.Data["ok"] == false, "拒绝时 ok 应为 false");
            string why = (string)refused.Data["error"];
            Check(why.IndexOf("Executor", StringComparison.Ordinal) >= 0, "拒绝原因应点名 Executor 未接线");
            Check(why.IndexOf("未定义行为", StringComparison.Ordinal) >= 0, "拒绝原因应说明读线程上碰 Unity 的后果");

            // "open" 被接受（走同一条拒绝分支即证明它没落到用法页）。
            SlashResult refused2 = UiCommand.Provide("OPEN", new SlashContext { IsGameThread = false });
            Check(refused2 != null && (bool)refused2.Data["ok"] == false, "\"OPEN\" 应被接受为子命令（进路由后拒绝）");
            Check(!refused2.Data.ContainsKey("usage"), "\"OPEN\" 不应落到用法页");
        }

        private static void RegisterCases()
        {
            SlashRegistry.ResetForTests();
            UiCommand.Register();

            SlashEntry e;
            Check(SlashRegistry.TryGet("ui-open", out e), "应登记 verb ui-open");
            Check(e != null && e.Verb == "ui-open", "verb 应规范化为 ui-open（小写）");
            Check(e != null && e.Kind == "ui-open", "Kind 应为 ui-open");
            Check(e != null && e.Dangerous, "开窗会改界面状态，Dangerous 应为 true");
            Check(e != null && e.Handler != null, "Handler 不应为 null");

            string argsPart, note;
            SlashRegistry.SplitUsage(e.Usage, out argsPart, out note);
            // F5：本项无参数规格（之前把别名子动词 "open" 当参数段，渲染出 "/ui-open open"）；
            // 两段式别名由别名项 /ui 的那行 "  /ui open —— …" 提供可发现性。
            Check(argsPart == "", "Usage 参数段应为空（无参数规格），实际 " + Show(argsPart));
            Check(argsPart.IndexOf('/') < 0, "Usage 参数段不得含 '/'，实际 " + Show(argsPart));
            Check(note.Length > 0 && note.IndexOf("打开", StringComparison.Ordinal) >= 0, "Usage 说明段应说明开关窗，实际 " + Show(note));

            MethodInfo provide = typeof(UiCommand).GetMethod(
                "Provide", BindingFlags.Public | BindingFlags.Static, null,
                new Type[] { typeof(string), typeof(SlashContext) }, null);
            Check(provide != null, "必须暴露 public static Provide(string rest, SlashContext ctx)");
            Check(provide != null && provide.ReturnType == typeof(SlashResult), "Provide 必须返回 SlashResult");

            string seenRest = null;
            SlashContext seenCtx = null;
            var wired = new SlashContext
            {
                Channel = "test",
                UiOpen = delegate (string a, SlashContext c) { seenRest = a; seenCtx = c; return SlashResults.Ok("ui-open"); }
            };
            SlashResult r;
            Check(SlashRegistry.TryDispatch("/ui-open open", wired, out r), "命中 /ui-open 应返回 true");
            Check(r != null && r.Kind == "ui-open", "结果应来自注入的 provider");
            Check(seenRest == "open", "rest 应原样交给 ctx.CallUiOpen，实际 " + Show(seenRest));
            Check(object.ReferenceEquals(seenCtx, wired), "ctx 应原样透传");
            Check(wired.RegisteredCapabilities().Contains("ui-open"), "注入后能力表应含 ui-open");

            SlashResult miss;
            Check(SlashRegistry.TryDispatch("/ui-open", new SlashContext(), out miss), "未注入时仍应返回 true（吞下）");
            Check(miss != null && miss.Kind == "missing-capability", "未注入应回 missing-capability，实际 " + (miss == null ? "<null>" : miss.Kind));
            Check(miss != null && (string)miss.Data["capability"] == "ui-open", "应点名能力 ui-open");
        }

        // ---------------- 断言 ----------------

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

        private static string Show(string s) { return s == null ? "<null>" : "\"" + s + "\""; }
    }
}
