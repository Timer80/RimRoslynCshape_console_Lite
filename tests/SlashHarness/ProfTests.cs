// RoslynProbe —— /prof 的纯逻辑单测：零依赖、离线可跑。
//
// 只断言**纯逻辑**（ParseArgs / OrderTop / BuildTop / BuildStatus 的 Data 形状 + 注册形状 + 未注入能力语义），
// 不碰 ConsoleLayer / ProfilerPrefs / 任何引擎类型 —— 故不需要 UnityEngine，能在无游戏环境的 harness 里跑。
// 反射路径 CaptureTop 离线**不测**：它依赖运行期才存在的 Waylith.dll。
//
// 约定：零依赖单测 —— public static int Run() 返回失败数，不用 xunit。

using System;
using System.Collections.Generic;
using System.Reflection;

namespace RoslynProbe.Tests
{
    /// <summary>/prof 的纯逻辑断言（返回失败数；由 SlashTestHarness 反射发现）。</summary>
    public static class ProfTests
    {
        private static int failures;
        private static int checks;

        /// <summary>返回失败数。</summary>
        public static int Run()
        {
            failures = 0;
            checks = 0;
            SlashRegistry.ResetForTests();
            try
            {
                ParseCases();
                OrderCases();
                TopShapeCases();
                StatusShapeCases();
                RegistryCases();
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("  FAIL [ProfTests] 用例本身抛出: " + ex.GetType().Name + ": " + ex.Message);
            }
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();
            Console.WriteLine("ProfTests: " + (checks - failures) + "/" + checks + " 通过，" + failures + " 失败");
            return failures;
        }

        private static void Check(bool ok, string name)
        {
            checks++;
            if (ok) return;
            failures++;
            Console.WriteLine("  FAIL [ProfTests] " + name);
        }

        private static void ParseCases()
        {
            Check(ProfCommand.ParseArgs("").Mode == ProfCommand.Mode.Status, "裸 /prof → status");
            Check(ProfCommand.ParseArgs("status").Mode == ProfCommand.Mode.Status, "status → status");
            Check(ProfCommand.ParseArgs("status x").Error != null, "status 带参数 → 用法错误");
            ProfCommand.Query q = ProfCommand.ParseArgs("top");
            Check(q.Mode == ProfCommand.Mode.Top && q.N == ProfCommand.DefaultTopN, "top 不带 n → 默认条数");
            Check(ProfCommand.ParseArgs("top 5").N == 5, "top 5 → N=5");
            Check(ProfCommand.ParseArgs("TOP 7").Mode == ProfCommand.Mode.Top, "子命令大小写不敏感");
            Check(ProfCommand.ParseArgs("top 0").Error != null, "top 0 → 用法错误（n 必须 ≥ 1）");
            Check(ProfCommand.ParseArgs("top abc").Error != null, "top abc → 用法错误");
            Check(ProfCommand.ParseArgs("wibble").Error != null, "未知子命令 → 用法错误");

            // R1b/F1 同族：分隔空白 = **任意空白**（TAB / 全角空格 U+3000 / NBSP U+00A0）。
            // 退化成 IndexOf(' ') 时，"top<TAB>3" 会落进"未知子命令"分支 ⇒ 下面三条恰好红；
            // "status<空白>x" 则应进 status 分支（报"不接受参数"）而不是"未知子命令"。
            string[] blanks = { "\t", "\u3000", "\u00A0" };
            string[] blankNames = { "TAB", "全角空格(U+3000)", "NBSP(U+00A0)" };
            for (int i = 0; i < blanks.Length; i++)
            {
                ProfCommand.Query tw = ProfCommand.ParseArgs("top" + blanks[i] + "3");
                Check(tw.Error == null && tw.Mode == ProfCommand.Mode.Top && tw.N == 3,
                    blankNames[i] + " 分隔的 \"top 3\" 应走 top 分支且 N=3，实际 mode=" + tw.Mode
                    + " N=" + tw.N + " err=" + (tw.Error == null ? "<null>" : tw.Error));

                ProfCommand.Query sw = ProfCommand.ParseArgs("status" + blanks[i] + "x");
                Check(sw.Error != null && sw.Error.IndexOf("不接受参数", StringComparison.Ordinal) >= 0,
                    blankNames[i] + " 分隔的 \"status x\" 应走 status 分支（报不接受参数），实际 "
                    + (sw.Error == null ? "<null>" : sw.Error));
            }
        }

        private static void OrderCases()
        {
            var rows = new List<ProfCommand.ScopeRow>
            {
                new ProfCommand.ScopeRow { PathId = 10, SelfUs = 1.0 },
                new ProfCommand.ScopeRow { PathId = 11, SelfUs = 3.0 },
                new ProfCommand.ScopeRow { PathId = 12, SelfUs = 2.0 },
                new ProfCommand.ScopeRow { PathId = 13, SelfUs = 2.0 }
            };
            List<ProfCommand.ScopeRow> top = ProfCommand.OrderTop(rows, 2);
            Check(top.Count == 2, "取前 2 条");
            Check(top[0].PathId == 11, "第一条应是自耗时最大的");
            Check(top[1].PathId == 12, "同值时按 pathId 升序（稳定）");
            Check(ProfCommand.OrderTop(rows, 99).Count == 4, "n 大于总数时全给");
            Check(ProfCommand.OrderTop(null, 3).Count == 0, "null 输入不抛");
        }

