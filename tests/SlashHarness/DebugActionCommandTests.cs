// RoslynProbe —— /debug-action 的纯逻辑单测(W7)。
//
// 只覆盖纯逻辑(语法解析 / 登记形状 / 内核派发),绝不碰 Verse、也绝不碰游戏:
//   * DebugActionRequest(含嵌套 Args)的成员零 Verse 类型 ⇒ 没有 Assembly-CSharp 的进程也能加载它;
//   * 登记与派发断言走**反射**(测试 IL 里不静态引用 DebugActionCommand —— 它的静态字段含
//     Verse 值类型 IntVec3 / PlanetTile,加载它需要 Assembly-CSharp)。反射不可用时就跳过这几条
//     并打印原因,不把环境问题记成失败。
//
// 真机行为(建树耗时、Harmony 鼠标代理、Tool* 实执行)不在本离线套件覆盖范围内;真机验收见
// docs/boundary-suite/s11-8/REPORT-specials.md §2(建树耗时与分片)与 §3(Tool 系列鼠标代理)——
// 该读数测于 b45e570 产物,行为面核验见该文 §12。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using RoslynProbe;

namespace RoslynProbe.Tests
{
    /// <summary>零依赖测试类:返回失败数,由 SlashTestHarness 反射调用(约定见 docs/slash-command-authoring.md §5)。</summary>
    public static class DebugActionCommandTests
    {
        private const string Verb = "debug-action";

        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";
        private static int checks;

