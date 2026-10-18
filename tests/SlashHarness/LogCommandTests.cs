// RoslynProbe —— /log 的纯逻辑单测（含审查修复的回归项）。
//
// 零依赖（不用 xunit：SlashHarness.csproj 必须保持零 PackageReference，见 tests/SlashHarness/README.md）：
// public static int Run() 返回失败数，由 tests/SlashHarness/SlashRegistryTests.cs 的 SlashTestHarness 反射发现。
//
// 覆盖范围：ParseArgs / Matches / Prefix / BuildResult（Lines + Data 形状 + 图例开关）/
//   RouteFor / MarshalFailureReason / LegendFor / 登记元数据 / **姿势 B 接线**（经
//   ctx.CallLog 的注入式派发 —— 注入假 provider，于是无需游戏即可证明 handler 不是直读 Verse）/
//   **C1 线程判据收口**（契约 v1.1 §3.4.1：只认事实位 ctx.IsGameThread，不看 Channel、
//   不查 UnityData —— 用例按"Channel 撒谎"构造，把判据改回启发式立刻变红）。
// **不覆盖** Provide 本体与 Capture()：它们要碰 UnityData / Verse.Log，只能在游戏里跑
// （/log 的真机 Provide/Capture 路径未在 docs/boundary-suite/ 的验收报告里单列小节）；正因如此，路由判定与 marshal 失败原因都被抽成了可离线测的纯函数。

using System;
using System.Collections.Generic;
using System.Reflection;
using RoslynProbe;
using RoslynProbe.Repl;

namespace RoslynProbe.Tests
{
    public static class LogCommandTests
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

            Case("ParseArgs：默认 n=50、无 filter", DefaultCases);
            Case("ParseArgs：n / n+filter / 纯 filter", ParseCases);
            Case("ParseArgs：clear 与非法 n", ClearAndBadNCases);
            Case("BuildResult：取尾部 n 条（时间序）+ 大小写不敏感子串过滤", SelectCases);
            Case("BuildResult：Data 形状 {count,total,filter,entries[type,text,stack]}", DataCases);
            Case("BuildResult：Lines 汇总行与 [E]/[W]/[M] 前缀 + 空日志/无命中文案", LineCases);
            Case("修复轮1 M1：Matches 先判 null（空 filter 也不放行 null 记录）", NullRecordCases);
            Case("修复轮1 M4：图例开关（pipe 不给图例行）", LegendCases);
            Case("修复轮1 M2/M3 + 修复轮3 C1：RouteFor 三分支 / 判据只认 ctx.IsGameThread / marshal 失败原因带出 session poisoned", RouteAndReasonCases);
            Case("登记 + 姿势 B 接线：Provide 签名 / handler 经 ctx.CallLog / 未注入回 missing-capability", RegisterCases);

            // 收尾：还原成"只含真实登记"的状态，避免影响同进程其它写手的测试。
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();