        private static void TopShapeCases()
        {
            var snap = new ProfCommand.TopSnapshot();
            snap.Rows.Add(new ProfCommand.ScopeRow
            {
                PathId = 3, Name = "Foo", Path = "Root > Foo",
                SelfUs = 12.5, TotalUs = 20.0, Calls = 4, SelfAllocBytes = 128
            });
            snap.Gaps.Add("demo-gap");
            SlashResult r = ProfCommand.BuildTop(snap, 10);

            Check(r != null && r.Kind == "prof", "Kind 应为 prof");
            Check(r.Data != null && (string)r.Data["mode"] == "top", "mode 应为 top");
            Check(!r.Data.ContainsKey("ok"), "成功路径不得有恒真 ok 键（项目级判例）");
            Check(r.Data["frames"] == null, "未取到帧数时 frames 必须是 null");
            Check(r.Data["framesApprox"] != null, "framesApprox 必须有值以标注近似");
            Check(r.Data["torn"] == null, "未取到 torn 时必须是 null");
            var scopes = r.Data["scopes"] as List<Dictionary<string, object>>;
            Check(scopes != null && scopes.Count == 1, "scopes 应含 1 条");
            if (scopes != null && scopes.Count == 1)
            {
                Check((string)scopes[0]["name"] == "Foo" && (string)scopes[0]["path"] == "Root > Foo", "scope 名字/路径应透传");
                Check((int)scopes[0]["pathId"] == 3 && (double)scopes[0]["selfUs"] == 12.5, "scope pathId/selfUs 应透传");
            }
            var gaps = r.Data["gaps"] as List<object>;
            Check(gaps != null && gaps.Count == 1 && (string)gaps[0] == "demo-gap", "gaps 必须原样带出");
            Check(r.Lines.Count > 0 && r.Lines[0].IndexOf("剖析热点", StringComparison.Ordinal) >= 0, "首行应是热点标题");
        }

        private static void StatusShapeCases()
        {
            var prefs = new Dictionary<string, object> { ["historyCapacity"] = 2000, ["allocTracking"] = false };
            var extra = new Dictionary<string, object> { ["foldRecursion"] = true };
            SlashResult r = ProfCommand.BuildStatus(prefs, extra, "C:\\x\\Profiler.xml", new List<string> { "g1" });

            Check(r.Kind == "prof", "Kind 应为 prof");
            Check((string)r.Data["mode"] == "status", "mode 应为 status");
            Check(!r.Data.ContainsKey("ok"), "成功路径不得有恒真 ok 键（项目级判例）");
            Check(!r.Data.ContainsKey("samplingReason"), "F-3：恒为同一字面量的 samplingReason 必须已删除");
            Check(!r.Data.ContainsKey("excludedAssemblies"), "恒定常量列表不得进 Data");
            Check(r.Data["sampling"] == null && r.Data["frames"] == null && r.Data["torn"] == null,
                "三项不可得必须是 null（不编造值）");
            Check((string)r.Data["prefsPath"] == "C:\\x\\Profiler.xml", "prefsPath 应透传");
            var p = r.Data["prefs"] as Dictionary<string, object>;
            Check(p != null && p["historyCapacity"].Equals(2000), "prefs 应透传");
            var gaps = r.Data["gaps"] as List<object>;
            Check(gaps != null && gaps.Count == 1, "gaps 必须原样带出");
        }

        private static void RegistryCases()
        {
            ProfCommand.Register();
            SlashEntry e;
            Check(SlashRegistry.TryGet("prof", out e), "应登记 verb prof");
            Check(e != null && e.Kind == "prof", "Kind 应为 prof");
            Check(e != null && !e.Dangerous, "/prof 只读，Dangerous 应为 false");
            Check(e != null && e.Handler != null, "Handler 不应为 null");
            string argsPart, note;
            SlashRegistry.SplitUsage(e == null ? "" : e.Usage, out argsPart, out note);
            Check(argsPart.IndexOf("status", StringComparison.Ordinal) >= 0
                && argsPart.IndexOf("top", StringComparison.Ordinal) >= 0, "Usage 参数段应含 status|top");
            Check(note.IndexOf("只读", StringComparison.Ordinal) >= 0, "Usage 说明段应声明只读");

            // 未注入能力 → 冻结 API 的 missing-capability 行（不是异常、也不是 null）。
            SlashResult r = new SlashContext().CallProf("");
            Check(r != null && r.Kind == "missing-capability", "未注入 ctx.Prof 应回 missing-capability");

            // provider 注入方法名：契约 v1.1 §3.5 统一 Provide。
            MethodInfo m = typeof(ProfCommand).GetMethod("Provide",
                BindingFlags.Public | BindingFlags.Static, null,
                new Type[] { typeof(string), typeof(SlashContext) }, null);
            Check(m != null, "必须暴露 public static Provide(string rest, SlashContext ctx)（契约 v1.1 §3.5）");
        }
    }
}