        public static int Run()
        {
            Failures.Clear();
            checks = 0;
            currentCase = "(none)";

            Case("语法:空串→用法 / list(+filter) / refresh", ModeCases);
            Case("语法:显式分隔符 \" -- \"", ExplicitSeparatorCases);
            Case("语法:位置式切分(路径 + x z / tile / pawn / 单整数)", PositionalCases);
            Case("语法:边界(只有 \"--\" / \"--\" 后缺参 / 关键词路径)", BoundCases);
            Case("参数:位置式与命名式", ArgsCases);
            Case("参数:错误写法必须响亮报错", ArgsErrorCases);
            Case("登记:verb/Kind/Handler/Dangerous/Usage 形状(反射)", RegisterShapeCases);
            Case("派发:provider 未注入 → missing-capability;已注入 → rest 原样透传(反射)", DispatchCases);
            Case("线程规则:非游戏线程且无 Executor → 明确拒绝(绝不裸碰 Verse)", RefuseOffThreadCases);
            Case("线程规则:非游戏线程 + 有 Executor → Enqueue 后由 Tick 履约(不抛)", MarshalCases);
            Case("执行形状:成功/拒绝/失败/语法错误共用同一组 Data 键", ExecuteShapeCases);
            Case("唯一出口 StableKeys:补 canonical 键 + S1 守卫(删守卫即红)", StableKeysCases);
            Case("Data 形状:七组探针的 Data 都含 canonical 键集(消费方不踩 KeyNotFound)", SevenProbeShapeCases);
            Case("Lines↔Data 同源:仅打印/仅扫描/仅探针被预算截断时的汇总口径", ListFactsCases);

            // 收尾:把表还原成「只含真实登记」的状态(与 SlashRegistryTests 同款)。
            // 别的写手的 verb 若撞名,RegisterAll 会抛 —— 那是他们的问题,记一行警告,不当成本类的失败。
            try
            {
                SlashRegistry.ResetForTests();
                SlashRegistry.RegisterAll();
            }
            catch (Exception ex)
            {
                Console.WriteLine("DebugActionCommandTests: 还原真实指令表失败(不影响本类结论): " + ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine("DebugActionCommandTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- 用例 ----------------

        private static void ModeCases()
        {
            DebugActionRequest r = Must("", DebugActionRequest.Intent.Usage);
            Check(r.Filter == "" && r.Path == "" && r.ArgsRaw == "", "Usage 模式三个字段都应为空");

            Must("   ", DebugActionRequest.Intent.Usage);

            r = Must("list", DebugActionRequest.Intent.List);
            Check(r.Filter == "", "list 无 filter 时 Filter 应为空串");
            r = Must("LIST", DebugActionRequest.Intent.List);
            Check(r.Filter == "", "list 大小写不敏感");
            r = Must("list Frame", DebugActionRequest.Intent.List);
            Check(r.Filter == "Frame", "list filter 应为 \"Frame\"，实际 " + Show(r.Filter));
            r = Must("list Frame profiler", DebugActionRequest.Intent.List);
            Check(r.Filter == "Frame profiler", "filter 应保留内部空格，实际 " + Show(r.Filter));
            r = Must("list   a   b  ", DebugActionRequest.Intent.List);
            Check(r.Filter == "a b", "filter 应折叠多空格，实际 " + Show(r.Filter));

            Must("refresh", DebugActionRequest.Intent.Refresh);
            Must("Refresh", DebugActionRequest.Intent.Refresh);

            string why;
            DebugActionRequest bad;
            Check(!DebugActionRequest.TryParse("refresh now", out bad, out why), "refresh 带参数应报错");
            Check(why.Length > 0, "报错必须给可读原因");
        }

        private static void ExplicitSeparatorCases()
        {
            DebugActionRequest r = Must(@"Actions\Foo -- tile 42", DebugActionRequest.Intent.Execute);
            Check(r.Path == @"Actions\Foo", "路径应为 Actions\\Foo，实际 " + Show(r.Path));
            Check(r.ArgsRaw == "tile 42", "参数应为 \"tile 42\"，实际 " + Show(r.ArgsRaw));
            Check(r.ExplicitSeparator, "应标记 ExplicitSeparator");

            r = Must(@"Actions\Foo  --  5 5", DebugActionRequest.Intent.Execute);
            Check(r.Path == @"Actions\Foo" && r.ArgsRaw == "5 5" && r.ExplicitSeparator,
                "多余空格也应正确切分，实际 " + Show(r.Path) + " + " + Show(r.ArgsRaw));

            // 路径含空格是合法的(如原版 "Frame profiler"),显式分隔是它的正规写法。
            r = Must("Frame profiler -- tile 3", DebugActionRequest.Intent.Execute);
            Check(r.Path == "Frame profiler" && r.ArgsRaw == "tile 3", "含空格路径 + 显式分隔");

            // 路径本身含 "--" 段?以第一个独立 "--" 为界(文档化)。
            r = Must(@"A\B -- C -- D", DebugActionRequest.Intent.Execute);
            Check(r.Path == @"A\B" && r.ArgsRaw == "C -- D", "以第一个独立 \"--\" 为界");
        }

        private static void PositionalCases()
        {
            DebugActionRequest r = Must(@"Actions\General\Do Thing", DebugActionRequest.Intent.Execute);
            Check(r.Path == @"Actions\General\Do Thing", "无参数时整串都是路径，实际 " + Show(r.Path));
            Check(r.ArgsRaw == "", "无参数时 ArgsRaw 应为空串");
            Check(!r.ExplicitSeparator, "位置式不应标记 ExplicitSeparator");

            r = Must("Frame profiler", DebugActionRequest.Intent.Execute);
            Check(r.Path == "Frame profiler" && r.ArgsRaw == "", "末段非数字 ⇒ 整串是路径");

            r = Must(@"Actions\Foo 5 5", DebugActionRequest.Intent.Execute);
            Check(r.Path == @"Actions\Foo" && r.ArgsRaw == "5 5", "两个尾随整数 = 地图格");

            r = Must(@"Actions\Foo 42", DebugActionRequest.Intent.Execute);
            Check(r.Path == @"Actions\Foo" && r.ArgsRaw == "42", "一个尾随整数 = 待按 actionType 解释");

            r = Must(@"Actions\Foo tile 42", DebugActionRequest.Intent.Execute);
            Check(r.Path == @"Actions\Foo" && r.ArgsRaw == "tile 42", "tile <n> 是两段参数");

            r = Must(@"Actions\Foo pawn 7", DebugActionRequest.Intent.Execute);
            Check(r.Path == @"Actions\Foo" && r.ArgsRaw == "pawn 7", "pawn <id> 是两段参数");

            r = Must(@"Actions\Foo pawnId 7", DebugActionRequest.Intent.Execute);
            Check(r.Path == @"Actions\Foo" && r.ArgsRaw == "pawnId 7", "pawnId 别名");

            r = Must(@"Actions\Foo map 3 4", DebugActionRequest.Intent.Execute);
            Check(r.Path == @"Actions\Foo" && r.ArgsRaw == "map 3 4", "map <x> <z> 是三段参数");

            // 文档化的保守切分:末段含数字的路径在这里会被切错,游戏侧会「整串当路径」回落。
            r = Must(@"Spawn 1", DebugActionRequest.Intent.Execute);
            Check(r.Path == "Spawn" && r.ArgsRaw == "1", "末段数字被保守地当参数(游戏侧有回落)");
        }

        private static void BoundCases()
        {
            Check(MustFail(@"Actions\Foo --"), "\"--\" 后缺参数应报错");
            Check(MustFail("-- 5 5"), "\"--\" 之前缺路径应报错");

            // 整串都是参数 ⇒ 位置式切分放弃,整串当路径(由游戏侧报「节点不存在」)。
            DebugActionRequest r = Must("map 5 5", DebugActionRequest.Intent.Execute);
            Check(r.Path == "map 5 5" && r.ArgsRaw == "", "全是参数时整串当路径，实际 " + Show(r.Path));

            r = Must("5 5", DebugActionRequest.Intent.Execute);
            Check(r.Path == "5 5" && r.ArgsRaw == "", "全是整数时整串当路径");
        }

        private static void ArgsCases()
        {
            DebugActionRequest.Args a = MustArgs("");
            Check(!a.HasAny, "空参数应 HasAny=false");

            a = MustArgs("5 5");
            Check(a.HasMapCell && a.MapX == 5 && a.MapZ == 5 && !a.HasSingleInt && !a.HasTile && !a.HasPawn, "位置式两个整数 = 地图格");

            a = MustArgs("42");
            Check(a.HasSingleInt && a.SingleInt == 42 && !a.HasMapCell, "位置式一个整数 = 待解释");

            a = MustArgs("tile 42");
            Check(a.HasTile && a.Tile == 42 && !a.HasSingleInt, "tile <n>");

            a = MustArgs("pawn 7");
            Check(a.HasPawn && a.PawnId == 7, "pawn <id>");

            a = MustArgs("pawnId 7");
            Check(a.HasPawn && a.PawnId == 7, "pawnId 别名");

            a = MustArgs("PAWN 7");
            Check(a.HasPawn && a.PawnId == 7, "关键词大小写不敏感");

            a = MustArgs("map 3 4");
            Check(a.HasMapCell && a.MapX == 3 && a.MapZ == 4, "map <x> <z>");

            a = MustArgs("-3 4");
            Check(a.HasMapCell && a.MapX == -3 && a.MapZ == 4, "允许负整数");
        }

        private static void ArgsErrorCases()
        {
            Check(MustArgsFail("5 5 5"), "位置式三个整数应报错");
            Check(MustArgsFail("abc"), "非整数非关键词应报错");
            Check(MustArgsFail("tile"), "tile 缺数字应报错");
            Check(MustArgsFail("tile x"), "tile 后非整数应报错");
            Check(MustArgsFail("pawn"), "pawn 缺数字应报错");
            Check(MustArgsFail("map 1"), "map 缺第二个坐标应报错");
            Check(MustArgsFail("tile 5 6"), "位置式与命名式混用应报错");
            Check(MustArgsFail("1 2 tile 3"), "命名式与位置式混用应报错");
            Check(MustArgsFail("tile 1 tile 2"), "重复指定应报错");

            string why;
            DebugActionRequest.Args a;
            DebugActionRequest.Args.TryParse("5 5 5", out a, out why);
            Check(why.IndexOf("最多两个整数", StringComparison.Ordinal) >= 0, "错误文案应说明上限，实际 " + Show(why));
        }

        private static void RegisterShapeCases()
        {
            if (!RegisterByReflection())
            {
                Console.WriteLine("DebugActionCommandTests: 跳过登记/派发断言(反射调 DebugActionCommand.Register 失败 —— "
                    + "本进程大概加载不到 Assembly-CSharp;纯语法断言已全部执行)");
                return;
            }

            SlashEntry e;
            Check(SlashRegistry.TryGet(Verb, out e), "应登记 verb " + Verb);
            if (e == null) return;

            Check(e.Kind == Verb, "Kind 应为 " + Verb + "，实际 " + Show(e.Kind));
            Check(e.Handler != null, "Handler 不得为 null(已登记必须已实现)");
            Check(e.Dangerous, "执行任意调试动作 = 有副作用 ⇒ Dangerous 应为 true");

            string argsPart, note;
            SlashRegistry.SplitUsage(e.Usage, out argsPart, out note);
            Check(argsPart.IndexOf("list", StringComparison.Ordinal) >= 0, "Usage 参数段应提到 list，实际 " + Show(argsPart));
            Check(argsPart.IndexOf('—') < 0, "Usage 的参数段不得含分隔符");
            Check(note.Length > 0, "Usage 必须带说明段");
            Check(e.Usage.IndexOf("node.Path", StringComparison.Ordinal) >= 0, "说明段应点明键是 node.Path");
        }

        private static void DispatchCases()
        {
            if (!RegisterByReflection()) return;

            SlashResult r;

            // ① provider 未注入:Bootstrap 还没 wire ⇒ 标准「能力未注册」行。
            SlashContext empty = new SlashContext();
            Check(SlashRegistry.TryDispatch("/debug-action", empty, out r), "是指令就必须返回 true");
            Check(r != null && r.Kind == "missing-capability", "provider 未注入应为 missing-capability，实际 " + (r == null ? "<null>" : r.Kind));
            Check(r != null && (string)r.Data["capability"] == Verb, "应点名能力 " + Verb);

            // ② provider 已注入:rest 必须原样(已 Trim、内部空格保留、反斜杠原样)透传,内核不加戏。
            string seen = null;
            SlashContext ctx = new SlashContext
            {
                DebugAction = delegate(string rest, SlashContext c) { seen = rest; return SlashResults.Ok(Verb); }
            };
            Check(SlashRegistry.TryDispatch(@"  /debug-action   Actions\Foo 5 5  ", ctx, out r), "带参派发应返回 true");
            Check(seen == @"Actions\Foo 5 5", "rest 应原样透传(反斜杠 + 内部空格)，实际 " + Show(seen));
            Check(r != null && r.Kind == Verb, "结果 Kind 应沿用 provider");

            Check(SlashRegistry.TryDispatch("/DEBUG-ACTION list", ctx, out r), "verb 大小写不敏感");
            Check(seen == "list", "大写 verb 的 rest 应为 \"list\"，实际 " + Show(seen));

            Check(SlashRegistry.TryDispatch("/debug-action", ctx, out r), "无参调用");
            Check(seen == "", "无参时 rest 应为空串，实际 " + Show(seen));
        }

        // ---------------- W7-fix1 新增:契约 v1.1 线程规则 + Data 键稳定性 ----------------

        private static void RefuseOffThreadCases()
        {
            Func<string, SlashContext, SlashResult> provide = ResolveProvide();
            if (provide == null) { Console.WriteLine("DebugActionCommandTests: 跳过线程规则断言(取不到 Provide)"); return; }

            // 舰队规则:非游戏线程 + 无 Executor ⇒ 明确拒绝,绝不直读 Unity/Verse。
            // 这两条都不需要游戏环境:拒绝发生在任何 Verse 访问之前。
            var ctx = new SlashContext { IsGameThread = false, Executor = null, Channel = "pipe" };

            SlashResult r = provide("list", ctx);
            Check(r != null && r.Kind == Verb, "拒绝路径 Kind 应为 " + Verb);
            Check(r.Data != null && (bool)r.Data["ok"] == false, "拒绝路径 ok 应为 false");
            Check(r.Data != null && (string)r.Data["mode"] == "list", "公共核心键 mode 应自报 list");
            Check(r.Data != null && r.Data.ContainsKey("unsupportedReason") && (string)r.Data["unsupportedReason"] != null,
                "拒绝路径必须有 unsupportedReason(未执行必有原因)");
            Check(r.Data != null && ((string)r.Data["error"]).IndexOf("拒绝执行", StringComparison.Ordinal) >= 0,
                "拒绝文案应逐字说明「拒绝执行」，实际 " + Show(r.Data == null ? null : (string)r.Data["error"]));

            r = provide(@"Actions\Foo", ctx);   // 执行模式同一条规则(与 mode 无关)
            Check(r != null && r.Data != null && (string)r.Data["mode"] == "execute", "执行模式的 mode 应为 execute");
            Check(r != null && r.Data != null && (bool)r.Data["ok"] == false, "执行模式在无 Executor 的非游戏线程上也要拒绝");
        }

        private static void MarshalCases()
        {
            Func<string, SlashContext, SlashResult> provide = ResolveProvide();
            if (provide == null) { Console.WriteLine("DebugActionCommandTests: 跳过 marshal 断言(取不到 Provide)"); return; }

            var exec = new RoslynProbe.Repl.Executor();
            var ctx = new SlashContext { IsGameThread = false, Executor = exec, Channel = "pipe" };

            SlashResult got = null;
            Exception thrown = null;
            var th = new Thread(new ThreadStart(delegate
            {
                try { got = provide("list", ctx); }
                catch (Exception ex) { thrown = ex; }
            }));
            th.IsBackground = true;
            th.Start();

            // 本线程冒充「游戏线程」把队列抽干(生产环境由 BootstrapBehaviour.Update 做)。
            for (int i = 0; i < 400 && th.IsAlive; i++) { exec.Tick(4); Thread.Sleep(2); }
            th.Join(200);

            Check(!th.IsAlive, "marshal 路径应在预算内完成(Enqueue → Tick 履约 → TCS 回传)");
            Check(thrown == null, "marshal 路径不得抛异常，实际 " + (thrown == null ? "无" : thrown.GetType().Name + ": " + thrown.Message));
            Check(got != null && got.Kind == Verb, "marshal 路径应回 " + Verb + " 结果");
            if (got == null || got.Data == null) { Check(false, "marshal 结果必须有 Data"); return; }

            Check((string)got.Data["mode"] == "list", "mode 应为 list，实际 " + Show((string)got.Data["mode"]));
            if ((bool)got.Data["ok"])
            {
                // 无游戏进程里 DefDatabase 为空 ⇒ TrySetupNodeGraph 也能成(零 tab),于是得到一棵**空树**:
                // 这恰好证明「失败不伪装成功、成功也不伪装失败」两侧都走的是真路径。
                Check(got.Data.ContainsKey("count") && got.Data.ContainsKey("nodes") && got.Data.ContainsKey("truncated"),
                    "list 成功形状应含 count/nodes/truncated");
                var list = got.Data["nodes"] as List<Dictionary<string, object>>;
                Check(list != null, "nodes 应为 List<Dictionary<string,object>>");
                Check(list != null && (int)got.Data["count"] == list.Count, "count 必须等于 nodes 条数(审查 N4)");
                Check((bool)got.Data["walkDone"], "空索引应立即报走查完成");
            }
            else
            {
                Check(got.Data.ContainsKey("unsupportedReason") && (string)got.Data["unsupportedReason"] != null,
                    "list 失败路径也必须有 unsupportedReason(CoreError 形状)");
            }
        }

        private static void ExecuteShapeCases()
        {
            Func<string, SlashContext, SlashResult> provide = ResolveProvide();
            if (provide == null) { Console.WriteLine("DebugActionCommandTests: 跳过 Data 形状断言(取不到 Provide)"); return; }

            // 纯语法错误:@\" -- \" 后缺参数 ⇒ 在路由之前就返回(不必游戏环境),但必须是**执行形状**的完整键集。
            var ctx = new SlashContext { IsGameThread = false, Executor = null, Channel = "pipe" };
            SlashResult r = provide(@"Actions\Foo --", ctx);
            Check(r != null && r.Kind == Verb, "语法错误结果 Kind 应为 " + Verb);
            Check(r.Data != null, "语法错误结果必须有 Data");
            if (r.Data == null) return;

            string[] keys =
            {
                "ok", "mode", "path", "requestedPath", "label", "actionType",
                "visible", "active", "visibleError", "activeError",
                "executed", "unsupportedReason", "error", "patchLeak",
                "mapX", "mapZ", "tile", "pawnId", "singleInt",
                "resolved", "foldedArgs", "elapsedMs"
            };
            foreach (string k in keys)
                Check(r.Data.ContainsKey(k), "执行形状缺键(审查 N3): " + k);

            Check((string)r.Data["mode"] == "execute", "mode 应为 execute");
            Check((bool)r.Data["ok"] == false && (bool)r.Data["executed"] == false, "语法错误:ok/executed 都应为 false");
            Check((string)r.Data["unsupportedReason"] != null && (string)r.Data["error"] != null,
                "语法错误时 unsupportedReason 与 error 都应有值");
            Check((bool)r.Data["patchLeak"] == false, "语法错误不应报 patchLeak");
            Check(r.Data["mapX"] == null && r.Data["pawnId"] == null, "未解析参数时这些键应为 null(键仍在)");
        }

        // ---------------- W7-fix2:唯一出口 StableKeys(N3-a + S1)----------------

        private static void StableKeysCases()
        {
            Type t = typeof(SlashRegistry).Assembly.GetType("RoslynProbe.DebugActionCommand", false);
            MethodInfo stable = ResolveMember(t, "StableKeys", new Type[] { typeof(SlashResult), typeof(string) });
            MethodInfo canonical = ResolveMember(t, "CanonicalKeyNames", Type.EmptyTypes);
            if (stable == null || canonical == null)
            {
                Console.WriteLine("DebugActionCommandTests: 跳过 StableKeys 断言(取不到方法 —— 大概加载不到 Assembly-CSharp)");
                return;
            }

            string[] keys = (string[])canonical.Invoke(null, null);
            Check(keys != null && keys.Length == 22, "canonical 键应为 22 个，实际 " + (keys == null ? -1 : keys.Length));
            if (keys == null) return;

            // (a) execute + 未执行 + 既无原因也无 error ⇒ **S1 守卫必须补录**(删掉守卫 ⇒ 这条红)
            SlashResult r = (SlashResult)stable.Invoke(null, new object[] { NewResult(false, false, null, null), "execute" });
            Check(Key(r, "unsupportedReason") != null,
                "S1 守卫:execute 且 executed=false 却没有任何原因时,StableKeys 必须补录 unsupportedReason");
            Check(!string.IsNullOrEmpty(Key(r, "unsupportedReason") as string), "补录的原因不能是空串");
            CheckAllCanonical(keys, r, "守卫探针(无 error)");

            // (b) 有 error ⇒ 用原文补录
            r = (SlashResult)stable.Invoke(null, new object[] { NewResult(false, false, null, "boom"), "execute" });
            string reason = Key(r, "unsupportedReason") as string;
            Check(reason != null && reason.IndexOf("执行失败: boom", StringComparison.Ordinal) >= 0,
                "应优先用 error 原文补录，实际 " + Show(reason));

            // (c) 执行成功 ⇒ 不补录,且已有键不被覆盖
            r = (SlashResult)stable.Invoke(null, new object[] { NewResult(true, true, null, null), "execute" });
            Check(Key(r, "unsupportedReason") == null, "executed=true 时不得补录原因");
            Check(Key(r, "ok") is bool ok1 && ok1, "已有 ok=true 不得被覆盖");
            Check(Key(r, "executed") is bool ex1 && ex1, "已有 executed=true 不得被覆盖");

            // (d) 已有键不覆盖:结果自报 list ⇒ 守卫不触发(按结果自报的 mode 判定,自洽)
            r = (SlashResult)stable.Invoke(null, new object[] { NewResult(null, null, "list", null), "execute" });
            Check((string)Key(r, "mode") == "list", "已有 mode 不得被覆盖，实际 " + Show((string)Key(r, "mode")));
            Check(Key(r, "unsupportedReason") == null, "结果自报 list 时不应触发 execute 守卫");
            CheckAllCanonical(keys, r, "自报 list 探针");

            // (e) 没自报 mode ⇒ 用参数补,并按它判守卫
            r = (SlashResult)stable.Invoke(null, new object[] { NewResult(false, false, null, null), "list" });
            Check((string)Key(r, "mode") == "list", "缺 mode 时应用参数补 mode=list");
            Check(Key(r, "unsupportedReason") == null, "list 模式不得触发 S1 守卫(executed=false 只是没执行)");
        }

        private static void SevenProbeShapeCases()
        {
            Func<string, SlashContext, SlashResult> provide = ResolveProvide();
            Type t = typeof(SlashRegistry).Assembly.GetType("RoslynProbe.DebugActionCommand", false);
            MethodInfo canonical = ResolveMember(t, "CanonicalKeyNames", Type.EmptyTypes);
            if (provide == null || canonical == null)
            {
                Console.WriteLine("DebugActionCommandTests: 跳过七组探针断言(取不到 Provide/CanonicalKeyNames)");
                return;
            }
            string[] keys = (string[])canonical.Invoke(null, null);

            var off = new SlashContext { IsGameThread = false, Executor = null, Channel = "pipe" };
            var inline = new SlashContext { IsGameThread = true };

            ProbeShape(keys, provide(@"Actions\Foo --", off), "execute/语法错误");
            ProbeShape(keys, provide(@"Actions\Foo", off), "execute/路由拒绝");
            ProbeShape(keys, ProvideViaMarshal(provide, @"Actions\Foo"), "execute/marshal 回传");
            ProbeShape(keys, provide("list", off), "list/路由拒绝");
            ProbeShape(keys, provide("list", inline), "list/inline");
            ProbeShape(keys, provide("refresh", inline), "refresh/inline");
            ProbeShape(keys, provide("", off), "usage");

            // W7-fix3:真实 list 调用上,Lines 的汇总必须与 Data 对同一个量给同一个值。
            SlashResult listOk = provide("list", inline);
            if (listOk != null && listOk.Lines.Count > 0 && listOk.Data != null && listOk.Data.ContainsKey("truncated"))
            {
                string head = string.Join("\n", listOk.Lines.GetRange(0, Math.Min(3, listOk.Lines.Count)).ToArray());
                bool tv = (bool)listOk.Data["truncated"];
                Check(head.IndexOf("truncated=" + (tv ? "true" : "false"), StringComparison.Ordinal) >= 0,
                    "真实 list 调用的 Lines 汇总必须与 Data.truncated 一致，实际 " + Show(head));
                Check(head.IndexOf("聚合/渲染/打印 " + listOk.Data["aggregateMs"] + " ms", StringComparison.Ordinal) >= 0,
                    "真实 list 调用的 Lines 汇总必须印与 Data.aggregateMs 相同的耗时，实际 " + Show(head));
                Check(head.IndexOf("回传 " + listOk.Data["count"] + " 条", StringComparison.Ordinal) >= 0,
                    "Lines 的条数必须与 Data.count 一致，实际 " + Show(head));
            }
            else
            {
                Check(false, "真实 list 调用应返回带 truncated 的 Data");
            }
        }

        private static void ProbeShape(string[] keys, SlashResult r, string label)
        {
            CheckAllCanonical(keys, r, "探针[" + label + "]");
            if (r == null || r.Data == null) return;
            Check(r.Data.ContainsKey("mode"), "探针[" + label + "] 必须自报 mode");
            Check(r.Data.ContainsKey("executed"), "探针[" + label + "] 必须含 executed(消费方按它判成败)");
        }

        private static SlashResult ProvideViaMarshal(Func<string, SlashContext, SlashResult> provide, string rest)
        {
            var exec = new RoslynProbe.Repl.Executor();
            var ctx = new SlashContext { IsGameThread = false, Executor = exec, Channel = "pipe" };
            SlashResult got = null;
            var th = new Thread(new ThreadStart(delegate { got = provide(rest, ctx); }));
            th.IsBackground = true;
            th.Start();
            for (int i = 0; i < 400 && th.IsAlive; i++) { exec.Tick(4); Thread.Sleep(2); }
            th.Join(200);
            return got;
        }

        private static SlashResult NewResult(bool? ok, bool? executed, string mode, string error)
        {
            var d = new Dictionary<string, object>();
            if (ok.HasValue) d["ok"] = ok.Value;
            if (executed.HasValue) d["executed"] = executed.Value;
            if (mode != null) d["mode"] = mode;
            if (error != null) d["error"] = error;
            return new SlashResult { Kind = Verb, Data = d };
        }

        private static object Key(SlashResult r, string key)
        {
            if (r == null || r.Data == null || !r.Data.ContainsKey(key)) return null;
            return r.Data[key];
        }

        private static void CheckAllCanonical(string[] keys, SlashResult r, string label)
        {
            if (r == null || r.Data == null) { Check(false, label + ":结果或 Data 为 null"); return; }
            foreach (string k in keys)
                Check(r.Data.ContainsKey(k), label + " 缺 canonical 键: " + k);
        }

        private static MethodInfo ResolveMember(Type t, string name, Type[] args)
        {
            try
            {
                if (t == null) return null;
                return t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, args, null);
            }
            catch (Exception) { return null; }
        }

        // ---------------- W7-fix3:Lines 与 Data 必须对同一个事实说同一句话 ----------------

        private static void ListFactsCases()
        {
            Type t = typeof(SlashRegistry).Assembly.GetType("RoslynProbe.DebugActionCommand", false);
            Type factsType = t == null ? null : t.GetNestedType("ListFacts", BindingFlags.Public);
            MethodInfo summary = ResolveMember(t, "ListSummaryLine", factsType == null ? null : new Type[] { factsType });
            PropertyInfo truncatedProp = factsType == null ? null : factsType.GetProperty("Truncated");
            if (factsType == null || summary == null || truncatedProp == null)
            {
                Console.WriteLine("DebugActionCommandTests: 跳过 ListFacts 断言(取不到 ListFacts/ListSummaryLine)");
                return;
            }

            // ① **仅打印被预算截断** —— W7-fix3 的争议路径:
            //    扫描走完了(Scanned==Indexed)、没有 capping、没有 remaining、没有探针截断,只有打印被截断。
            object f = NewFacts(factsType, 5, 5, 1, 0, false, false, true, false, 1, 2, 77);
            string line = (string)summary.Invoke(null, new object[] { f });
            Check((bool)truncatedProp.GetValue(f, null), "仅打印被截断时 ListFacts.Truncated 必须为 true");
            Check(line.IndexOf("truncated=true", StringComparison.Ordinal) >= 0,
                "仅打印被截断时汇总行必须印 truncated=true(不能与 Data 说反话)，实际 " + Show(line));
            Check(line.IndexOf("打印被预算截断(已打印 1/5 条)", StringComparison.Ordinal) >= 0,
                "应点名是**打印**段被截断，实际 " + Show(line));
            Check(line.IndexOf("扫描被预算截断", StringComparison.Ordinal) < 0,
                "不得把打印截断说成扫描截断");
            Check(line.IndexOf("聚合/渲染/打印 77 ms", StringComparison.Ordinal) >= 0,
                "汇总行必须用终值(含打印)的聚合耗时，实际 " + Show(line));

            // ② 仅扫描被预算截断
            f = NewFacts(factsType, 8, 0, 0, 0, false, true, false, false, 1, 2, 51);
            line = (string)summary.Invoke(null, new object[] { f });
            Check((bool)truncatedProp.GetValue(f, null), "仅扫描被截断时 Truncated 必须为 true");
            Check(line.IndexOf("truncated=true", StringComparison.Ordinal) >= 0 && line.IndexOf("扫描被预算截断", StringComparison.Ordinal) >= 0,
                "扫描被截断时行里要有 truncated=true 与「扫描被预算截断」，实际 " + Show(line));
            Check(line.IndexOf("打印被预算截断", StringComparison.Ordinal) < 0, "不得把扫描截断说成打印截断");

            // ③ 仅探针被预算截断
            f = NewFacts(factsType, 9, 2, 2, 0, false, false, false, true, 50, 0, 3);
            line = (string)summary.Invoke(null, new object[] { f });
            Check((bool)truncatedProp.GetValue(f, null) && line.IndexOf("探针被预算截断", StringComparison.Ordinal) >= 0,
                "仅探针被截断时也必须是 truncated=true 且点名探针,实际 " + Show(line));

            // ④ 干净:没有任何截断事实
            f = NewFacts(factsType, 9, 9, 9, 0, false, false, false, false, 0, 0, 0);
            line = (string)summary.Invoke(null, new object[] { f });
            Check(!(bool)truncatedProp.GetValue(f, null), "干净情形 Truncated 必须为 false");
            Check(line.IndexOf("truncated=false", StringComparison.Ordinal) >= 0, "干净情形行里应为 truncated=false，实际 " + Show(line));
            Check(line.IndexOf("被预算截断", StringComparison.Ordinal) < 0, "干净情形不得出现任何「被预算截断」");

            // ⑤ 走查没走完 / ⑥ 回传被上限截断 —— 两种「不全」也要与 Data 同值
            f = NewFacts(factsType, 8, 5, 0, 5, false, false, false, false, 0, 9, 4);
            line = (string)summary.Invoke(null, new object[] { f });
            Check((bool)truncatedProp.GetValue(f, null) && line.IndexOf("truncated=true", StringComparison.Ordinal) >= 0
                && line.IndexOf("待走查", StringComparison.Ordinal) >= 0, "remaining>0 时 truncated=true 且点明待走查");
            f = NewFacts(factsType, 100000, 0, 0, 0, true, false, false, false, 0, 0, 0);
            line = (string)summary.Invoke(null, new object[] { f });
            Check((bool)truncatedProp.GetValue(f, null) && line.IndexOf("truncated=true", StringComparison.Ordinal) >= 0
                && line.IndexOf("上限", StringComparison.Ordinal) >= 0, "capped 时 truncated=true 且点明上限");

            // ⑦ 同一个量在两处不能给不同值:把行里的 truncated= 读回来,与 ListFacts.Truncated 对比
            CheckTruncatedTokenAgrees(factsType, summary, truncatedProp, NewFacts(factsType, 5, 5, 1, 0, false, false, true, false, 1, 2, 77), "仅打印");
            CheckTruncatedTokenAgrees(factsType, summary, truncatedProp, NewFacts(factsType, 8, 0, 0, 0, false, true, false, false, 1, 2, 51), "仅扫描");
            CheckTruncatedTokenAgrees(factsType, summary, truncatedProp, NewFacts(factsType, 9, 9, 9, 0, false, false, false, false, 0, 0, 0), "干净");
            CheckTruncatedTokenAgrees(factsType, summary, truncatedProp, NewFacts(factsType, 8, 5, 0, 5, false, false, false, false, 0, 9, 4), "走查未完");
        }

        private static void CheckTruncatedTokenAgrees(Type factsType, MethodInfo summary, PropertyInfo truncatedProp, object f, string label)
        {
            string line = (string)summary.Invoke(null, new object[] { f });
            bool fromFacts = (bool)truncatedProp.GetValue(f, null);
            int i = line.IndexOf("truncated=", StringComparison.Ordinal);
            Check(i >= 0, "[" + label + "] 汇总行里必须有 truncated=，实际 " + Show(line));
            if (i < 0) return;
            int j = i + "truncated=".Length;
            int k = j;
            while (k < line.Length && char.IsLetter(line[k])) k++;
            string token = line.Substring(j, k - j);
            Check(token == (fromFacts ? "true" : "false"),
                "[" + label + "] 行里的 truncated(" + token + ") 必须等于 ListFacts.Truncated(" + (fromFacts ? "true" : "false") + ")");
        }

        /// <summary>反射造一个 ListFacts(公开嵌套类型,纯数据,不碰 Verse)。</summary>
        private static object NewFacts(Type factsType, int indexed, int returned, int printed, int remaining,
            bool capped, bool scanHit, bool printHit, bool probeHit, long probeMs, long walkMs, long aggMs)
        {
            object f = Activator.CreateInstance(factsType);
            SetField(factsType, f, "Indexed", indexed);
            SetField(factsType, f, "Scanned", scanHit ? Math.Min(indexed, 1) : indexed);
            SetField(factsType, f, "Matched", returned + (scanHit ? 1 : 0));
            SetField(factsType, f, "Returned", returned);
            SetField(factsType, f, "Printed", printed);
            SetField(factsType, f, "Remaining", remaining);
            SetField(factsType, f, "DynamicMenus", 0);
            SetField(factsType, f, "ActiveFailures", 0);
            SetField(factsType, f, "MaterializedUnindexed", 0);
            SetField(factsType, f, "NodesTotal", returned);
            SetField(factsType, f, "MaxNodesReturned", 5000);
            SetField(factsType, f, "MaxLinesPrinted", 200);
            SetField(factsType, f, "Capped", capped);
            SetField(factsType, f, "ScanBudgetHit", scanHit);
            SetField(factsType, f, "PrintBudgetHit", printHit);
            SetField(factsType, f, "ProbeBudgetHit", probeHit);
            SetField(factsType, f, "BuildMs", -1L);
            SetField(factsType, f, "ProbeMs", probeMs);
            SetField(factsType, f, "WalkMs", walkMs);
            SetField(factsType, f, "AggregateMs", aggMs);
            return f;
        }

        private static void SetField(Type t, object o, string name, object value)
        {
            FieldInfo fi = t.GetField(name);
            if (fi != null) fi.SetValue(o, value);
        }

        // ---------------- 辅助 ----------------

        /// <summary>
        /// 反射取 DebugActionCommand.Provide(契约 v1.1 §3.5 的唯一注入方法名)成委托。
        /// 用反射而不是静态引用:DebugActionCommand 的静态字段含 Verse 值类型,加载它需要 Assembly-CSharp;
        /// 取不到就返回 null,调用方跳过并说明,不把环境问题记成失败。
        /// </summary>
        private static Func<string, SlashContext, SlashResult> ResolveProvide()
        {
            try
            {
                Type t = typeof(SlashRegistry).Assembly.GetType("RoslynProbe.DebugActionCommand", false);
                if (t == null) return null;
                MethodInfo m = t.GetMethod("Provide", BindingFlags.Public | BindingFlags.Static, null,
                    new Type[] { typeof(string), typeof(SlashContext) }, null);
                if (m == null || m.ReturnType != typeof(SlashResult)) return null;
                return (Func<string, SlashContext, SlashResult>)Delegate.CreateDelegate(
                    typeof(Func<string, SlashContext, SlashResult>), m);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool RegisterByReflection()
        {
            try
            {
                Type t = typeof(SlashRegistry).Assembly.GetType("RoslynProbe.DebugActionCommand", false);
                if (t == null) return false;
                MethodInfo m = t.GetMethod("Register", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m == null || m.ReturnType != typeof(void)) return false;
                SlashRegistry.ResetForTests();
                m.Invoke(null, null);
                return SlashRegistry.Count > 0;
            }
            catch (Exception)
            {
                try { SlashRegistry.ResetForTests(); } catch (Exception) { }
                return false;
            }
        }

        private static DebugActionRequest Must(string rest, DebugActionRequest.Intent want)
        {
            string why;
            DebugActionRequest r;
            if (!DebugActionRequest.TryParse(rest, out r, out why))
            {
                checks++;
                Failures.Add("[" + currentCase + "] TryParse(" + Show(rest) + ") 意外失败: " + Show(why));
                return new DebugActionRequest();
            }
            checks++;
            if (r.Mode != want)
                Failures.Add("[" + currentCase + "] TryParse(" + Show(rest) + ") 模式应为 " + want + "，实际 " + r.Mode);
            return r;
        }

        private static bool MustFail(string rest)
        {
            string why;
            DebugActionRequest r;
            return !DebugActionRequest.TryParse(rest, out r, out why);
        }

        private static DebugActionRequest.Args MustArgs(string raw)
        {
            string why;
            DebugActionRequest.Args a;
            if (!DebugActionRequest.Args.TryParse(raw, out a, out why))
            {
                checks++;
                Failures.Add("[" + currentCase + "] Args.TryParse(" + Show(raw) + ") 意外失败: " + Show(why));
                return new DebugActionRequest.Args();
            }
            checks++;
            return a;
        }

        private static bool MustArgsFail(string raw)
        {
            string why;
            DebugActionRequest.Args a;
            return !DebugActionRequest.Args.TryParse(raw, out a, out why);
        }

        private static void Case(string name, Action body)
        {
            currentCase = name;
            try { body(); }
            catch (Exception ex)
            {
                checks++;
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Failures.Add("[" + name + "] 用例本身抛出: " + inner.GetType().Name + ": " + inner.Message
                    + (inner.StackTrace == null ? "" : "  @ " + FirstFrame(inner.StackTrace)));
            }
        }

        private static void Check(bool condition, string message)
        {
            checks++;
            if (!condition) Failures.Add("[" + currentCase + "] " + message);
        }

        private static string Show(string s) { return s == null ? "<null>" : "\"" + s + "\""; }

        private static string FirstFrame(string stackTrace)
        {
            int nl = stackTrace.IndexOf('\n');
            return nl < 0 ? stackTrace : stackTrace.Substring(0, nl);
        }
    }
}
