// RoslynProbe —— Slash 表驱动内核的纯逻辑单测。
//
// 为什么不用 xunit：本文件由 **零 PackageReference** 的 SlashHarness.csproj 编入
// （命令行必须能 -restore:false 直编，见 tests/SlashHarness/README.md）。所以这里用零依赖的自带断言：
// 每个测试类暴露 <c>public static int Run()</c>（返回失败数），由 SlashTestHarness 反射统一调用。
//
// 覆盖（对照 T-S0 步骤 5 的三条要求）：
//   1. 表内 verb 唯一（重名 Register 立刻抛）
//   2. 未知指令响亮拒绝且不抛（TryDispatch 返回 true + Kind="unknown"）
//   3. help 行与 Data **同源**一致（同一张表、同一顺序、同一 verb 集合）

using System;
using System.Collections.Generic;
using System.Reflection;
using RoslynProbe;
using RoslynProbe.Repl;

namespace RoslynProbe.Tests
{
    /// <summary>零依赖测试套件入口 + 极简断言。</summary>
    public static class SlashRegistryTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";
        private static int checks;

        public static int Run()
        {
            Failures.Clear();
            checks = 0;
            currentCase = "(none)";

            Case("IsCommand 判定与现有 SlashCommands.IsCommand 等价", IsCommandCases);
            Case("分派：非指令返回 false", NonCommandCases);
            Case("分派：命中指令（verb 大小写不敏感 / rest Trim / ctx 透传）", DispatchHitCases);
            Case("分派：未知指令响亮拒绝且不抛", UnknownCases);
            Case("分派：handler 抛异常 → 结果行，不逃逸", HandlerThrowCases);
            Case("分派：handler 为 null → NotImplemented（stub 可只登记）", HandlerNullCases);
            Case("登记：重名立刻抛（表内 verb 唯一）", DuplicateRegisterCases);
            Case("登记：非法 verb/Kind 立刻抛 + 规范化", RegisterValidationCases);
            Case("help：Lines 与 Data 同源一致（构造表）", delegate { CheckHelpSameSource(); });
            Case("help：SlashResults.Help() 就是同源实现体", HelpHelperCases);
            Case("SlashResults 各便捷构造的形状", SlashResultsCases);
            Case("SlashContext：未注册能力返回'能力未注册'行，绝不抛", CapabilityMissingCases);
            Case("SlashContext：IsGameThread 线程事实位（契约 v1.1）", IsGameThreadCases);
            Case("SlashContext：已注册能力透传 args/ctx，异常与 null 都收敛成错误行", CapabilityRegisteredCases);
            Case("RegisterAll 幂等（真实表）", RegisterAllCases);
            Case("真实表不变量 + help 同源（RegisterAll 之后）", new Action(delegate
            {
                CheckTableInvariants();
                CheckHelpSameSource();
            }));

            // 收尾：把表还原成"只含真实登记"的状态，避免影响同进程其它写手的测试。
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();

            Console.WriteLine("SlashRegistryTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- 用例 ----------------

        private static void IsCommandCases()
        {
            Check(SlashRegistry.IsCommand("/x"), "IsCommand(\"/x\") 应为 true");
            Check(SlashRegistry.IsCommand("  /x"), "IsCommand(\"  /x\") 应为 true（TrimStart）");
            Check(SlashRegistry.IsCommand("/"), "IsCommand(\"/\") 应为 true");
            Check(!SlashRegistry.IsCommand("x"), "IsCommand(\"x\") 应为 false");
            Check(!SlashRegistry.IsCommand(""), "IsCommand(\"\") 应为 false");
            Check(!SlashRegistry.IsCommand(null), "IsCommand(null) 应为 false");
            Check(!SlashRegistry.IsCommand("  "), "IsCommand(\"  \") 应为 false");
        }

        private static void NonCommandCases()
        {
            SlashResult r;
            Check(!SlashRegistry.TryDispatch("1+1", null, out r), "非指令应返回 false");
            Check(r == null, "非指令的 result 应为 null");
            Check(!SlashRegistry.TryDispatch("", null, out r), "空串应返回 false");
            Check(!SlashRegistry.TryDispatch("   ", null, out r), "空白串应返回 false");
            Check(!SlashRegistry.TryDispatch(null, null, out r), "null 应返回 false");
        }

        private static void DispatchHitCases()
        {
            SlashRegistry.ResetForTests();
            string seenArgs = null;
            SlashContext seenCtx = null;
            SlashRegistry.Register(new SlashEntry
            {
                Verb = "t-echo",
                Usage = "[text] —— 回显",
                Kind = "t-echo",
                Handler = delegate(string args, SlashContext c) { seenArgs = args; seenCtx = c; return SlashResults.Ok("t-echo"); }
            });

            var ctx = new SlashContext { Channel = "test" };
            SlashResult r;
            Check(SlashRegistry.TryDispatch("/t-echo hello world", ctx, out r), "命中指令应返回 true");
            Check(r != null && r.Kind == "t-echo", "Kind 应取登记项的 Kind");
            Check(seenArgs == "hello world", "rest 应为 \"hello world\"，实际 " + Show(seenArgs));
            Check(object.ReferenceEquals(seenCtx, ctx), "ctx 应原样透传");

            Check(SlashRegistry.TryDispatch("/T-ECHO x", ctx, out r), "verb 大小写不敏感");
            Check(seenArgs == "x", "大写 verb 的 rest 应为 \"x\"，实际 " + Show(seenArgs));

            Check(SlashRegistry.TryDispatch("   /t-echo    a  b   ", ctx, out r), "前导空白 + 多空格");
            Check(seenArgs == "a  b", "rest 应 Trim 两端、保留内部空格，实际 " + Show(seenArgs));

            Check(SlashRegistry.TryDispatch("/t-echo", ctx, out r), "无参数调用");
            Check(seenArgs == "", "无参数时 rest 应为空串，实际 " + Show(seenArgs));

            SlashRegistry.TryDispatch("/t-echo y", null, out r);
            Check(seenCtx != null, "ctx 传 null 时应降级为空上下文而不是 null");
        }

        private static void UnknownCases()
        {
            SlashRegistry.ResetForTests();
            SlashResult r;
            Check(SlashRegistry.TryDispatch("/nosuch a b", new SlashContext(), out r), "未知指令仍应返回 true（吞下，不放行给编译器）");
            Check(r != null && r.Kind == "unknown", "未知指令 Kind 应为 unknown");
            Check(r != null && r.Data != null && (bool)r.Data["ok"] == false, "Data[\"ok\"] 应为 false");
            Check(r != null && r.Data != null && (string)r.Data["unknown"] == "nosuch", "Data[\"unknown\"] 应为 verb");
            Check(r != null && r.Lines.Count == 1 && r.Lines[0].IndexOf("未知指令: /nosuch", StringComparison.Ordinal) == 0,
                "首行应以 \"未知指令: /nosuch\" 开头，实际 " + Show(r == null ? null : r.Lines[0]));

            Check(SlashRegistry.TryDispatch("/", new SlashContext(), out r), "裸 \"/\" 应返回 true 且不被当成命令");
            Check(r != null && r.Kind == "unknown" && (string)r.Data["unknown"] == "", "裸 \"/\" 的 verb 为空串");

            // 未知指令不得抛（含奇怪字符）
            SlashRegistry.TryDispatch("/?!*", new SlashContext(), out r);
            Check(r != null && r.Kind == "unknown", "奇怪字符的未知指令也不得抛");
        }

        private static void HandlerThrowCases()
        {
            SlashRegistry.ResetForTests();
            SlashRegistry.Register(new SlashEntry
            {
                Verb = "t-boom",
                Kind = "boom",
                Handler = delegate(string a, SlashContext c) { throw new InvalidOperationException("炸了"); }
            });
            SlashResult r;
            bool threw = false;
            try { SlashRegistry.TryDispatch("/t-boom", new SlashContext(), out r); }
            catch { threw = true; r = null; }
            Check(!threw, "handler 异常不得逃逸出 TryDispatch");
            Check(r != null && r.Kind == "boom", "异常结果应沿用该指令的 Kind");
            Check(r != null && r.Data != null && (bool)r.Data["ok"] == false, "异常结果的 Data[\"ok\"] 应为 false");
            Check(r != null && r.Lines.Count == 1 && r.Lines[0].IndexOf("InvalidOperationException", StringComparison.Ordinal) >= 0,
                "异常结果应点名异常类型，实际 " + Show(r == null || r.Lines.Count == 0 ? null : r.Lines[0]));
        }

        private static void HandlerNullCases()
        {
            SlashRegistry.ResetForTests();
            SlashRegistry.Register(new SlashEntry { Verb = "t-stub", Kind = "stub", Usage = "—— 待实现" });
            SlashResult r;
            Check(SlashRegistry.TryDispatch("/t-stub", new SlashContext(), out r), "stub 指令应返回 true");
            Check(r != null && r.Kind == "stub", "stub 的 Kind 应为登记项 Kind");
            Check(r != null && r.Data != null && (bool)r.Data["implemented"] == false, "stub 的 Data[\"implemented\"] 应为 false");
            Check(r != null && r.Data != null && (string)r.Data["verb"] == "t-stub", "stub 的 Data[\"verb\"] 应为 verb");
        }

        private static void DuplicateRegisterCases()
        {
            SlashRegistry.ResetForTests();
            SlashRegistry.Register(new SlashEntry { Verb = "t-dup", Kind = "t-dup" });
            CheckThrows<InvalidOperationException>(
                delegate { SlashRegistry.Register(new SlashEntry { Verb = "t-dup", Kind = "t-dup" }); },
                "重名 verb 必须立刻抛 InvalidOperationException");
            CheckThrows<InvalidOperationException>(
                delegate { SlashRegistry.Register(new SlashEntry { Verb = "T-DUP", Kind = "t-dup" }); },
                "重名判定必须大小写不敏感");
            Check(SlashRegistry.Count == 1, "重名抛之后表里仍应只有 1 条，实际 " + SlashRegistry.Count);

            var verbs = new HashSet<string>(StringComparer.Ordinal);
            foreach (SlashEntry e in SlashRegistry.Snapshot())
                Check(verbs.Add(e.Verb), "表内 verb 重复: " + e.Verb);
        }

        private static void RegisterValidationCases()
        {
            SlashRegistry.ResetForTests();
            CheckThrows<ArgumentNullException>(delegate { SlashRegistry.Register(null); }, "Register(null) 必须抛");
            CheckThrows<ArgumentException>(delegate { SlashRegistry.Register(new SlashEntry { Verb = "" }); }, "空 verb 必须抛");
            CheckThrows<ArgumentException>(delegate { SlashRegistry.Register(new SlashEntry { Verb = "   " }); }, "全空白 verb 必须抛");
            CheckThrows<ArgumentException>(delegate { SlashRegistry.Register(new SlashEntry { Verb = "a b" }); }, "含空格 verb 必须抛");
            CheckThrows<ArgumentException>(delegate { SlashRegistry.Register(new SlashEntry { Verb = "a/b" }); }, "含 '/' verb 必须抛");
            CheckThrows<ArgumentException>(delegate { SlashRegistry.Register(new SlashEntry { Verb = "a", Kind = "bad kind" }); }, "含空格 Kind 必须抛");

            SlashRegistry.Register(new SlashEntry { Verb = "  T-Case  ", Usage = null, Group = null });
            SlashEntry e;
            Check(SlashRegistry.TryGet("t-case", out e), "verb 应被 Trim + 小写化后可按小写查到");
            Check(SlashRegistry.TryGet("T-CASE", out e), "查询大小写不敏感");
            Check(e != null && e.Verb == "t-case", "登记项的 Verb 应被规范化为小写");
            Check(e != null && e.Kind == "t-case", "Kind 为空时应默认取 verb");
            Check(e != null && e.Usage == "", "Usage 为 null 时应规范化为空串");
            Check(e != null && e.Group == "", "Group 为 null 时应规范化为空串");
            Check(!SlashRegistry.TryGet("nope", out e) && e == null, "查不到的 verb 应返回 false + null");
            Check(SlashRegistry.Count == 1, "只应登记 1 条");
        }

        private static void HelpHelperCases()
        {
            SlashRegistry.ResetForTests();
            SlashRegistry.Register(new SlashEntry { Verb = "t-h", Kind = "t-h", Usage = "—— 帮助测试" });
            SlashResult r = SlashResults.Help();
            Check(r.Kind == "help", "SlashResults.Help() 的 Kind 应为 help");
            string[] lines = SlashRegistry.HelpLines();
            Check(r.Lines.Count == lines.Length, "Help() 的 Lines 应等于 HelpLines()");
            for (int i = 0; i < lines.Length && i < r.Lines.Count; i++)
                Check(r.Lines[i] == lines[i], "Help() 第 " + i + " 行应与 HelpLines() 一致");
            Check(r.Data != null && (int)r.Data["count"] == 1, "Help() 的 Data 应等于 HelpData()");
        }

        private static void SlashResultsCases()
        {
            SlashResult u = SlashResults.Unknown("zz");
            Check(u.Kind == "unknown" && u.Lines.Count == 1 && (string)u.Data["unknown"] == "zz", "Unknown 形状");
            Check(u.Lines[0] == "未知指令: /zz —— 拒绝执行（不会当作 C# 编译）。可用指令见 /help",
                "Unknown 行必须与现有 SlashCommands.Unknown 逐字一致，实际 " + Show(u.Lines[0]));

            SlashResult n = SlashResults.NotImplemented("dev", "dev");
            Check(n.Kind == "dev" && (bool)n.Data["implemented"] == false && (string)n.Data["verb"] == "dev", "NotImplemented 形状");

            SlashResult m = SlashResults.CapabilityMissing("hook");
            Check(m.Kind == "missing-capability" && (string)m.Data["capability"] == "hook", "CapabilityMissing 形状");

            SlashResult er = SlashResults.Error("gc", "boom");
            Check(er.Kind == "gc" && (bool)er.Data["ok"] == false && (string)er.Data["error"] == "boom" && er.Lines[0] == "boom", "Error 形状");
            Check(SlashResults.Error("gc", null).Lines[0] == "未知错误", "Error 的空消息应有兜底文案");

            SlashResult ok = SlashResults.Ok("gc");
            Check(ok.Kind == "gc" && (bool)ok.Data["ok"] == true && ok.Lines.Count == 0, "Ok 形状");

            SlashResult us = SlashResults.Usage("gc", "用法: /gc", null, "");
            Check(us.Lines.Count == 1 && us.Lines[0] == "用法: /gc" && (bool)us.Data["usage"] == true, "Usage 形状（跳过空行）");
        }

        private static void IsGameThreadCases()
        {
            // 契约 v1.1：普通 bool 事实位，零 Verse/Unity 依赖；默认 false = 拿不准就 marshal。
            var ctx = new SlashContext();
            Check(!ctx.IsGameThread, "IsGameThread 默认必须是 false（宁可多一次 marshal，也不要裸读 Unity/Verse）");

            ctx.IsGameThread = true;
            Check(ctx.IsGameThread, "IsGameThread 应是可读写的普通 bool 字段");

            // 与能力注入无关：未注入任何 provider 时它照样准确。
            Check(ctx.RegisteredCapabilities().Count == 0 && ctx.IsGameThread,
                "IsGameThread 与姿势 B 能力注入相互独立");

            var fresh = new SlashContext { Channel = "gui", IsGameThread = true };
            Check(fresh.IsGameThread && fresh.Channel == "gui", "对象初始化器可同时设 Channel 与 IsGameThread");

            // 分派时 ctx 原样透传，provider 读到的就是壳层填的那个值。
            SlashRegistry.ResetForTests();
            bool seen = false;
            SlashRegistry.Register(new SlashEntry
            {
                Verb = "t-thr",
                Kind = "t-thr",
                Handler = delegate(string a, SlashContext c) { seen = c.IsGameThread; return SlashResults.Ok("t-thr"); }
            });
            SlashResult r;
            SlashRegistry.TryDispatch("/t-thr", new SlashContext { IsGameThread = true }, out r);
            Check(seen, "handler 应读到壳层填的 IsGameThread=true");
            seen = true;
            SlashRegistry.TryDispatch("/t-thr", new SlashContext(), out r);
            Check(!seen, "默认上下文里 handler 应读到 false");
        }

        private static void CapabilityMissingCases()
        {
            var ctx = new SlashContext();
            Check(ctx.RegisteredCapabilities().Count == 0, "空上下文不应报出任何已注册能力");
            Check(ctx.Status == null && ctx.Log == null && ctx.Dev == null && ctx.DebugAction == null
                && ctx.Hook == null && ctx.Prof == null && ctx.UsingAdd == null && ctx.UiOpen == null,
                "空上下文 8 个能力全为 null");

            SlashResult[] all = new[]
            {
                ctx.CallStatus("a"), ctx.CallLog("a"), ctx.CallDev("a"), ctx.CallDebugAction("a"),
                ctx.CallHook("a"), ctx.CallProf("a"), ctx.CallUsingAdd("a"), ctx.CallUiOpen("a")
            };
            string[] names = { "status", "log", "dev", "debug-action", "hook", "prof", "usings-add", "ui-open" };
            for (int i = 0; i < all.Length; i++)
            {
                SlashResult r = all[i];
                Check(r != null && r.Kind == "missing-capability", names[i] + " 未注册时应返回 missing-capability");
                Check(r != null && r.Lines.Count == 1 && r.Lines[0].IndexOf("能力未注册", StringComparison.Ordinal) == 0,
                    names[i] + " 首行应为'能力未注册'行，实际 " + Show(r == null || r.Lines.Count == 0 ? null : r.Lines[0]));
                Check(r != null && (string)r.Data["capability"] == names[i], names[i] + " 的 Data[\"capability\"] 应点名能力");
            }
        }

        private static void CapabilityRegisteredCases()
        {
            string seenArgs = null;
            SlashContext seenCtx = null;
            var ctx = new SlashContext
            {
                Dev = delegate(string a, SlashContext c) { seenArgs = a; seenCtx = c; return SlashResults.Ok("dev"); },
                Hook = delegate(string a, SlashContext c) { throw new ApplicationException("hook 炸了"); },
                Prof = delegate(string a, SlashContext c) { return null; }
            };

            List<string> caps = ctx.RegisteredCapabilities();
            Check(caps.Count == 3 && caps.Contains("dev") && caps.Contains("hook") && caps.Contains("prof"),
                "RegisteredCapabilities 应报出 dev/hook/prof，实际 " + string.Join(",", caps));

            SlashResult r = ctx.CallDev("rest-1");
            Check(r.Kind == "dev" && seenArgs == "rest-1" && object.ReferenceEquals(seenCtx, ctx), "CallDev 应透传 args 与 ctx");
            Check(ctx.CallDev(null).Kind == "dev", "CallDev(null) 不得抛（args 兜底为空串）");
            Check(seenArgs == "", "args 为 null 时应传空串，实际 " + Show(seenArgs));

            SlashResult h = ctx.CallHook("x");
            Check(h.Kind == "capability-error" && (bool)h.Data["ok"] == false
                && h.Lines[0].IndexOf("ApplicationException", StringComparison.Ordinal) >= 0,
                "provider 抛异常应收敛成 capability-error 行，实际 " + Show(h.Lines.Count == 0 ? null : h.Lines[0]));

            SlashResult p = ctx.CallProf("x");
            Check(p.Kind == "capability-error", "provider 返回 null 应收敛成 capability-error");
        }

        private static void RegisterAllCases()
        {
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();
            int first = SlashRegistry.Count;
            SlashRegistry.RegisterAll();   // 幂等
            Check(SlashRegistry.Count == first, "RegisterAll 应幂等，实际 " + first + " → " + SlashRegistry.Count);
        }

        // ---------------- 不变量 ----------------

        private static void CheckTableInvariants()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (SlashEntry e in SlashRegistry.Snapshot())
            {
                Check(seen.Add(e.Verb), "真实表内 verb 重复: " + e.Verb);
                Check(e.Verb == e.Verb.Trim().ToLowerInvariant(), "verb 必须 Trim+小写: " + e.Verb);
                Check(e.Verb.IndexOf('/') < 0 && e.Verb.IndexOf(' ') < 0, "verb 不得含 '/' 或空格: " + e.Verb);
                Check(e.Kind != null && e.Kind.Length > 0, "Kind 不得为空: " + e.Verb);
                for (int i = 0; i < e.Kind.Length; i++)
                    Check(!char.IsWhiteSpace(e.Kind[i]), "Kind 不得含空白: " + e.Kind);
                Check(e.Usage != null && e.Group != null, "Usage/Group 不得为 null: " + e.Verb);
            }

            // 排序：(Group, Verb) 升序
            List<SlashEntry> snap = SlashRegistry.Snapshot();
            for (int i = 1; i < snap.Count; i++)
            {
                int cmp = string.CompareOrdinal(snap[i - 1].Group, snap[i].Group);
                bool ok = cmp < 0 || (cmp == 0 && string.CompareOrdinal(snap[i - 1].Verb, snap[i].Verb) < 0);
                Check(ok, "Snapshot 排序错误: " + snap[i - 1].Group + "/" + snap[i - 1].Verb + " 在 "
                    + snap[i].Group + "/" + snap[i].Verb + " 之前");
            }
        }

        /// <summary>
        /// T-S0 要求 3：help 行与 Data 同源一致 —— 同序、同数、同 verb 集合、字段可回溯。
        /// </summary>
        private static void CheckHelpSameSource()
        {
            List<SlashEntry> snap = SlashRegistry.Snapshot();
            string[] lines = SlashRegistry.HelpLines();
            Dictionary<string, object> data = SlashRegistry.HelpData();

            Check(lines.Length == snap.Count + 2, "HelpLines 条数应为 表条数+2(首尾)，实际 " + lines.Length + " vs " + snap.Count);
            Check(lines[0] == "可用指令:", "HelpLines 首行应为 \"可用指令:\"，实际 " + Show(lines[0]));
            Check(lines[lines.Length - 1] == "未知指令一律报错，不会当作 C# 编译。", "HelpLines 尾行固定文案");

            Check((int)data["count"] == snap.Count, "HelpData[\"count\"] 应等于表条数");
            List<string> verbs = (List<string>)data["verbs"];
            List<Dictionary<string, object>> commands = (List<Dictionary<string, object>>)data["commands"];
            Check(verbs.Count == snap.Count, "HelpData[\"verbs\"] 条数应等于表条数");
            Check(commands.Count == snap.Count, "HelpData[\"commands\"] 条数应等于表条数");

            for (int i = 0; i < snap.Count; i++)
            {
                SlashEntry e = snap[i];
                string expectVerb = "/" + e.Verb;

                Check(verbs[i] == expectVerb, "第 " + i + " 个 verb 顺序应与 Snapshot 一致: " + verbs[i] + " vs " + expectVerb);

                Dictionary<string, object> c = commands[i];
                Check((string)c["verb"] == expectVerb, "commands[" + i + "][\"verb\"] 应与 verbs 一致");
                Check((string)c["kind"] == e.Kind, "commands[" + i + "][\"kind\"] 应回溯到表");
                Check((string)c["group"] == e.Group, "commands[" + i + "][\"group\"] 应回溯到表");
                Check((bool)c["dangerous"] == e.Dangerous, "commands[" + i + "][\"dangerous\"] 应回溯到表");
                Check((bool)c["implemented"] == (e.Handler != null), "commands[" + i + "][\"implemented\"] 应回溯到表");
                Check((string)c["usage"] == e.Usage, "commands[" + i + "][\"usage\"] 应逐字等于登记项");

                // 行 ↔ Data：第 i+1 行必须落在第 i 条指令上（同源同序）
                string line = lines[i + 1];
                Check(line.StartsWith("  " + expectVerb, StringComparison.Ordinal),
                    "HelpLines 第 " + (i + 1) + " 行应以 \"  " + expectVerb + "\" 开头，实际 " + Show(line));

                string argsPart, note;
                SlashRegistry.SplitUsage(e.Usage, out argsPart, out note);
                Check((string)c["args"] == argsPart && (string)c["note"] == note, "commands[" + i + "] 的 args/note 应等于 Usage 拆分");
                if (argsPart.Length > 0)
                    Check(line.StartsWith("  " + expectVerb + " " + argsPart, StringComparison.Ordinal),
                        "行内应含参数段: " + Show(line));
                if (note.Length > 0)
                    Check(line.EndsWith("—— " + note, StringComparison.Ordinal), "行尾应含说明段: " + Show(line));
            }
        }

        // ---------------- 断言/发现器 ----------------

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

        private static void CheckThrows<T>(Action body, string message) where T : Exception
        {
            checks++;
            try
            {
                body();
                Failures.Add("[" + currentCase + "] " + message + "（实际未抛）");
            }
            catch (T) { }
            catch (Exception ex)
            {
                Failures.Add("[" + currentCase + "] " + message + "（实际抛了 " + ex.GetType().Name + "）");
            }
        }

        private static string Show(string s) { return s == null ? "<null>" : "\"" + s + "\""; }
    }

