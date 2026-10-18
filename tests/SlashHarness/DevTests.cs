// RoslynProbe —— /dev 指令的纯逻辑单测（W6；修复轮 1/2 起覆盖契约 v1.1 的线程路由与 toggledState 六态）。
//
// 零依赖（不用 xunit，见 docs/slash-command-authoring.md §5）：public static int Run() 返回失败数，
// 由 SlashTestHarness 反射发现。收尾把表还原成真实登记，避免打断同进程其它写手的测试。
//
// 覆盖两块：
//   1. 内核半（Handle→ctx.CallDev）：用法、语法错误的归属、合法请求原样透传、未注册能力。
//   2. 游戏侧半的纯逻辑核心 DevCommand.Run(rest, tables, ctx)：用**合成表**（无游戏环境可跑）覆盖
//      列 / 翻转 / 置位 / 清位 / 未变更不调回调 / 回调抛出 / 非 bool 与 const·readonly 拒绝 /
//      重名歧义 / 类名限定与大小写口径 / const 不进列表 / 过滤 / Data 形状。
//   3. 线程路由（契约 v1.1 §3.4.1，C1 回归）：**判据只认 ctx.IsGameThread**——
//      事实位 true → 就地；false + Executor → marshal 到 Tick 线程（真开一个后台线程 + 真 Executor 验证）；
//      false + 无 Executor → 明确拒绝（绝不裸调，字段也不改）；Channel=gui 但事实位 false 同样拒绝。
//      另有两条"未确认"路径：队列被 Reset 取消、Executor 已 Poisoned（handler 根本没跑）。
// 真机部分（真三张表、真回调线程）不在本离线套件覆盖范围内；这两项未在 docs/boundary-suite/ 的验收报告里单列小节。

using System;
using System.Collections.Generic;
using System.Threading;
using RoslynProbe;
using RoslynProbe.Repl;

namespace RoslynProbe.Tests
{
    /// <summary>/dev 的单测套件。</summary>
    public static class DevCommandTests
    {
        // ---- 合成三张表（只用于本文件的 Run 调用；不碰真 Verse 类型） ----

        /// <summary>合成表 A：布尔 / 非布尔 / const / static readonly / 回调（正常、抛异常、不存在）。</summary>
        public static class FakeSettings
        {
            public static bool alpha = false;
            public static bool beta = true;
            public static bool gamma = false;
            public static bool boom = false;
            public static int counter = 7;
            public static int alphaToggledCalls;
            public static int alphaToggledThreadId = -1;
            public const bool fixedConst = true;                    // const：列表里不应出现，指定它要给精确文案
            public static readonly bool readOnlyFlag = false;       // static readonly：同上

            public static void alphaToggled()
            {
                alphaToggledCalls++;
                alphaToggledThreadId = Environment.CurrentManagedThreadId;
            }

            public static void boomToggled() { throw new InvalidOperationException("回调炸了"); }
        }

        /// <summary>合成表 B：与 A 重名 alpha（制造跨表歧义），外加 delta。</summary>
        public static class FakeViewSettings
        {
            public static bool alpha = false;
            public static bool delta = false;
        }

        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";
        private static int checks;

