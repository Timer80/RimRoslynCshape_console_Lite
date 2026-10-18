// RoslynProbe —— /usings add（表内 verb usings-add；两段式别名项为 /usings）的纯逻辑单测。
//
// 零依赖（不用 xunit：SlashHarness.csproj 必须保持零 PackageReference，见 tests/SlashHarness/README.md）：
// public static int Run() 返回失败数，由 tests/SlashHarness/SlashRegistryTests.cs 的 SlashTestHarness 反射发现。
//
// 覆盖：ParseItems（空白切分、首个词 "add" 防御性跳过、**B1 双引号分段 / 引号内最小转义 / 未闭合引号与空引号
//   报错且不静默丢 / 被引号包住的 "add" 不跳**）/ RejectedData / BuildResult（Data 形状
//   {added,accepted,rejected[item,error]}）/ UsageResult（含 **O-1：参数错误路 Data 必须带 error 原文**）/
//   登记元数据 + 姿势 B 接线（注入假 provider 证明 handler 不是直读 ReplHost、handler 把原样 rest 交给 ctx.CallUsingAdd）。
// **不覆盖** Provide 的成功路径（要 ReplHost → RoslynEngine），只覆盖它不碰引擎的两条分支：
//   空条目 → 用法页；ctx.Host == null → 未接线错误行。真机侧:docs/boundary-suite/s11-8/REPORT-specials.md
//   §8 实测 /usings add 的 Provide 成功路径(该读数测于 b45e570 产物,行为面核验见该文 §12)。

using System;
using System.Collections.Generic;
using System.Reflection;
using RoslynProbe;
using RoslynProbe.Repl;

namespace RoslynProbe.Tests
{
    public static class UsingAddCommandTests
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

            Case("ParseItems：通用切分 + skipFirstWord=\"add\" 的动词跳过（usings-add 自己的规则）", ParseCases);
            Case("RejectedData：{item,error} 形状 + null 条目跳过", RejectedCases);
            Case("BuildResult：Data 恰好三键 {added,accepted,rejected} 且逐条点名", BuildCases);
            Case("UsageResult：空条目给用法页（options: ok=false/usage=true）", UsageCases);
            Case("Provide：空条目与 ctx.Host == null 两条不碰引擎的分支", ProvideCases);
            Case("登记 + 姿势 B 接线：verb/Usage/Dangerous + handler 经 ctx.CallUsingAdd", RegisterCases);

            // 收尾：还原成"只含真实登记"的状态，避免影响同进程其它写手的测试。
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();