    /// <summary>
    /// 零依赖测试发现器：反射调用本程序集 namespace RoslynProbe.Tests 下所有
    /// <c>public static int Run()</c>（返回失败数），按类型全名序执行。
    /// 新增测试类只需在自己的 <c>tests/SlashHarness/&lt;Name&gt;Tests.cs</c> 里加一个这样的 Run()。
    /// </summary>
    public static class SlashTestHarness
    {
        public static int RunAll()
        {
            var methods = new List<MethodInfo>();
            foreach (Type t in typeof(SlashTestHarness).Assembly.GetTypes())
            {
                if (t == null || t == typeof(SlashTestHarness)) continue;
                if (t.FullName == null || !t.FullName.StartsWith("RoslynProbe.Tests.", StringComparison.Ordinal)) continue;
                MethodInfo m = t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m == null || m.ReturnType != typeof(int)) continue;
                methods.Add(m);
            }
            methods.Sort((a, b) => string.CompareOrdinal(a.DeclaringType.FullName, b.DeclaringType.FullName));

            int totalFailures = 0;
            foreach (MethodInfo m in methods)
            {
                int f = (int)m.Invoke(null, null);
                totalFailures += f;
            }
            Console.WriteLine("SlashTestHarness: 共 " + methods.Count + " 个测试类，失败 " + totalFailures);
            return totalFailures;
        }
    }
}