        public static int Run()
        {
            Failures.Clear();
            checks = 0;
            currentCase = "(none)";

            Case("注册：verb/kind/dangerous/usage 形状", RegistrationCases);
            Case("内核半：用法与语法错误不调 provider", KernelParseCases);
            Case("内核半：合法请求原样透传给 ctx.Dev", KernelPassThroughCases);
            Case("内核半：能力未注册 → missing-capability", MissingCapabilityCases);
            Case("核心：list 形状、排序、const/readonly 跳过、过滤、非 bool 标注", ListCases);
            Case("核心：翻转 / 置位 / 清位 + 回调联动（同原版）", MutateCases);
            Case("核心：未变更不写不调回调", NoChangeCases);
            Case("核心：回调抛出 → ok=false + toggledState=failed", CallbackThrowCases);
            Case("核心：非 bool 字段明确拒绝（不硬当 bool）", NonBoolCases);
            Case("核心：跨表重名不猜（歧义 + 类名限定消歧）", AmbiguityCases);
            Case("核心：未找到字段 / 未找到类 / 空表 / 空参数", NotFoundCases);
            Case("核心：const / static readonly 不进列表、指定时给精确文案", UnsupportedCases);
            Case("线程：IsGameThread=true 就地；false 且无 Executor 明确拒绝（C1）", ThreadRoutingCases);
            Case("线程：IsGameThread=true 且 Executor 已接线 ⇒ 仍就地、不入队（F1）", GameThreadBeatsExecutorCase);
            Case("线程：false + Executor → marshal 到 Tick 所在线程执行回调", MarshalCases);
            Case("线程：队列被取消 → toggledState=unconfirmed + ok=false", UnconfirmedByCancelCase);
            Case("线程：Executor 已 Poisoned（handler 没跑）→ unconfirmed", UnconfirmedByPoisonCase);
            Case("provider：无游戏环境不抛，形状合法", ProviderRobustnessCases);

            // 收尾 ①：夹具还原到已知初态 —— 保证"第二轮逐字等于第一轮"（§8.4 幂等；
            //   即使将来某个用例忘了在自己的开头重置，也不会把状态漏给下一轮）。
            ResetFakes();

            // 收尾 ②：还原真实登记表（其它写手的测试假定它可用）。
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();

            Console.WriteLine("DevCommandTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- 用例 ----------------

        private static void RegistrationCases()
        {
            SlashRegistry.ResetForTests();
            DevCommand.Register();

            SlashEntry entry;
            Check(SlashRegistry.TryGet("dev", out entry), "应登记 verb=dev");
            Check(entry != null && entry.Kind == "dev", "Kind 应为 dev");
            Check(entry != null && entry.Group == "调试", "Group 应为 调试，实际 " + Show(entry == null ? null : entry.Group));
            Check(entry != null && entry.Dangerous, "有副作用 ⇒ Dangerous 应为 true");
            Check(entry != null && entry.Handler != null, "Handler 不能为 null（本次是完整实现）");

            string args, note;
            SlashRegistry.SplitUsage(entry.Usage, out args, out note);
            Check(args.IndexOf("on|off", StringComparison.Ordinal) >= 0, "Usage 参数段应含 on|off，实际 " + Show(args));
            Check(args.IndexOf("list", StringComparison.Ordinal) >= 0, "Usage 参数段应含 list，实际 " + Show(args));
            Check(note.IndexOf("翻转", StringComparison.Ordinal) >= 0, "Usage 说明段应提到翻转，实际 " + Show(note));

            CheckThrows<InvalidOperationException>(delegate { DevCommand.Register(); },
                "二次 Register 必须被内核按重名拒绝");
        }

        private static void KernelParseCases()
        {
            SlashRegistry.ResetForTests();
            DevCommand.Register();

            var seen = new List<string>();
            SlashContext ctx = Recording(seen);

            SlashResult r;
            Check(SlashRegistry.TryDispatch("/dev", ctx, out r), "/dev 应被吞下（true）");
            Check(r != null && r.Kind == "dev", "Kind 应为 dev");
            Check(r != null && r.Data != null && (bool)r.Data["ok"] == false && (bool)r.Data["usage"] == true,
                "裸 /dev 应回用法页（ok=false, usage=true）");
            Check(seen.Count == 0, "裸 /dev 不该调 provider，实际 " + seen.Count + " 次");
            Check(r.Data.ContainsKey("toggledState") && r.Data["toggledState"] == null, "用法页也应带稳定键 toggledState=null");
            Check(r.Data.ContainsKey("changed") && (bool)r.Data["changed"] == false, "用法页也应带稳定键 changed=false");

            Check(SlashRegistry.TryDispatch("/dev godMode maybe", ctx, out r), "非法 on|off 仍应被吞下");
            Check(r != null && (bool)r.Data["ok"] == false, "非法 on|off 应 ok=false");
            Check(r != null && ((string)r.Data["error"]).IndexOf("on 或 off", StringComparison.Ordinal) >= 0,
                "非法 on|off 应点名只能是 on/off，实际 " + Show(r == null ? null : (string)r.Data["error"]));
            Check(seen.Count == 0, "语法错误不该调 provider，实际 " + seen.Count + " 次");
            Check(r.Data.ContainsKey("toggledState") && r.Data["toggledState"] == null, "语法错误结果也应带稳定键 toggledState=null");
            Check(r.Data.ContainsKey("toggledError") && r.Data["toggledError"] == null, "语法错误结果也应带稳定键 toggledError=null");

            Check(SlashRegistry.TryDispatch("/dev godMode on extra", ctx, out r), "参数过多仍应被吞下");
            Check(r != null && (bool)r.Data["usage"] == true, "参数过多应回用法页");
            Check(seen.Count == 0, "参数过多不该调 provider");

            Check(SlashRegistry.TryDispatch("/dev   ", ctx, out r), "只有空白的 rest 应回用法页");
            Check(r != null && (bool)r.Data["usage"] == true, "空白 rest 应回用法页");
            Check(seen.Count == 0, "空白 rest 不该调 provider");
        }

        private static void KernelPassThroughCases()
        {
            SlashRegistry.ResetForTests();
            DevCommand.Register();

            var seen = new List<string>();
            SlashContext ctx = Recording(seen);

            string[] inputs =
            {
                "/dev list",
                "/dev list draw",
                "/dev list  two  spaces",
                "/dev godMode",
                "/dev godMode on",
                "/dev godMode OFF",
                "/dev DebugSettings.godMode off",
                "/dev LIST"
            };
            string[] expect =
            {
                "list",
                "list draw",
                "list  two  spaces",
                "godMode",
                "godMode on",
                "godMode OFF",
                "DebugSettings.godMode off",
                "LIST"
            };
            for (int i = 0; i < inputs.Length; i++)
            {
                SlashResult got;
                Check(SlashRegistry.TryDispatch(inputs[i], ctx, out got), inputs[i] + " 应被吞下");
                Check(got != null && got.Kind == "dev", inputs[i] + " 的 Kind 应为 dev");
                Check(seen.Count == i + 1, inputs[i] + " 应恰好调 provider 一次");
                Check(seen.Count > i && seen[i] == expect[i],
                    inputs[i] + " 的 rest 应原样透传，期望 " + Show(expect[i]) + " 实际 "
                    + Show(seen.Count > i ? seen[i] : null));
            }
        }

        private static void MissingCapabilityCases()
        {
            SlashRegistry.ResetForTests();
            DevCommand.Register();

            SlashResult r;
            Check(SlashRegistry.TryDispatch("/dev list", new SlashContext { Channel = "test", IsGameThread = true }, out r),
                "未注册也能吞下");
            Check(r != null && r.Kind == "missing-capability", "未注册 ctx.Dev ⇒ missing-capability，实际 "
                + Show(r == null ? null : r.Kind));
            Check(r != null && (string)r.Data["capability"] == "dev", "能力名应为 dev");

            Check(SlashRegistry.TryDispatch("/dev", new SlashContext(), out r), "裸 /dev 不需要 provider");
            Check(r != null && (bool)r.Data["usage"] == true, "裸 /dev 即便未注册也应回用法页");
        }

        private static void ListCases()
        {
            ResetFakes();   // 本用例读的是夹具的**当前值**（下面断言 alpha=false / counter=7）⇒ 必须先把前提钉到已知值，
                            // 否则它依赖"我是第一次跑"：前一轮/前一个用例把 alpha 翻成 true 后，第二轮这条断言必红（W6-fix4）。
            SlashResult r = DevCommand.Run("list", Tables(typeof(FakeSettings)), Inline());
            Check(Ok(r), "list 应成功");
            Check((int)r.Data["count"] == 7, "FakeSettings 的可写 public static 字段应为 7 条，实际 "
                + (int)r.Data["count"]);
            Check((int)r.Data["total"] == 7, "total 应为 7");
            Check(r.Data["filter"] == null, "无过滤时 filter 应为 null");

            var fields = (List<Dictionary<string, object>>)r.Data["fields"];
            var names = new List<string>();
            foreach (Dictionary<string, object> f in fields)
            {
                names.Add((string)f["class"] + "." + (string)f["field"]);
                Check(f.ContainsKey("value"), "每个字段项都要有 value");
                Check(f.ContainsKey("type"), "每个字段项都要有 type");
            }
            Check(names.IndexOf("FakeSettings.alpha") >= 0, "应含 FakeSettings.alpha");
            Check(names.IndexOf("FakeSettings.fixedConst") < 0, "const 字段不应进列表（同原版跳过 IsLiteral）");
            Check(names.IndexOf("FakeSettings.readOnlyFlag") < 0, "static readonly 不应进列表（M1）");
            Check(names.IndexOf("FakeSettings.counter") >= 0, "非 bool 字段也应列出，实际 " + string.Join(",", names));

            for (int i = 1; i < names.Count; i++)
                Check(string.CompareOrdinal(names[i - 1], names[i]) < 0, "类内字段应按名排序："
                    + names[i - 1] + " 在 " + names[i] + " 之前");

            foreach (Dictionary<string, object> f in fields)
            {
                if ((string)f["field"] == "counter")
                {
                    Check((int)f["value"] == 7, "counter 的 value 应为 7");
                    Check((string)f["type"] == "Int32", "counter 的 type 应为 Int32，实际 " + Show((string)f["type"]));
                }
                if ((string)f["field"] == "alpha")
                {
                    Check((bool)f["value"] == false, "alpha 的 value 应为 false");
                    Check((string)f["type"] == "Boolean", "alpha 的 type 应为 Boolean");
                }
            }

            // 过滤：子串匹配字段名或类名，大小写不敏感
            SlashResult f1 = DevCommand.Run("list del", Tables(typeof(FakeSettings), typeof(FakeViewSettings)), Inline());
            Check(Ok(f1), "带过滤的 list 应成功");
            Check((int)f1.Data["count"] == 1, "filter=del 应只命中 delta，实际 " + (int)f1.Data["count"]);
            Check((int)f1.Data["total"] == 9, "total 应为两表之和 9，实际 " + (int)f1.Data["total"]);
            Check((string)f1.Data["filter"] == "del", "Data[\"filter\"] 应回显过滤串");

            SlashResult f2 = DevCommand.Run("list FAKEVIEW", Tables(typeof(FakeSettings), typeof(FakeViewSettings)), Inline());
            Check(Ok(f2) && (int)f2.Data["count"] == 2, "filter=FAKEVIEW 应命中整张 FakeViewSettings（2 条）");

            SlashResult f3 = DevCommand.Run("list zzz", Tables(typeof(FakeSettings)), Inline());
            Check(Ok(f3) && (int)f3.Data["count"] == 0, "无匹配应 count=0 且仍 ok");
            Check(f3.Lines.Count >= 1 && f3.Lines[f3.Lines.Count - 1].IndexOf("无匹配字段", StringComparison.Ordinal) >= 0,
                "无匹配应给出提示行，实际 " + Show(f3.Lines[f3.Lines.Count - 1]));
        }

        private static void MutateCases()
        {
            ResetFakes();

            SlashResult flip = DevCommand.Run("alpha", Tables(typeof(FakeSettings)), Inline());
            Check(Ok(flip), "翻转应成功");
            Check((bool)flip.Data["before"] == false && (bool)flip.Data["after"] == true, "before/after 应为 false→true");
            Check((bool)flip.Data["changed"] == true, "changed 应为 true");
            Check((string)flip.Data["mode"] == "flip", "mode 应为 flip");
            Check((string)flip.Data["class"] == "FakeSettings" && (string)flip.Data["field"] == "alpha",
                "class/field 应回填");
            Check((string)flip.Data["toggledCallback"] == "alphaToggled", "toggledCallback 应为 alphaToggled");
            Check((string)flip.Data["toggledState"] == "called", "toggledState 应为 called，实际 "
                + Show((string)flip.Data["toggledState"]));
            Check(flip.Data["toggledError"] == null, "回调未抛时 toggledError 应为 null");
            Check(FakeSettings.alpha == true, "字段应真的被翻转");
            Check(FakeSettings.alphaToggledCalls == 1, "回调应被调 1 次，实际 " + FakeSettings.alphaToggledCalls);
            Check(flip.Lines.Count == 2 && flip.Lines[0].IndexOf("false → true", StringComparison.Ordinal) >= 0,
                "首行应渲染 false → true，实际 " + Show(flip.Lines[0]));
            Check(flip.Lines[1].IndexOf("已调用", StringComparison.Ordinal) >= 0, "次行应说回调已调用");

            ResetFakes();
            SlashResult off = DevCommand.Run("beta off", Tables(typeof(FakeSettings)), Inline());
            Check(Ok(off) && (bool)off.Data["after"] == false, "beta off 应置为 false");
            Check((string)off.Data["mode"] == "off", "mode 应为 off");
            Check(off.Lines[0].IndexOf("已清位", StringComparison.Ordinal) >= 0, "行应说已清位，实际 " + Show(off.Lines[0]));
            Check(FakeSettings.beta == false, "字段应真的被清位");

            ResetFakes();
            SlashResult gamma = DevCommand.Run("gamma on", Tables(typeof(FakeSettings)), Inline());
            Check(Ok(gamma) && (bool)gamma.Data["after"] == true, "gamma on 应置为 true");
            Check((bool)gamma.Data["changed"] == true, "changed 应为 true");
            Check(gamma.Data["toggledCallback"] == null, "没有 gammaToggled ⇒ toggledCallback 应为 null");
            Check((string)gamma.Data["toggledState"] == "no-callback", "状态应为 no-callback，实际 "
                + Show((string)gamma.Data["toggledState"]));
            Check(gamma.Lines.Count == 2 && gamma.Lines[1].IndexOf("不存在", StringComparison.Ordinal) >= 0,
                "应给出「回调不存在」行，实际 " + Show(gamma.Lines.Count > 1 ? gamma.Lines[1] : null));

            ResetFakes();
            SlashResult qualified = DevCommand.Run("FakeSettings.alpha on",
                Tables(typeof(FakeSettings), typeof(FakeViewSettings)), Inline());
            Check(Ok(qualified) && (bool)qualified.Data["after"] == true, "类名限定应能消歧并置位");
            Check(FakeSettings.alphaToggledCalls == 1, "类名限定的写入同样要调回调");
        }

        private static void NoChangeCases()
        {
            ResetFakes();
            FakeSettings.alpha = true;
            FakeSettings.alphaToggledCalls = 0;

            SlashResult r = DevCommand.Run("alpha on", Tables(typeof(FakeSettings)), Inline());
            Check(Ok(r), "已是 on 再 on：不该报错");
            Check((bool)r.Data["changed"] == false, "changed 应为 false");
            Check((bool)r.Data["before"] == true && (bool)r.Data["after"] == true, "before/after 应都是 true");
            Check(r.Data["toggledCallback"] == null, "未变更 ⇒ 不调回调，toggledCallback 应为 null");
            Check((string)r.Data["toggledState"] == "not-changed", "状态应为 not-changed，实际 "
                + Show((string)r.Data["toggledState"]));
            Check(FakeSettings.alphaToggledCalls == 0, "未变更 ⇒ 回调一次都不能调，实际 "
                + FakeSettings.alphaToggledCalls);
            Check(r.Lines.Count == 1, "未变更只应有 1 行（无回调行），实际 " + r.Lines.Count);
            Check(r.Lines[0].IndexOf("未变更", StringComparison.Ordinal) >= 0, "行应说明未变更，实际 " + Show(r.Lines[0]));

            ResetFakes();
            FakeSettings.alpha = false;
            SlashResult r2 = DevCommand.Run("alpha", Tables(typeof(FakeSettings)), Inline());
            Check(Ok(r2) && (bool)r2.Data["changed"] == true, "翻转本来就翻转，必变更");
        }

        private static void CallbackThrowCases()
        {
            ResetFakes();
            SlashResult r = DevCommand.Run("boom", Tables(typeof(FakeSettings)), Inline());

            Check(!Ok(r), "回调抛出 ⇒ ok 必须 false（别让「联动未完成」看起来像成功）");
            Check((bool)r.Data["changed"] == true && (bool)r.Data["after"] == true && FakeSettings.boom == true,
                "字段仍应写入（那是主线效果）");
            Check((string)r.Data["toggledCallback"] == "boomToggled", "toggledCallback 应为 boomToggled");
            Check((string)r.Data["toggledState"] == "failed", "状态应为 failed，实际 "
                + Show((string)r.Data["toggledState"]));
            string err = (string)r.Data["toggledError"];
            Check(err != null && err.IndexOf("InvalidOperationException", StringComparison.Ordinal) >= 0,
                "toggledError 应含真实异常类型（拆掉 TargetInvocationException 外壳），实际 " + Show(err));
            Check(err != null && err.IndexOf("回调炸了", StringComparison.Ordinal) >= 0, "toggledError 应含异常消息");
            Check(r.Lines.Count == 2 && r.Lines[1].IndexOf("失败", StringComparison.Ordinal) >= 0,
                "应给出回调失败行，实际 " + Show(r.Lines.Count > 1 ? r.Lines[1] : null));
        }

        private static void NonBoolCases()
        {
            ResetFakes();
            SlashContext ctx = Inline();

            SlashResult flip = DevCommand.Run("counter", Tables(typeof(FakeSettings)), ctx);
            Check(!Ok(flip), "非 bool 字段的翻转必须被拒，实际 ok=" + Ok(flip));
            Check(((string)flip.Data["error"]).IndexOf("不是 bool", StringComparison.Ordinal) >= 0,
                "错误应点名「不是 bool」，实际 " + Show((string)flip.Data["error"]));
            Check((string)flip.Data["type"] == "Int32", "Data[\"type\"] 应为 Int32");
            Check((string)flip.Data["class"] == "FakeSettings" && (string)flip.Data["field"] == "counter",
                "拒绝时也要回填 class/field");

            SlashResult on = DevCommand.Run("counter on", Tables(typeof(FakeSettings)), ctx);
            Check(!Ok(on), "非 bool 字段的 on 必须被拒");
            SlashResult off = DevCommand.Run("counter off", Tables(typeof(FakeSettings)), ctx);
            Check(!Ok(off), "非 bool 字段的 off 必须被拒");
            Check(FakeSettings.counter == 7, "被拒的请求绝不改字段，实际 " + FakeSettings.counter);
        }

        private static void AmbiguityCases()
        {
            ResetFakes();
            var both = Tables(typeof(FakeSettings), typeof(FakeViewSettings));
            SlashContext ctx = Inline();

            SlashResult r = DevCommand.Run("alpha", both, ctx);
            Check(!Ok(r), "跨表重名必须拒绝，不能猜");
            Check(((string)r.Data["error"]).IndexOf("不唯一", StringComparison.Ordinal) >= 0,
                "错误应说「不唯一」，实际 " + Show((string)r.Data["error"]));
            var ambiguous = (List<Dictionary<string, object>>)r.Data["ambiguous"];
            Check(ambiguous != null && ambiguous.Count == 2, "ambiguous 应列出两个候选，实际 "
                + (ambiguous == null ? -1 : ambiguous.Count));
            Check((string)r.Data["field"] == "alpha", "Data[\"field\"] 应为 alpha");
            Check(FakeSettings.alpha == false && FakeViewSettings.alpha == false, "歧义时一个字段都不能改");

            SlashResult r2 = DevCommand.Run("alpha on", both, ctx);
            Check(!Ok(r2), "带 on 的歧义同样拒绝");

            SlashResult viaFullName = DevCommand.Run("Verse.DebugViewSettings.alpha", both, ctx);
            Check(!Ok(viaFullName), "合成类型没有 Verse 全名 ⇒ 应报「未找到类」");
            Check(((string)viaFullName.Data["error"]).IndexOf("未找到类", StringComparison.Ordinal) >= 0,
                "应点名未找到类，实际 " + Show((string)viaFullName.Data["error"]));

            SlashResult inOtherClass = DevCommand.Run("FakeViewSettings.delta", both, ctx);
            Check(Ok(inOtherClass), "类名限定应命中另一张表的字段");
            Check(FakeViewSettings.delta == true, "应真的写进 FakeViewSettings.delta");

            SlashResult wrongClass = DevCommand.Run("FakeViewSettings.beta", both, ctx);
            Check(!Ok(wrongClass), "FakeViewSettings 里没有 beta ⇒ 拒绝");
            Check(((string)wrongClass.Data["error"]).IndexOf("没有 public static 可写字段", StringComparison.Ordinal) >= 0,
                "应说该类没有这个字段，实际 " + Show((string)wrongClass.Data["error"]));
        }

        private static void NotFoundCases()
        {
            ResetFakes();
            SlashContext ctx = Inline();
            var one = Tables(typeof(FakeSettings));

            SlashResult r = DevCommand.Run("nosuchfield", one, ctx);
            Check(!Ok(r), "未找到字段应失败");
            Check(((string)r.Data["error"]).IndexOf("未找到字段", StringComparison.Ordinal) >= 0,
                "错误应说未找到字段，实际 " + Show((string)r.Data["error"]));
            Check((string)r.Data["field"] == "nosuchfield", "Data[\"field\"] 应回显请求");
            Check(r.Data.ContainsKey("toggledState") && r.Data["toggledState"] == null, "失败形状也应稳定带 toggledState=null（F5）");
            Check(r.Data.ContainsKey("changed") && (bool)r.Data["changed"] == false, "失败形状也应带 changed=false（F5）");

            SlashResult upper = DevCommand.Run("ALPHA", one, ctx);
            Check(Ok(upper), "精确匹配不到时应回退到忽略大小写匹配");
            Check(FakeSettings.alpha == true, "忽略大小写命中后应真的写入");

            SlashResult noTables = DevCommand.Run("list", new List<Type>(), ctx);
            Check(!Ok(noTables), "空表应失败");
            SlashResult nullTables = DevCommand.Run("list", null, ctx);
            Check(!Ok(nullTables), "null 表应失败（且不抛）");

            SlashResult empty = DevCommand.Run("", one, null);
            Check(!Ok(empty) && (bool)empty.Data["usage"] == true, "空 rest 应回用法页（ctx=null 也不抛）");
            SlashResult nil = DevCommand.Run(null, one, null);
            Check(!Ok(nil) && (bool)nil.Data["usage"] == true, "null rest 应回用法页");

            SlashResult badFormat = DevCommand.Run(".alpha", one, ctx);
            Check(!Ok(badFormat), "'.alpha' 格式不对应失败");
            SlashResult badFormat2 = DevCommand.Run("FakeSettings.", one, ctx);
            Check(!Ok(badFormat2), "'FakeSettings.' 格式不对应失败");
        }

        private static void UnsupportedCases()
        {
            ResetFakes();
            SlashContext ctx = Inline();

            SlashResult ro = DevCommand.Run("readOnlyFlag on", Tables(typeof(FakeSettings)), ctx);
            Check(!Ok(ro), "static readonly 必须被拒（M1）");
            Check(((string)ro.Data["error"]).IndexOf("static readonly", StringComparison.Ordinal) >= 0,
                "应点名 static readonly，实际 " + Show((string)ro.Data["error"]));
            Check(((string)ro.Data["error"]).IndexOf("刻意不出现在", StringComparison.Ordinal) >= 0,
                "应说明它刻意不出现在列表里");
            Check(FakeSettings.readOnlyFlag == false, "readonly 字段不可能被改");

            SlashResult c = DevCommand.Run("fixedConst", Tables(typeof(FakeSettings)), ctx);
            Check(!Ok(c), "const 必须被拒");
            Check(((string)c.Data["error"]).IndexOf("const", StringComparison.Ordinal) >= 0,
                "应点名 const，实际 " + Show((string)c.Data["error"]));

            SlashResult qualified = DevCommand.Run("FakeSettings.readOnlyFlag", Tables(typeof(FakeSettings)), ctx);
            Check(!Ok(qualified), "类名限定的 readonly 同样被拒");
            Check((string)qualified.Data["class"] == "FakeSettings", "拒绝时回填 class");
        }

        private static void ThreadRoutingCases()
        {
            ResetFakes();
            var one = Tables(typeof(FakeSettings));
            int here = Environment.CurrentManagedThreadId;

            // ① 事实位 true → 就地调用（回调跑在调用线程上）
            SlashResult inline = DevCommand.Run("alpha", one, new SlashContext { Channel = "gui", IsGameThread = true });
            Check(Ok(inline), "IsGameThread=true 应就地成功");
            Check((string)inline.Data["toggledState"] == "called", "状态应为 called");
            Check(FakeSettings.alphaToggledThreadId == here, "就地调用时回调必须跑在调用线程上，实际 "
                + FakeSettings.alphaToggledThreadId + " vs " + here);

            // ② 事实位 false 且无 Executor → 明确拒绝（绝不裸调 Unity/Verse 回调）
            ResetFakes();
            var noExecutor = new SlashContext { Channel = "pipe", IsGameThread = false, Executor = null };
            SlashResult refused = DevCommand.Run("alpha", one, noExecutor);
            Check(!Ok(refused), "不在游戏线程且无 Executor ⇒ 必须拒绝");
            string msg = (string)refused.Data["error"];
            Check(msg != null && msg.IndexOf("拒绝执行", StringComparison.Ordinal) >= 0,
                "错误应明说拒绝执行，实际 " + Show(msg));
            Check(msg != null && msg.IndexOf("IsGameThread", StringComparison.Ordinal) >= 0,
                "错误应点名事实位 ctx.IsGameThread（判据不是 Channel），实际 " + Show(msg));
            Check((string)refused.Data["toggledCallback"] == "alphaToggled", "错误里应点名需要 marshal 的回调");
            Check((string)refused.Data["toggledState"] == "refused", "拒绝时状态应为 refused，实际 "
                + Show(DataText(refused, "toggledState")));
            Check((bool)refused.Data["changed"] == false, "拒绝时 changed 必须 false");
            Check(FakeSettings.alpha == false, "拒绝时字段一个 bit 都不能改");
            Check(FakeSettings.alphaToggledCalls == 0, "拒绝时回调不能跑");

            // ③ C1 回归：Channel=gui 但事实位 false —— 不得按 Channel 猜，同样拒绝
            ResetFakes();
            var lyingChannel = new SlashContext { Channel = "gui", IsGameThread = false, Executor = null };
            Check(!Ok(DevCommand.Run("alpha", one, lyingChannel)),
                "Channel=gui 但 ctx.IsGameThread=false 且无 Executor ⇒ 必须拒绝（契约 v1.1 判据只认事实位）");
            Check(FakeSettings.alphaToggledCalls == 0, "C1 路径下回调一次都不能跑");

            // ④ 没有同名回调的字段不碰 Unity/Verse：无 Executor 的读线程上仍可就地写
            ResetFakes();
            SlashResult noCallback = DevCommand.Run("gamma", one, noExecutor);
            Check(Ok(noCallback) && FakeSettings.gamma, "无回调字段在无 Executor 的读线程上仍可就地写");
            Check((string)noCallback.Data["toggledState"] == "no-callback", "状态应为 no-callback");

            // ⑤ 值未变 ⇒ 根本不需要游戏线程，也就不会因缺 Executor 被拒
            ResetFakes();
            FakeSettings.alpha = true;
            SlashResult noChange = DevCommand.Run("alpha on", one, noExecutor);
            Check(Ok(noChange) && (bool)noChange.Data["changed"] == false,
                "值未变不需要回调 ⇒ 不该被线程路由拒绝");
        }

        /// <summary>
        /// F1：**事实位优先于 Executor** —— GUI 壳（本次调用已在游戏线程 + Bootstrap 已接线 Executor）绝不能
        /// Enqueue + Wait（那会在游戏线程上等一个只有游戏线程才能排空的队列 ⇒ 15s 超时 ⇒ 每个回调字段 unconfirmed）。
        /// 断言就地调用 + **真没入队**（PendingCount == 0）。
        /// </summary>
        private static void GameThreadBeatsExecutorCase()
        {
            ResetFakes();
            var executor = new Executor();
            var ctx = new SlashContext { Channel = "gui", IsGameThread = true, Executor = executor };
            int here = Environment.CurrentManagedThreadId;

            SlashResult r = DevCommand.Run("alpha", Tables(typeof(FakeSettings)), ctx);

            Check(Ok(r), "IsGameThread=true 且 Executor 已接线 ⇒ 仍应就地成功，实际 error=" + Show(DataText(r, "error")));
            Check(DataText(r, "toggledState") == "called", "状态应为 called，实际 " + Show(DataText(r, "toggledState")));
            Check(FakeSettings.alphaToggledCalls == 1, "回调应被调 1 次，实际 " + FakeSettings.alphaToggledCalls);
            Check(FakeSettings.alphaToggledThreadId == here, "回调必须跑在调用线程（就地），实际 "
                + FakeSettings.alphaToggledThreadId + " vs " + here);
            Check(executor.PendingCount == 0, "IsGameThread=true 时**不得入队**（PendingCount 应为 0），实际 "
                + executor.PendingCount);
        }

        /// <summary>
        /// marshal 路径：真开一个后台线程当"会话读线程"，主线程当"游戏线程"反复 Tick 排空队列。
        /// 断言回调跑在 **Tick 的线程**（= 游戏线程）上，而不是调用 Run 的那个线程。
        /// </summary>
        private static void MarshalCases()
        {
            ResetFakes();
            var executor = new Executor();
            var ctx = new SlashContext { Channel = "pipe", IsGameThread = false, Executor = executor };
            int drainThreadId = Environment.CurrentManagedThreadId;
            int callerThreadId = -1;
            SlashResult result = null;

            var worker = new Thread(new ThreadStart(delegate
            {
                callerThreadId = Environment.CurrentManagedThreadId;
                result = DevCommand.Run("alpha", Tables(typeof(FakeSettings)), ctx);
            }));
            worker.IsBackground = true;
            worker.Start();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (worker.IsAlive && DateTime.UtcNow < deadline)
            {
                executor.Tick(1);
                Thread.Sleep(2);
            }
            worker.Join(5000);
            Check(!worker.IsAlive, "worker 必须在超时内结束（残留线程会跨用例/跨轮污染夹具）");

            Check(result != null, "marshal 路径应返回结果");
            Check(Ok(result), "marshal 成功应 ok=true，实际 error=" + Show(DataText(result, "error")));
            Check(DataText(result, "toggledState") == "called", "状态应为 called，实际 "
                + Show(DataText(result, "toggledState")));
            Check(FakeSettings.alpha == true, "字段应已变更");
            Check(FakeSettings.alphaToggledCalls == 1, "回调应被调 1 次，实际 " + FakeSettings.alphaToggledCalls);
            Check(callerThreadId != drainThreadId, "本用例必须真的跨线程（否则断言没意义）");
            Check(FakeSettings.alphaToggledThreadId == drainThreadId,
                "回调必须跑在 Tick 的线程（游戏线程 " + drainThreadId + "）上，实际 "
                + FakeSettings.alphaToggledThreadId + "（调用线程 " + callerThreadId + "）");
        }

        /// <summary>队列里的请求被 Executor.Reset() 取消 → Wait 抛取消 → unconfirmed（不挂住）。</summary>
        private static void UnconfirmedByCancelCase()
        {
            ResetFakes();
            var executor = new Executor();
            var ctx = new SlashContext { Channel = "pipe", IsGameThread = false, Executor = executor };
            SlashResult result = null;

            var worker = new Thread(new ThreadStart(delegate
            {
                result = DevCommand.Run("alpha", Tables(typeof(FakeSettings)), ctx);
            }));
            worker.IsBackground = true;
            worker.Start();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (executor.PendingCount == 0 && DateTime.UtcNow < deadline) Thread.Sleep(1);
            executor.Reset();          // 取消排队请求：Tcs.TrySetCanceled
            worker.Join(5000);

            Check(!worker.IsAlive, "取消后 Run 必须及时返回（不许挂住）");   // 同时保证没有残留线程污染后续用例
            Check(result != null, "取消路径应返回结果");
            Check(!Ok(result), "未确认 ⇒ ok 必须 false，实际 error=" + Show(DataText(result, "error")));
            Check(DataText(result, "toggledState") == "unconfirmed", "状态应为 unconfirmed，实际 "
                + Show(DataText(result, "toggledState")));
            Check(result != null && (bool)result.Data["changed"] == true, "字段已变更（changed=true），但必须标未确认");
            Check(FakeSettings.alphaToggledCalls == 0, "被取消后回调不应跑");
        }

        /// <summary>Executor 已 Poisoned：Enqueue 直接给一个已完成的失败结果（handler 根本没跑）—— 不许报成功。</summary>
        private static void UnconfirmedByPoisonCase()
        {
            ResetFakes();
            var executor = new Executor();
            executor.Panic();
            var ctx = new SlashContext { Channel = "pipe", IsGameThread = false, Executor = executor };

            SlashResult r = DevCommand.Run("alpha", Tables(typeof(FakeSettings)), ctx);
            Check(!Ok(r), "handler 没跑 ⇒ ok 必须 false");
            Check((string)r.Data["toggledState"] == "unconfirmed", "状态应为 unconfirmed，实际 "
                + Show((string)r.Data["toggledState"]));
            Check(((string)r.Data["toggledError"]).IndexOf("session poisoned", StringComparison.Ordinal) >= 0,
                "错误应原样带出 session poisoned，实际 " + Show((string)r.Data["toggledError"]));
            Check(FakeSettings.alpha == true, "字段仍应写入（主线效果），实际 " + FakeSettings.alpha);
            Check(FakeSettings.alphaToggledCalls == 0, "回调不应跑");
        }

        private static void ProviderRobustnessCases()
        {
            // 离线环境下 Provide 有两种落点，本用例的断言对**两者都成立**（核对者实测：本机进程确实能 Load 到
            // Assembly-CSharp ⇒ 实际走的是成功路径）：
            //   ① 能解析到三张表（exe 旁放了游戏 DLL，或本来就在游戏进程内）⇒ 成功路径：ok=true、
            //      Data 带 count/total/fields（本机实测可写 181 条）；
            //   ② 解析不到 ⇒ 未解析路径：ok=false + 一行点名的错误。
            // 所以这里钉的是「Provide 绝不抛且形状合法」，**不是**「环境里没有表」。
            SlashResult r = null;
            bool threw = false;
            try { r = DevCommand.Provide("list", new SlashContext()); }
            catch (Exception) { threw = true; }
            Check(!threw, "Provide 绝不抛（表解析成功 / 失败两种落点都只给结果行）");
            Check(r != null && r.Kind == "dev", "Provide 的 Kind 应为 dev");
            Check(r != null && r.Data != null && r.Data.ContainsKey("ok"), "Provide 的 Data 应有 ok");

            SlashResult r2 = null;
            bool threw2 = false;
            try { r2 = DevCommand.Provide(null, null); }
            catch (Exception) { threw2 = true; }
            Check(!threw2, "Provide(null, null) 绝不抛");
            Check(r2 != null && r2.Kind == "dev", "Provide(null,null) 的 Kind 应为 dev");
        }

        // ---------------- 工具 ----------------

        private static List<Type> Tables(params Type[] types) { return new List<Type>(types); }

        /// <summary>默认测试上下文：模拟"本次调用就在游戏线程"（路由用例会自己造别的上下文）。</summary>
        private static SlashContext Inline()
        {
            return new SlashContext { Channel = "test", IsGameThread = true };
        }

        private static SlashContext Recording(List<string> seen)
        {
            return new SlashContext
            {
                Channel = "test",
                IsGameThread = true,
                Dev = delegate(string rest, SlashContext c)
                {
                    seen.Add(rest);
                    return SlashResults.Ok("dev");
                }
            };
        }

        private static bool Ok(SlashResult r)
        {
            return r != null && r.Data != null && r.Data.ContainsKey("ok") && (bool)r.Data["ok"];
        }

        private static void ResetFakes()
        {
            FakeSettings.alpha = false;
            FakeSettings.beta = true;
            FakeSettings.gamma = false;
            FakeSettings.boom = false;
            FakeSettings.counter = 7;
            FakeSettings.alphaToggledCalls = 0;
            FakeSettings.alphaToggledThreadId = -1;
            FakeViewSettings.alpha = false;
            FakeViewSettings.delta = false;
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

        /// <summary>取 Data 里的字符串用于失败消息（缺键 / 为 null 都不抛）。</summary>
        private static string DataText(SlashResult r, string key)
        {
            if (r == null || r.Data == null || !r.Data.ContainsKey(key)) return "<无>";
            object v = r.Data[key];
            return v == null ? "<null>" : v.ToString();
        }
    }
}