            Console.WriteLine("UsingAddCommandTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- 用例 ----------------

        private static void ParseCases()
        {
            Check(UsingAddCommand.ParseItems("").Count == 0, "空 rest → 空条目");
            Check(UsingAddCommand.ParseItems(null).Count == 0, "null rest → 空条目");
            Check(UsingAddCommand.ParseItems("   ").Count == 0, "全空白 rest → 空条目");
            // 通用解析器不含指令语义：不传 skipFirstWord 时 "add" 就是普通条目（usings remove 走的就是这条路）。
            List<string> generic = UsingAddCommand.ParseItems("add");
            Check(generic.Count == 1 && generic[0] == "add", "不传 skipFirstWord → \"add\" 是一个条目（不静默吃掉）");

            string skErr;
            Check(UsingAddCommand.ParseItems("add", out skErr, "add").Count == 0,
                "skipFirstWord=\"add\"（usings-add 形态）→ 只有 \"add\" 时为空条目");

            List<string> a = UsingAddCommand.ParseItems("System.IO");
            Check(a.Count == 1 && a[0] == "System.IO", "裸条目应原样保留");

            List<string> b = UsingAddCommand.ParseItems("add System.IO", out skErr, "add");
            Check(b.Count == 1 && b[0] == "System.IO", "\"add System.IO\" + skipFirstWord=add → 只留 System.IO（反查集成用）");

            List<string> b2 = UsingAddCommand.ParseItems("add System.IO", out skErr);
            Check(b2.Count == 2 && b2[0] == "add" && b2[1] == "System.IO",
                "通用重载（skipFirstWord=null）→ [add, System.IO]，实际 " + b2.Count);

            List<string> c = UsingAddCommand.ParseItems("ADD System.IO System.Text", out skErr, "add");
            Check(c.Count == 2 && c[0] == "System.IO" && c[1] == "System.Text",
                "\"ADD\" 大小写不敏感 + 多条目，实际 " + c.Count);

            List<string> d = UsingAddCommand.ParseItems("System.IO   \t System.Text");
            Check(d.Count == 2, "多空格 / 制表符都当分隔符，实际 " + d.Count);

            List<string> e = UsingAddCommand.ParseItems("using System.IO", out skErr, "add");
            Check(e.Count == 2 && e[0] == "using", "只有首词等于 skipFirstWord 才跳过；\"using\" 不是分隔词");

            // ---- B1：双引号分段 ----
            List<string> f = UsingAddCommand.ParseItems("\"SB = System.Text.StringBuilder\" \"static System.Math\"");
            Check(f.Count == 2 && f[0] == "SB = System.Text.StringBuilder" && f[1] == "static System.Math",
                "B1：引号内空白不是分隔符、还原后不含外层引号，实际 " + f.Count);

            List<string> g = UsingAddCommand.ParseItems("System.IO \"static System.Math\"");
            Check(g.Count == 2 && g[0] == "System.IO" && g[1] == "static System.Math",
                "B1：带引号与不带引号可混用，实际 " + g.Count);

            string skErr2;
            List<string> h = UsingAddCommand.ParseItems("add \"SB = X\"", out skErr2, "add");
            Check(h.Count == 1 && h[0] == "SB = X", "B1：反查形态 add + 引号条目");

            List<string> q1 = UsingAddCommand.ParseItems("\"add\"", out skErr2, "add");
            Check(q1.Count == 1 && q1[0] == "add", "B1：被引号包住的 \"add\" 不该被当动词跳过");

            List<string> q2 = UsingAddCommand.ParseItems("\"a\\\"b\"");
            Check(q2.Count == 1 && q2[0] == "a\"b", "B1：引号内 \\\" 是转义（条目本身含引号）");

            List<string> q3 = UsingAddCommand.ParseItems("\"a\\\\b\"");
            Check(q3.Count == 1 && q3[0] == "a\\b", "B1：引号内 \\\\ 是转义（条目本身含反斜杠）");

            List<string> q4 = UsingAddCommand.ParseItems("\"a\\nb\"");
            Check(q4.Count == 1 && q4[0] == "a\\nb", "B1：其它 \\x 原样保留（不猜）");

            List<string> q5 = UsingAddCommand.ParseItems("  \"System.IO\"   \"System.Text\"  ");
            Check(q5.Count == 2 && q5[0] == "System.IO" && q5[1] == "System.Text", "B1：引号条目两侧空白照旧被吃掉");

            string err1;
            UsingAddCommand.ParseItems("System.IO \"unclosed", out err1);
            Check(!string.IsNullOrEmpty(err1), "B1：未闭合引号必须是用法错误（不猜）");

            string err2;
            UsingAddCommand.ParseItems("\"\"", out err2);
            Check(!string.IsNullOrEmpty(err2), "B1：空引号 \"\" 必须是用法错误（不静默丢）");

            string err3;
            UsingAddCommand.ParseItems("\"   \"", out err3);
            Check(!string.IsNullOrEmpty(err3), "B1：只含空白的引号也是错误");

            string err4;
            List<string> ok4 = UsingAddCommand.ParseItems("System.IO \"static System.Math\"", out err4);
            Check(string.IsNullOrEmpty(err4) && ok4.Count == 2, "B1：合法输入不得报错");
        }

        private static void RejectedCases()
        {
            Check(UsingAddCommand.RejectedData(null).Count == 0, "null 报告 → 空列表");

            var report = new UsingAddReport();
            report.Rejected.Add(new UsingRejection("No.Such.Ns", "无法解析的命名空间: No.Such.Ns（CS0246）"));

            List<Dictionary<string, object>> data = UsingAddCommand.RejectedData(report);
            Check(data.Count == 1, "一条拒绝 → 列表 1 项");
            Check((string)data[0]["item"] == "No.Such.Ns", "键 item 应为被拒条目");
            Check(((string)data[0]["error"]).IndexOf("无法解析的命名空间", StringComparison.Ordinal) == 0,
                "键 error 应为引擎原文，实际 " + Show((string)data[0]["error"]));
            Check(data[0].Count == 2, "每项恰好两键 item/error（与 HTTP rejected[] 1:1），实际 " + data[0].Count);
        }

        private static void BuildCases()
        {
            var report = new UsingAddReport { Added = 1, Accepted = 2 };
            report.Rejected.Add(new UsingRejection("Bad.Ns", "无法解析的命名空间: Bad.Ns（CS0246）"));

            SlashResult r = UsingAddCommand.BuildResult(report, new List<string> { "System.IO", "System.Text", "Bad.Ns" });
            Check(r != null && r.Kind == "usings-add", "Kind 应为 usings-add");
            Check(r.Data != null && r.Data.Count == 3, "Data 恰好三键，实际 " + (r.Data == null ? 0 : r.Data.Count));
            Check((int)r.Data["added"] == 1, "added 应取 report.Added");
            Check((int)r.Data["accepted"] == 2, "accepted 应取 report.Accepted（HTTP 的 count 口径）");
            Check(r.Data["rejected"] is List<Dictionary<string, object>>, "rejected 应是 List<Dictionary>");
            Check(((List<Dictionary<string, object>>)r.Data["rejected"]).Count == 1, "rejected 应带出 1 条");
            Check(r.Lines.Count == 2, "Lines = 汇总行 + 1 条拒绝点名，实际 " + r.Lines.Count);
            Check(r.Lines[0].IndexOf("拒绝 1 条", StringComparison.Ordinal) >= 0, "汇总行应点名拒绝条数");
            Check(r.Lines[1].IndexOf("Bad.Ns", StringComparison.Ordinal) >= 0, "拒绝行应点名条目");

            var allOk = new UsingAddReport { Added = 3, Accepted = 3 };
            SlashResult r2 = UsingAddCommand.BuildResult(allOk, new List<string> { "a", "b", "c" });
            Check(r2.Lines.Count == 1, "无拒绝时只有汇总行");
            Check(r2.Lines[0].IndexOf("拒绝", StringComparison.Ordinal) < 0, "无拒绝时汇总行不提拒绝");

            var dropped = new UsingAddReport();   // accepted == 0 且 rejected == 0：条目被规范化丢弃
            SlashResult r3 = UsingAddCommand.BuildResult(dropped, new List<string> { "using", ";" });
            Check(r3.Lines.Count == 2, "全丢弃时应补一行可读解释");
            Check(r3.Lines[1].IndexOf("2 个条目", StringComparison.Ordinal) >= 0, "解释行应带提交条数");
            Check((int)r3.Data["added"] == 0 && (int)r3.Data["accepted"] == 0, "全丢弃时两个计数都是 0");
        }

        private static void UsageCases()
        {
            SlashResult r = UsingAddCommand.UsageResult();
            Check(r != null && r.Kind == "usings-add", "用法页 Kind 应为 usings-add");
            Check(r.Data != null && (bool)r.Data["usage"] == true, "用法页 Data[usage] 应为 true");
            Check(r.Data != null && (bool)r.Data["ok"] == false, "用法页 Data[ok] 应为 false");
            Check(r.Lines.Count >= 2 && r.Lines[0].IndexOf("用法: /usings add", StringComparison.Ordinal) == 0,
                "首行应是 /usings add 用法，实际 " + Show(r.Lines[0]));

            // B1：带错误文案时错误行置顶；用法页要展示引号形态的例子。
            SlashResult re = UsingAddCommand.UsageResult("引号未闭合");
            Check(re.Lines.Count > 0 && re.Lines[0].IndexOf("错误: 引号未闭合", StringComparison.Ordinal) == 0,
                "带错误文案时首行应是错误行");
            Check(re.Lines.Exists(delegate (string s) { return s.IndexOf("\"SB = System.Text.StringBuilder\"", StringComparison.Ordinal) >= 0; }),
                "用法页应展示引号形态的别名/静态导入例子");

            // O-1：只读 Data 的消费者（--json）必须能直接拿到错误原文，不必解析 Lines。
            Check(re.Data != null && re.Data.ContainsKey("error") && (string)re.Data["error"] == "引号未闭合",
                "O-1：参数错误路 Data 必须带 error 原文，实际 " + (re.Data != null && re.Data.ContainsKey("error") ? Show((string)re.Data["error"]) : "（无该键）"));
            Check(re.Data != null && (bool)re.Data["ok"] == false && (bool)re.Data["usage"] == true,
                "O-1：错误路仍保留 ok=false / usage=true");
            // 空参数（不是错误）→ 不得有 error 键，免得消费者把"要用法页"误判成"参数错"。
            SlashResult ru = UsingAddCommand.UsageResult(null);
            Check(ru.Data != null && !ru.Data.ContainsKey("error"), "空参数用法页不应有 error 键");
        }

        private static void ProvideCases()
        {
            // 空条目 → 用法页：不碰 ctx.Host（ctx 为 null 也不该抛）。
            SlashResult empty = UsingAddCommand.Provide("", null);
            Check(empty != null && empty.Kind == "usings-add", "空条目应回用法页");
            Check(empty.Data != null && !empty.Data.ContainsKey("error"), "空参数经 Provide 也不应有 error 键");

            // O-1 端到端：引号参数错误经 Provide 也要把原因带进 Data，且**不碰** ctx.Host。
            SlashResult badq = UsingAddCommand.Provide("System.IO \"unclosed", null);
            Check(badq != null && badq.Data != null && badq.Data.ContainsKey("error"),
                "O-1：Provide 的引号错误路 Data 必须带 error 原文");
            Check(badq != null && badq.Data != null && (bool)badq.Data["usage"] == true, "O-1：该路仍应是用法页（usage=true）");
            Check(empty.Data != null && (bool)empty.Data["usage"] == true, "空条目应走 Usage 分支");

            SlashResult onlyAdd = UsingAddCommand.Provide("add", new SlashContext());
            Check(onlyAdd.Data != null && (bool)onlyAdd.Data["usage"] == true, "只有 \"add\" 也应是用法页");

            // ctx.Host == null → 未接线错误行（绝不抛、绝不慌报成功）。
            SlashResult noHost = UsingAddCommand.Provide("add System.IO", new SlashContext());
            Check(noHost != null && noHost.Kind == "usings-add", "未接线时 Kind 仍应是 usings-add");
            Check(noHost.Data != null && (bool)noHost.Data["ok"] == false, "未接线时 ok 应为 false");
            Check(((string)noHost.Data["error"]).IndexOf("ctx.Host", StringComparison.Ordinal) >= 0,
                "未接线时应点名 ctx.Host，实际 " + Show((string)noHost.Data["error"]));
            Check(noHost.Lines.Count == 1, "未接线时只有一行错误行");
        }

        private static void RegisterCases()
        {
            SlashRegistry.ResetForTests();
            UsingAddCommand.Register();

            SlashEntry e;
            Check(SlashRegistry.TryGet("usings-add", out e), "应登记 verb usings-add");
            Check(e != null && e.Verb == "usings-add", "verb 应规范化为 usings-add（小写）");
            Check(e != null && e.Kind == "usings-add", "Kind 应为 usings-add");
            Check(e != null && e.Group == "会话", "Group 应为 会话");
            Check(e != null && e.Dangerous, "usings-add 会改会话状态，Dangerous 应为 true");
            Check(e != null && e.Handler != null, "Handler 不应为 null");

            string argsPart, note;
            SlashRegistry.SplitUsage(e.Usage, out argsPart, out note);
            // F5：参数段只放参数规格（不含 verb/斜杠）；两段式别名在说明段里点名。
            Check(argsPart == "<条目> [...]", "Usage 参数段应为 \"<条目> [...]\"，实际 " + Show(argsPart));
            Check(argsPart.IndexOf('/') < 0, "Usage 参数段不得含 '/'，实际 " + Show(argsPart));
            Check(note == "新增 using（两段式别名:/usings add <条目> [...]）",
                "Usage 说明段应点名两段式别名，实际 " + Show(note));

            // provider 必须是 public static SlashResult Provide(string, SlashContext)（Bootstrap 注入点）。
            MethodInfo provide = typeof(UsingAddCommand).GetMethod(
                "Provide", BindingFlags.Public | BindingFlags.Static, null,
                new Type[] { typeof(string), typeof(SlashContext) }, null);
            Check(provide != null, "必须暴露 public static Provide(string rest, SlashContext ctx)");
            Check(provide != null && provide.ReturnType == typeof(SlashResult), "Provide 必须返回 SlashResult");

            // 姿势 B：注入假 provider，证明 handler 走委托且原样透传 rest（含 \"add\" 前缀）。
            string seenRest = null;
            SlashContext seenCtx = null;
            var wired = new SlashContext
            {
                Channel = "test",
                UsingAdd = delegate (string a, SlashContext c) { seenRest = a; seenCtx = c; return SlashResults.Ok("usings-add"); }
            };
            SlashResult r;
            Check(SlashRegistry.TryDispatch("/usings-add add System.IO", wired, out r), "命中 /usings-add 应返回 true");
            Check(r != null && r.Kind == "usings-add", "结果应来自注入的 provider");
            Check(seenRest == "add System.IO", "rest 应原样交给 ctx.CallUsingAdd，实际 " + Show(seenRest));
            Check(object.ReferenceEquals(seenCtx, wired), "ctx 应原样透传");
            Check(wired.RegisteredCapabilities().Contains("usings-add"), "注入后能力表应含 usings-add");

            SlashResult miss;
            Check(SlashRegistry.TryDispatch("/usings-add System.IO", new SlashContext(), out miss), "未注入时仍应返回 true（吞下）");
            Check(miss != null && miss.Kind == "missing-capability", "未注入应回 missing-capability，实际 " + (miss == null ? "<null>" : miss.Kind));
            Check(miss != null && (string)miss.Data["capability"] == "usings-add", "应点名能力 usings-add");
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