            Console.WriteLine("LogCommandTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- 用例 ----------------

        private static void DefaultCases()
        {
            LogCommand.Query q = LogCommand.ParseArgs("");
            Check(q != null && !q.Clear && q.Error == null, "空 rest 应是读请求");
            Check(q.N == LogCommand.DefaultN, "空 rest 的 n 应为 DefaultN，实际 " + q.N);
            Check(q.Filter == "", "空 rest 的 filter 应为空串");

            LogCommand.Query q2 = LogCommand.ParseArgs(null);
            Check(q2.N == LogCommand.DefaultN && q2.Filter == "", "rest 为 null 时等价于空 rest");

            LogCommand.Query q3 = LogCommand.ParseArgs("   ");
            Check(q3.N == LogCommand.DefaultN && q3.Filter == "", "全空白 rest 时等价于空 rest");
        }

        private static void ParseCases()
        {
            LogCommand.Query a = LogCommand.ParseArgs("200");
            Check(a.N == 200 && a.Filter == "", "\"200\" → n=200 且无 filter，实际 n=" + a.N + " filter=" + Show(a.Filter));

            LogCommand.Query b = LogCommand.ParseArgs("50 Roslyn");
            Check(b.N == 50 && b.Filter == "Roslyn", "\"50 Roslyn\" → n=50 + filter=Roslyn，实际 " + b.N + "/" + Show(b.Filter));

            LogCommand.Query c = LogCommand.ParseArgs("Roslyn");
            Check(c.N == LogCommand.DefaultN && c.Filter == "Roslyn",
                "纯 filter（无 n）应保留默认 n，实际 " + c.N + "/" + Show(c.Filter));

            LogCommand.Query d = LogCommand.ParseArgs("Some error text");
            Check(d.N == LogCommand.DefaultN && d.Filter == "Some error text",
                "多词 filter 应整体保留，实际 " + Show(d.Filter));

            LogCommand.Query e = LogCommand.ParseArgs("  50   Roslyn   ");
            Check(e.N == 50 && e.Filter == "Roslyn", "两端空白应被吃掉，实际 " + e.N + "/" + Show(e.Filter));

            LogCommand.Query f = LogCommand.ParseArgs("+7 x");
            Check(f.N == 7 && f.Filter == "x", "带符号整数也应识别为 n，实际 " + f.N + "/" + Show(f.Filter));

            LogCommand.Query g = LogCommand.ParseArgs("1e3");
            Check(g.N == LogCommand.DefaultN && g.Filter == "1e3", "非整数首词应整体当 filter，实际 " + Show(g.Filter));

            // R1b/F1 同族：分隔空白 = **任意空白**（TAB / 全角空格 U+3000 / NBSP U+00A0），不是只认 ASCII 空格。
            // 退化成 IndexOf(' ') 时，"50<TAB>Roslyn" 会整体当 filter（n 不生效）⇒ 下面三条恰好红。
            string[] blanks = { "\t", "\u3000", "\u00A0" };
            string[] blankNames = { "TAB", "全角空格(U+3000)", "NBSP(U+00A0)" };
            for (int i = 0; i < blanks.Length; i++)
            {
                LogCommand.Query w = LogCommand.ParseArgs("50" + blanks[i] + "Roslyn");
                Check(w.Error == null && w.N == 50 && w.Filter == "Roslyn",
                    blankNames[i] + " 分隔的 \"50 Roslyn\" 应识别出 n=50 + filter=Roslyn，实际 n=" + w.N
                    + " filter=" + Show(w.Filter) + " err=" + Show(w.Error));
            }
            // 反向锚点：首词不是数字时，整段仍是 filter（分割不得越界改变语义）
            Check(LogCommand.ParseArgs("Roslyn\tx").Filter == "Roslyn\tx",
                "首词非数字时整段仍是 filter（不得把 tail 吃掉），实际 " + Show(LogCommand.ParseArgs("Roslyn\tx").Filter));
        }

        private static void ClearAndBadNCases()
        {
            LogCommand.Query cl = LogCommand.ParseArgs("clear");
            Check(cl.Clear, "\"clear\" 应置 Clear");
            Check(LogCommand.ParseArgs("CLEAR").Clear, "clear 判定大小写不敏感");
            Check(!LogCommand.ParseArgs("50 clear").Clear, "只有整段等于 clear 才是清空（\"50 clear\" 是过滤）");

            LogCommand.Query z = LogCommand.ParseArgs("0");
            Check(!z.Clear && z.Error != null, "\"0\" 应是用法错误，实际 " + Show(z.Error));
            LogCommand.Query neg = LogCommand.ParseArgs("-3");
            Check(neg.Error != null, "\"-3\" 应是用法错误，实际 " + Show(neg.Error));
            LogCommand.Query big = LogCommand.ParseArgs("99999999999999999999");
            Check(big.Error != null, "整数溢出应是用法错误，实际 " + Show(big.Error));

            SlashResult u = LogCommand.UsageResult("n 必须是 ≥ 1 的整数（收到 \"0\"）");
            Check(u != null && u.Kind == "log" && u.Data != null && (bool)u.Data["usage"] == true, "用法页 Kind=log + Data.usage=true");
            Check(u.Lines.Count > 0 && u.Lines[0].IndexOf("用法: /log", StringComparison.Ordinal) == 0, "用法页首行应是用法，实际 " + Show(u.Lines.Count == 0 ? null : u.Lines[0]));
            Check(u.Lines.Exists(delegate (string l) { return l.IndexOf("错误:", StringComparison.Ordinal) >= 0; }), "用法页应带上错误行");
        }

        private static void SelectCases()
        {
            List<LogCommand.Record> all = new List<LogCommand.Record>();
            for (int i = 0; i < 100; i++) all.Add(Rec(i % 3 == 0 ? "Error" : "Message", "msg " + i, null));

            LogCommand.Query q = LogCommand.ParseArgs("3");
            SlashResult r = LogCommand.BuildResult(all, q);
            Check((int)r.Data["total"] == 100, "total 应是全量条数（100），实际 " + r.Data["total"]);
            Check((int)r.Data["count"] == 3, "count 应是 3，实际 " + r.Data["count"]);
            List<Dictionary<string, object>> es = (List<Dictionary<string, object>>)r.Data["entries"];
            Check(es.Count == 3, "entries 应有 3 条");
            Check((string)es[0]["text"] == "msg 97" && (string)es[2]["text"] == "msg 99",
                "应取尾部 n 条且保持时间序，实际 " + Show((string)es[0]["text"]) + " … " + Show((string)es[2]["text"]));
            for (int i = 0; i < es.Count; i++)
                Check(string.Equals((string)es[i]["text"], "msg " + (97 + i), StringComparison.Ordinal), "第 " + i + " 条应是尾部顺序");

            LogCommand.Query q2 = LogCommand.ParseArgs("Roslyn");
            Check(LogCommand.BuildResult(all, q2).Data["count"] is int && (int)LogCommand.BuildResult(all, q2).Data["count"] == 0,
                "无命中时 count 应为 0");

            List<LogCommand.Record> mixed = new List<LogCommand.Record>
            {
                Rec("Message", "hello ROSLYN world", null),
                Rec("Error", "roslyn probe failed", null),
                Rec("Warning", "unrelated", null)
            };
            LogCommand.Query q3 = LogCommand.ParseArgs("roslyn");
            SlashResult r3 = LogCommand.BuildResult(mixed, q3);
            Check((int)r3.Data["count"] == 2, "过滤应大小写不敏感命中 2 条，实际 " + r3.Data["count"]);
            Check((string)r3.Data["filter"] == "roslyn", "Data.filter 应回显查询串");

            LogCommand.Query q4 = LogCommand.ParseArgs("10 ROSLYN");
            SlashResult r4 = LogCommand.BuildResult(mixed, q4);
            Check((int)r4.Data["count"] == 2 && (int)r4.Data["total"] == 3, "n>命中数时应回全部命中；total 仍为全量");

            Check(LogCommand.BuildResult(new List<LogCommand.Record>(), LogCommand.ParseArgs("")).Data["count"] is int, "空日志不应抛");
            Check(LogCommand.BuildResult(null, LogCommand.ParseArgs("")).Data["count"] is int, "null 快照不应抛");
            Check(LogCommand.BuildResult(all, null).Data["count"] is int, "null 查询不应抛（按默认口径）");
        }

        private static void DataCases()
        {
            var all = new List<LogCommand.Record>
            {
                Rec("Message", "plain", null),
                Rec("Error", "boom", "at Foo.Bar()\nat Baz.Qux()"),
                Rec("Warning", "careful", "No stack trace.")
            };
            SlashResult r = LogCommand.BuildResult(all, LogCommand.ParseArgs("3"));

            Check(r.Kind == "log", "Kind 应为 log");
            Check(r.Data.Count == 4, "Data 应恰有 4 个键，实际 " + r.Data.Count);
            Check(r.Data.ContainsKey("count") && r.Data.ContainsKey("total")
                && r.Data.ContainsKey("filter") && r.Data.ContainsKey("entries"), "Data 键应为 count/total/filter/entries");
            Check((int)r.Data["count"] == 3 && (int)r.Data["total"] == 3, "count/total 应为 3");
            Check((string)r.Data["filter"] == "", "无过滤时 filter 应为空串");

            List<Dictionary<string, object>> es = (List<Dictionary<string, object>>)r.Data["entries"];
            Check(es.Count == 3, "entries 应有 3 条");
            string[] wantTypes = { "Message", "Error", "Warning" };
            for (int i = 0; i < es.Count; i++)
            {
                Dictionary<string, object> e = es[i];
                Check(e.Count == 3, "第 " + i + " 条 entries 应恰有 3 个键（type/text/stack）");
                Check((string)e["type"] == wantTypes[i], "第 " + i + " 条 type 应为 " + wantTypes[i] + "，实际 " + Show((string)e["type"]));
                Check(e.ContainsKey("text") && e.ContainsKey("stack"), "第 " + i + " 条应含 text/stack");
            }
            Check((string)es[0]["text"] == "plain", "text 应逐字透传");
            Check((string)es[1]["stack"] == "at Foo.Bar()\nat Baz.Qux()", "多行 stack 应逐字透传");
            Check((string)es[2]["stack"] == "No stack trace.", "原版的无栈哨兵串应原样保留（不改写）");

            var one = new LogCommand.Record { Type = "Error", Text = "t", Stack = "s" };
            Check(one.Type == "Error" && one.Text == "t" && one.Stack == "s", "Record 的三个字段应分别是 Type/Text/Stack");
        }

        private static void LineCases()
        {
            var all = new List<LogCommand.Record>
            {
                Rec("Message", "m0", null),
                Rec("Error", "e1", "stack"),
                Rec("Warning", "w2", null)
            };
            SlashResult r = LogCommand.BuildResult(all, LogCommand.ParseArgs(""));
            Check(r.Lines.Count == 5, "1 汇总行 + 1 图例行 + 3 条目行，实际 " + r.Lines.Count);
            Check(r.Lines[0].IndexOf("共 3 条", StringComparison.Ordinal) >= 0, "汇总行应报总条数，实际 " + Show(r.Lines[0]));
            Check(r.Lines[0].IndexOf("显示最近 3 条", StringComparison.Ordinal) >= 0, "汇总行应报显示条数，实际 " + Show(r.Lines[0]));
            Check(r.Lines[2] == "[M] m0", "Message 前缀应为 [M]，实际 " + Show(r.Lines[2]));
            Check(r.Lines[3] == "[E] e1", "Error 前缀应为 [E]，实际 " + Show(r.Lines[3]));
            Check(r.Lines[4] == "[W] w2", "Warning 前缀应为 [W]，实际 " + Show(r.Lines[4]));
            Check(r.Data["entries"] is List<Dictionary<string, object>>, "entries 应是 List<Dictionary<string,object>>（与 HelpData 同款，工具消费方依赖）");

            SlashResult empty = LogCommand.BuildResult(new List<LogCommand.Record>(), LogCommand.ParseArgs(""));
            Check(empty.Lines.Count == 1 && (int)empty.Data["count"] == 0, "空日志应只有汇总行");
            Check(empty.Lines[0].IndexOf("共 0 条", StringComparison.Ordinal) >= 0, "空日志汇总行应报共 0 条，实际 " + Show(empty.Lines[0]));

            SlashResult none = LogCommand.BuildResult(all, LogCommand.ParseArgs("zzz"));
            Check(none.Lines.Count == 1 && none.Lines[0].IndexOf("无匹配", StringComparison.Ordinal) >= 0,
                "无命中应给无匹配文案，实际 " + Show(none.Lines[0]));
            Check(none.Data["entries"] is List<Dictionary<string, object>>, "无命中时 entries 仍是列表");

            Check(LogCommand.Prefix("Error") == "E" && LogCommand.Prefix("Warning") == "W"
                && LogCommand.Prefix("Message") == "M" && LogCommand.Prefix(null) == "M", "Prefix 映射");
            Check(LogCommand.Matches(Rec("Error", "abc", null), ""), "空 filter 应全通过");
            Check(LogCommand.Matches(Rec("Error", "abc", null), "BC"), "filter 应大小写不敏感");
            Check(!LogCommand.Matches(Rec("Error", "abc", null), "zz"), "不命中应为 false");

            SlashResult gap = LogCommand.ClearGap();
            Check(gap.Kind == "log" && (bool)gap.Data["ok"] == false, "缺口回报应是 ok:false 的 log 结果");
            Check((string)gap.Data["gap"] == LogCommand.GapNoClearApi, "缺口应点名 " + LogCommand.GapNoClearApi);
            Check(gap.Lines.Count > 0 && gap.Lines[0].IndexOf("/log clear", StringComparison.Ordinal) >= 0,
                "缺口首行应点名 /log clear，实际 " + Show(gap.Lines.Count == 0 ? null : gap.Lines[0]));
        }

        /// <summary>修复轮 1 / M1：null 记录必须在 Matches 里先被判掉（空 filter 也不例外），否则 Prefix 处 NRE。</summary>
        private static void NullRecordCases()
        {
            Check(!LogCommand.Matches(null, "a"), "Matches(null,\"a\") 应为 false");
            Check(!LogCommand.Matches(null, ""), "M1: Matches(null,\"\") 必须为 false（不得被空 filter 放行）");
            Check(!LogCommand.Matches(null, null), "M1: Matches(null,null) 必须为 false");

            var all = new List<LogCommand.Record>
            {
                Rec("Message", "a", null),
                null,
                Rec("Error", "b", "s")
            };
            SlashResult r = LogCommand.BuildResult(all, LogCommand.ParseArgs(""));
            Check((int)r.Data["count"] == 2, "M1: null 记录应被跳过，count 应为 2，实际 " + r.Data["count"]);
            Check((int)r.Data["total"] == 3, "total 仍是快照条数（含 null 位）");
            Check(r.Lines.Count == 1 + 1 + 2, "M1: 汇总 + 图例 + 2 条，实际 " + r.Lines.Count);
            Check(r.Lines[2] == "[M] a" && r.Lines[3] == "[E] b", "M1: 不得 NRE 且顺序保持");

            List<Dictionary<string, object>> es = (List<Dictionary<string, object>>)r.Data["entries"];
            for (int i = 0; i < es.Count; i++)
                Check(es[i]["type"] != null, "entries 不应出现 null 记录");
        }

        /// <summary>修复轮 1 / M4：前缀图例只在非 pipe 通道输出。</summary>
        private static void LegendCases()
        {
            Check(LogCommand.LegendFor("pipe") == false, "M4: pipe 不给图例");
            Check(LogCommand.LegendFor("PIPE") == false, "M4: pipe 判定大小写不敏感");
            Check(LogCommand.LegendFor("gui"), "M4: gui 保留图例");
            Check(LogCommand.LegendFor("stdio"), "M4: stdio 保留图例");
            Check(LogCommand.LegendFor(null), "M4: 通道未接线时保留图例");
            Check(LogCommand.LegendFor(""), "M4: 空通道保留图例");

            var all = new List<LogCommand.Record> { Rec("Error", "e", "s") };
            SlashResult withLegend = LogCommand.BuildResult(all, LogCommand.ParseArgs(""), true);
            SlashResult noLegend = LogCommand.BuildResult(all, LogCommand.ParseArgs(""), false);
            Check(withLegend.Lines.Count == 3, "带图例应是 汇总+图例+条目 = 3 行，实际 " + withLegend.Lines.Count);
            Check(noLegend.Lines.Count == 2, "M4: 不带图例应是 汇总+条目 = 2 行，实际 " + noLegend.Lines.Count);
            Check(noLegend.Lines[0].IndexOf("共 1 条", StringComparison.Ordinal) >= 0, "M4: 汇总行必须保留（本来就是给 agent 的计数）");
            Check(noLegend.Lines[1] == "[E] e", "M4: 去掉图例后条目行仍逐条渲染");
            Check((int)noLegend.Data["count"] == 1 && (int)noLegend.Data["total"] == 1, "M4: 图例开关不动 Data");
            Check(LogCommand.BuildResult(all, LogCommand.ParseArgs("")).Lines.Count == 3, "M4: 默认口径仍带图例（gui/stdio）");
        }

        /// <summary>修复轮 1 / M2 + M3：路由三分支与 marshal 失败原因（含 Executor.Poisoned 的 "session poisoned"）。</summary>
        private static void RouteAndReasonCases()
        {
            // M3：非游戏线程 + 无 Executor ⇒ 明确拒绝，绝不裸读。
            Check(LogCommand.RouteFor(true, false) == LogCommand.RouteInline, "M3: 游戏线程 → 就地读（无 Executor 也能读）");
            Check(LogCommand.RouteFor(true, true) == LogCommand.RouteInline, "M3: 游戏线程 → 就地读（有 Executor 也不排队）");
            Check(LogCommand.RouteFor(false, true) == LogCommand.RouteMarshal, "M3: 非游戏线程 + 有 Executor → marshal");
            Check(LogCommand.RouteFor(false, false) == LogCommand.RouteNoChannel, "M3: 非游戏线程 + 无 Executor → 明确拒绝");
            Check(LogCommand.NoChannelMessage.IndexOf("未接线", StringComparison.Ordinal) >= 0
                && LogCommand.NoChannelMessage.IndexOf("ctx.IsGameThread", StringComparison.Ordinal) >= 0,
                "M3/C1: 拒绝文案应点名事实位 ctx.IsGameThread 与「未接线」，实际 " + Show(LogCommand.NoChannelMessage));

            // C1（契约 v1.1 §3.4.1）：线程判据**只认 ctx.IsGameThread** —— 不看 Channel、不查 UnityData。
            // 前两条就是变异负向对照要打的靶（把判据改回 Channel 猜 ⇒ 立刻红）。
            Check(LogCommand.RouteFor(new SlashContext { Channel = "gui", IsGameThread = false }) == LogCommand.RouteNoChannel,
                "C1: Channel=\"gui\" 但 ctx.IsGameThread=false 且无 Executor ⇒ 必须明确拒绝（判据不是 Channel）");
            Check(LogCommand.RouteFor(new SlashContext { Channel = "pipe", IsGameThread = true }) == LogCommand.RouteInline,
                "C1: Channel=\"pipe\" 但 ctx.IsGameThread=true ⇒ 就地（判据不是 Channel）");
            Check(LogCommand.RouteFor(new SlashContext { Channel = "pipe", IsGameThread = false, Executor = new Executor() }) == LogCommand.RouteMarshal,
                "C1: 事实位 false + 有 Executor ⇒ marshal");
            Check(LogCommand.RouteFor(new SlashContext { Channel = "gui", IsGameThread = true, Executor = new Executor() }) == LogCommand.RouteInline,
                "C1: 事实位 true 时即使有 Executor 也就地");
            Check(LogCommand.RouteFor(null) == LogCommand.RouteNoChannel,
                "C1: ctx 为 null ⇒ 按空上下文（IsGameThread=false、Executor=null）明确拒绝，绝不裸读");

            // M2：Executor.Poisoned 时 Tcs 上是 "session poisoned" —— 必须原样带出。
            string poisoned = LogCommand.MarshalFailureReason(
                new ReplResult { Ok = false, Error = "session poisoned" }, null);
            Check(poisoned.IndexOf("session poisoned", StringComparison.Ordinal) >= 0,
                "M2: 必须带出 session poisoned，实际 " + Show(poisoned));

            // handler 抛异常时 Executor.Tick 收敛成的 Error 同样要带出。
            string thrown = LogCommand.MarshalFailureReason(new ReplResult { Ok = false, Error = "NullReferenceException: x" }, null);
            Check(thrown.IndexOf("NullReferenceException", StringComparison.Ordinal) >= 0, "M2: handler 异常原因应带出，实际 " + Show(thrown));

            string timedOut = LogCommand.MarshalFailureReason(new ReplResult { TimedOut = true }, null);
            Check(timedOut.IndexOf("超时", StringComparison.Ordinal) >= 0, "超时应给超时文案，实际 " + Show(timedOut));

            string waitErr = LogCommand.MarshalFailureReason(null, "TaskCanceledException: A task was canceled.");
            Check(waitErr.IndexOf("TaskCanceledException", StringComparison.Ordinal) >= 0, "Wait 抛出的异常应带出，实际 " + Show(waitErr));

            string unknown = LogCommand.MarshalFailureReason(null, null);
            Check(unknown.IndexOf("未取到快照", StringComparison.Ordinal) >= 0, "无原因时给泛化文案，实际 " + Show(unknown));
            Check(LogCommand.MarshalFailureReason(new ReplResult { Ok = true }, null).IndexOf("未取到快照", StringComparison.Ordinal) >= 0,
                "Ok=true 但没有快照时仍应报泛化失败（不得假装成功）");
        }

        private static void RegisterCases()
        {
            LogCommand.Register();   // Run() 开头已 ResetForTests
            SlashEntry e;
            Check(SlashRegistry.TryGet("log", out e), "应登记 verb=log");
            Check(e != null && e.Kind == "log", "Kind 应为 log");
            Check(e != null && e.Handler != null, "Handler 不得为 null（不是 stub）");
            Check(e != null && e.Dangerous == false, "只读指令 Dangerous 应为 false");
            Check(e != null && e.Usage != null && e.Usage.Length > 0, "Usage 不得为空");
            Check(e != null && e.Usage.IndexOf("——", StringComparison.Ordinal) >= 0, "Usage 应含 「——」 分隔符");
            Check(e != null && e.Group != null && e.Group.Length > 0, "Group 不得为空");

            string argsPart, note;
            SlashRegistry.SplitUsage(e.Usage, out argsPart, out note);
            Check(argsPart == "[n] [filter]", "Usage 参数段应为 \"[n] [filter]\"，实际 " + Show(argsPart));
            Check(note.Length > 0, "Usage 应有说明段");

            // I1：provider 必须是 public static SlashResult Provide(string, SlashContext)（Bootstrap 注入点）。
            MethodInfo provide = typeof(LogCommand).GetMethod(
                "Provide", BindingFlags.Public | BindingFlags.Static, null,
                new Type[] { typeof(string), typeof(SlashContext) }, null);
            Check(provide != null, "I1: 必须暴露 public static Provide(string rest, SlashContext ctx)");
            Check(provide != null && provide.ReturnType == typeof(SlashResult), "I1: Provide 必须返回 SlashResult");
            Check(provide != null && provide.IsStatic, "I1: Provide 必须是静态（Bootstrap 直接取方法组）");

            // I1：handler 必须走姿势 B 委托 —— 注入假 provider，派发时它必须被调到（无需游戏）。
            string seenRest = null;
            SlashContext seenCtx = null;
            var wired = new SlashContext
            {
                Channel = "test",
                Log = delegate (string a, SlashContext c) { seenRest = a; seenCtx = c; return SlashResults.Ok("log"); }
            };
            SlashResult r;
            Check(SlashRegistry.TryDispatch("/log 200 Roslyn", wired, out r), "命中 /log 应返回 true");
            Check(r != null && r.Kind == "log", "结果应来自注入的 provider");
            Check(seenRest == "200 Roslyn", "I1: rest 应原样交给 ctx.CallLog，实际 " + Show(seenRest));
            Check(object.ReferenceEquals(seenCtx, wired), "I1: ctx 应原样透传");
            Check(wired.RegisteredCapabilities().Contains("log"), "I1: 注入后能力表应含 log");

            // I1：未注入 → 标准「能力未注册」行（证明不是直读 Verse 的死绑定）。
            SlashResult miss;
            Check(SlashRegistry.TryDispatch("/log", new SlashContext(), out miss), "未注入时仍应返回 true（吞下）");
            Check(miss != null && miss.Kind == "missing-capability", "I1: 未注入应回 missing-capability，实际 " + (miss == null ? "<null>" : miss.Kind));
            Check(miss != null && (string)miss.Data["capability"] == "log", "I1: 应点名能力 log");

            Check(SlashRegistry.Count == 1, "ResetForTests 后应只有本指令 1 条，实际 " + SlashRegistry.Count);
        }

        // ---------------- 断言 ----------------

        private static LogCommand.Record Rec(string type, string text, string stack)
        {
            return new LogCommand.Record { Type = type, Text = text, Stack = stack };
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

        private static string Show(string s) { return s == null ? "<null>" : "\"" + s + "\""; }
    }
}
