// RoslynProbe —— /hook 的纯逻辑单测（零依赖，约定见 tests/SlashHarness/SlashRegistryTests.cs）。
//
// 覆盖：
//   1. 登记形状：verb/kind/dangerous/handler 非 null、Usage 带 “——”。
//   2. 内核半：语法错误与用法页在**不碰能力**的前提下给出（ctx.Hook 不被调用）；
//      合法请求才转 ctx.CallHook（未注册 → missing-capability）。
//   3. 目标语法：程序集前缀 / :: 与 . 两种成员写法 / 参数表 / 构造函数 / 非法输入。
//   4. 目标解析（对 BCL 反射，不需要游戏）：命中重载、类型级目标、未知类型/成员报错。
//   5. list 渲染（合成补丁条目）：分组、按 kind 计数、owner/type 汇总、上限截断、纯逻辑可断言。
//   6. check 渲染（合成条目 + 真方法）：层数/补丁行/Data。
//   7. ReJIT 探测：取不到就写 reason、绝不抛（普通 CoreCLR 下必然 available=false）。
//   8. provider 绝不抛：0Harmony 不可用（本校验工程的输出目录不带 0Harmony.dll）也是一行错误。
//   9. 【fix1/B2】只挂 inner finalizer（3.0.0 才有的第 7 类集合）的方法必须进 list、且 check 不误报“未打补丁”。
//  10. 【fix1/B1】ReJIT：token/集合读不出时 updated 保持 null 并写 reason，汇总口径算“不可判定”而不是“确定没动过”。
//  10b.【fix3/审查(b)】强情形补测：**已打补丁 + updated=null**（Layers>0）也必须算“不可判定”，且汇总行不得
//      谎报“未发现被 MethodBodyUpdater 动过的方法”—— 这是 fix1(B1) 真正要防的形态（变异 m4 回退口径时本项会红）。
//  10c.【fix4/审查】上面两条“汇总行”断言必须**限定到前缀“合计:”的那一行**：逐方法明细行本身就是
//      “ReJIT: 不可判定 —— <reason>”，扫全量行会让断言在 m4 回退下假绿（现已改为只扫汇总行 + 断言汇总行存在）。
//  11. 【fix1/B2】ReadOptionalCollection 的守卫：成员不存在 → null（无错）、非集合 → null+error、owner=null → null。
//
// 为什么不能直接断言 /hook list 的成功路径：单测不在游戏进程里，0Harmony.dll 载不进来。
// 所以 Harmony 侧的断言都放在“桥接失败必须收敛成一行错误”这一条；成功路径只在真机上成立。

using System;
using System.Collections.Generic;
using System.Reflection;
using RoslynProbe;
using RoslynProbe.Repl;

namespace RoslynProbe.Tests
{
    public static class HookCommandTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";
        private static int checks;

        public static int Run()
        {
            Failures.Clear();
            checks = 0;
            currentCase = "(none)";

            Case("登记形状（verb/kind/dangerous/usage/handler）", RegisterShapeCases);
            Case("内核半：语法错误不碰能力（ctx.Hook 不被调用）", KernelHalfCases);
            Case("内核半：合法请求才转 ctx.CallHook（未注册 → missing-capability）", CapabilityForwardCases);
            Case("目标语法解析（程序集前缀 / :: / . / 参数表 / 非法）", TargetSyntaxCases);
            Case("目标解析（BCL 反射：重载 / 类型级 / 未知）", ResolveTargetCases);
            Case("list 渲染（合成条目：分组 / kind 计数 / 汇总 / 截断）", BuildListCases);
            Case("check 渲染（合成条目 + 真方法）", BuildCheckCases);
            Case("ReJIT 探测：取不到写 reason、绝不抛", ProbeRejitCases);
            Case("【fix1】inner finalizer（第 7 类集合）进 list / 计入层数 / 可选集合守卫", InnerFinalizerCases);
            Case("provider 绝不抛（0Harmony 不可用 = 一行错误）", ProvideCases);
            Case("通配匹配（程序集名前缀）", WildcardCases);

            // 收尾：还原真实表，避免影响同进程其它写手的测试（并发写手写坏别人的文件时也不连坐）。
            try
            {
                SlashRegistry.ResetForTests();
                SlashRegistry.RegisterAll();
            }
            catch (Exception ex)
            {
                checks++;
                Failures.Add("[收尾 RegisterAll] 还原真实表失败（可能是并发写手的文件问题，不是 /hook 的）: "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine("HookCommandTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- 用例 ----------------

        private static void RegisterShapeCases()
        {
            SlashRegistry.ResetForTests();
            HookCommand.Register();

            SlashEntry entry;
            Check(SlashRegistry.TryGet("hook", out entry), "登记后 /hook 应能查到");
            Check(entry != null && entry.Verb == "hook", "verb 应为 hook（Register 会小写化）");
            Check(entry != null && entry.Kind == "hook", "Kind 应为 hook");
            Check(entry != null && entry.Dangerous, "off 有副作用 ⇒ Dangerous 应为 true");
            Check(entry != null && entry.Handler != null, "Handler 不应为 null");
            Check(entry != null && entry.Usage != null && entry.Usage.IndexOf("——", StringComparison.Ordinal) >= 0,
                "Usage 应含 “——” 分隔符");
            Check(entry != null && entry.Usage.IndexOf("off <owner>", StringComparison.Ordinal) >= 0,
                "Usage 应写明 off <owner>");

            string args, note;
            SlashRegistry.SplitUsage(entry.Usage, out args, out note);
            Check(args.IndexOf("list", StringComparison.Ordinal) >= 0, "Usage 参数段应含 list");
            Check(note.Length > 0, "Usage 说明段不应为空");
        }

        private static void KernelHalfCases()
        {
            SlashRegistry.ResetForTests();
            HookCommand.Register();

            bool called = false;
            var ctx = new SlashContext();
            ctx.Hook = delegate(string rest, SlashContext c) { called = true; return SlashResults.Ok("hook"); };

            SlashResult r;
            Check(SlashRegistry.TryDispatch("/hook", ctx, out r), "是指令 → 必定吞下");
            Check(r != null && r.Kind == "hook" && r.Data != null && (bool)r.Data["ok"] == false,
                "无参应是用法/错误结果");
            Check(!called, "语法不合法时不应调用能力");

            Check(SlashRegistry.TryDispatch("/hook nope", ctx, out r), "未知子命令也是指令");
            Check(r != null && (bool)r.Data["ok"] == false, "未知子命令应是错误结果");
            Check(!called, "未知子命令不应调用能力");
            Check(r.Lines.Count > 0 && r.Lines[0].IndexOf("未知子命令", StringComparison.Ordinal) >= 0,
                "未知子命令的行应点名它");

            Check(SlashRegistry.TryDispatch("/hook off", ctx, out r), "off 缺 owner 也是指令");
            Check(r != null && !called, "off 缺 owner 不应调用能力");

            Check(SlashRegistry.TryDispatch("/hook off a b", ctx, out r), "off 带两个词应被拒");
            Check(r != null && !called && (bool)r.Data["ok"] == false, "owner 含空白应报错且不调能力");

            Check(SlashRegistry.TryDispatch("/hook list x", ctx, out r), "list 带非数字应被拒");
            Check(r != null && !called, "list 非数字上限不应调能力");

            HookCommand.Request req;
            SlashResult early;
            Check(!HookCommand.TryParse("", out req, out early) && early != null, "空 rest → 用法页");
            Check(HookCommand.TryParse("list", out req, out early) && req.Mode == "list", "list → list 模式");
            Check(req != null && req.Limit == 200, "list 默认上限 200");
            Check(HookCommand.TryParse("LIST 50", out req, out early) && req.Limit == 50, "子命令大小写不敏感 + 上限可调");
            Check(HookCommand.TryParse("list 0", out req, out early) == false, "上限 0 应被拒");
            Check(HookCommand.TryParse("check X.Y::Z", out req, out early) && req.Target == "X.Y::Z", "check 目标原样保留");
            Check(HookCommand.TryParse("check X.Y::Z( System.Int32 , System.String )", out req, out early)
                && req.Target.IndexOf("System.String", StringComparison.Ordinal) >= 0, "check 目标可带空格");
            Check(HookCommand.TryParse("off roslynprobe.leak", out req, out early) && req.Owner == "roslynprobe.leak",
                "off owner");
            Check(HookCommand.TryParse("off", out req, out early) == false, "off 缺 owner 应被拒");
        }

        private static void CapabilityForwardCases()
        {
            SlashRegistry.ResetForTests();
            HookCommand.Register();

            var empty = new SlashContext();
            SlashResult r;
            Check(SlashRegistry.TryDispatch("/hook list", empty, out r), "合法请求应被吞下");
            Check(r != null && r.Kind == "missing-capability", "未注入能力 → missing-capability（不抛）");
            Check(r.Data != null && (string)r.Data["capability"] == "hook", "missing-capability 应点名 hook");

            Check(SlashRegistry.TryDispatch("/hook off roslynprobe.leak", empty, out r)
                && r.Kind == "missing-capability", "off 未注入能力也是 missing-capability");
            Check(SlashRegistry.TryDispatch("/hook check X.Y::Z", empty, out r)
                && r.Kind == "missing-capability", "check 未注入能力也是 missing-capability");

            bool called = false;
            string gotArgs = null;
            var ctx = new SlashContext();
            ctx.Hook = delegate(string rest, SlashContext c)
            {
                called = true;
                gotArgs = rest;
                return SlashResults.Ok("hook");
            };
            Check(SlashRegistry.TryDispatch("  /hook   list  50  ", ctx, out r), "带空白/多空格的指令应被吞下");
            Check(called && gotArgs == "list  50", "rest 应已 Trim、内部空白原样保留（实得 " + (gotArgs == null ? "<null>" : gotArgs) + "）");
            Check(r != null && r.Kind == "hook", "能力结果原样返回");
        }

        private static void TargetSyntaxCases()
        {
            HookCommand.TargetSpec spec;
            SlashResult error;

            Check(!HookCommand.TryParseTarget("", out spec, out error), "空目标应被拒");
            Check(!HookCommand.TryParseTarget("   ", out spec, out error), "全空白目标应被拒");

            Check(HookCommand.TryParseTarget("Verse.DebugWindowsOpener::DrawButtons", out spec, out error), ":: 形式");
            Check(spec.TypeText == "Verse.DebugWindowsOpener" && spec.MemberText == "DrawButtons", ":: 拆分正确");
            Check(spec.AssemblyPattern == null, "无程序集前缀");

            Check(HookCommand.TryParseTarget("Verse.Pawn.Kill", out spec, out error), "点号形式");
            Check(spec.TypeText == "Verse.Pawn.Kill" && spec.MemberText == null,
                "点号形式不在语法层拆（留给反射按最长类型前缀拆）");

            Check(HookCommand.TryParseTarget("Assembly-CSharp!RimWorld.Pawn::Kill", out spec, out error), "程序集前缀");
            Check(spec.AssemblyPattern == "Assembly-CSharp" && spec.TypeText == "RimWorld.Pawn"
                && spec.MemberText == "Kill", "程序集前缀拆分正确");

            Check(HookCommand.TryParseTarget("RimWorld.Pawn::Kill(System.Int32,Verse.Pawn)", out spec, out error),
                "带参数表");
            Check(spec.MemberText == "Kill" && spec.Parameters.Count == 2
                && spec.Parameters[0] == "System.Int32" && spec.Parameters[1] == "Verse.Pawn", "参数表拆分正确");

            Check(HookCommand.TryParseTarget("RimWorld.Pawn", out spec, out error), "类型级目标");
            Check(spec.MemberText == null && spec.TypeText == "RimWorld.Pawn" && spec.Parameters.Count == 0,
                "类型级目标形状");

            Check(HookCommand.TryParseTarget("Foo!Bar", out spec, out error) && spec.AssemblyPattern == "Foo"
                && spec.TypeText == "Bar", "Foo!Bar 合法（程序集前缀 Foo）");
            Check(!HookCommand.TryParseTarget("A::", out spec, out error), "成员为空应被拒");
            Check(!HookCommand.TryParseTarget("!A", out spec, out error), "程序集前缀为空应被拒");
            Check(!HookCommand.TryParseTarget("A::B(1,2", out spec, out error), "参数表未闭合应被拒");
        }

        private static void ResolveTargetCases()
        {
            MethodBase trim = typeof(string).GetMethod("Trim", Type.EmptyTypes);
            Check(trim != null, "前置：string.Trim() 应存在");

            HookCommand.TargetSpec spec;
            SlashResult error;
            Check(HookCommand.TryParseTarget("System.String::Trim", out spec, out error), "解析目标语法");

            HookCommand.ResolvedTarget resolved;
            Check(HookCommand.ResolveTarget(spec, out resolved, out error), "应解析到 System.String::Trim");
            Check(resolved != null && !resolved.TypeOnly, "带成员 → 方法级目标");
            Check(resolved != null && resolved.Type != null && resolved.Type == typeof(string), "类型应是 System.String");
            Check(resolved != null && resolved.Methods.Count >= 1, "至少命中一个 Trim 重载");
            bool allTrim = true;
            foreach (MethodBase m in resolved.Methods) if (m.Name != "Trim") allTrim = false;
            Check(allTrim, "命中的都应叫 Trim");

            Check(HookCommand.TryParseTarget("System.String.Trim", out spec, out error), "点号写法");
            HookCommand.ResolvedTarget dotted;
            Check(HookCommand.ResolveTarget(spec, out dotted, out error), "点号写法应解析成功（最长类型前缀）");
            Check(dotted != null && dotted.Type == typeof(string) && dotted.Methods.Count >= 1,
                "点号写法应落到 System.String.Trim");

            Check(HookCommand.TryParseTarget("System.String::Trim(System.Char[])", out spec, out error),
                "带参数短名");
            HookCommand.ResolvedTarget param;
            Check(HookCommand.ResolveTarget(spec, out param, out error), "参数短名应能消歧");
            bool noArg = true;
            foreach (MethodBase m in param.Methods) if (m.GetParameters().Length != 1) noArg = false;
            Check(noArg, "参数表应过滤掉无参重载");

            Check(HookCommand.TryParseTarget("System.Private.CoreLib!System.String", out spec, out error),
                "类型级 + 程序集前缀");
            HookCommand.ResolvedTarget typeOnly;
            Check(HookCommand.ResolveTarget(spec, out typeOnly, out error), "类型级目标应解析成功");
            Check(typeOnly != null && typeOnly.TypeOnly && typeOnly.Type == typeof(string), "类型级目标形状");

            Check(HookCommand.TryParseTarget("System.NoSuchType9999", out spec, out error), "未知类型语法合法");
            SlashResult missing;
            Check(!HookCommand.ResolveTarget(spec, out resolved, out missing), "未知类型应报错");
            Check(missing != null && missing.Kind == "hook" && (bool)missing.Data["ok"] == false, "未知类型是 hook 错误");
            Check(missing.Lines.Count > 0 && missing.Lines[0].IndexOf("未找到类型", StringComparison.Ordinal) >= 0,
                "未知类型的行应点名未找到类型");

            Check(HookCommand.TryParseTarget("System.String::NoSuchMember9999", out spec, out error), "未知成员语法合法");
            SlashResult badMember;
            Check(!HookCommand.ResolveTarget(spec, out resolved, out badMember), "未知成员应报错");
            Check(badMember.Data.ContainsKey("candidates"), "未知成员应附 candidates");
        }

        private static void BuildListCases()
        {
            var entries = new List<HookCommand.PatchEntry>
            {
                Entry("A.Kill()", "A", "o1", "prefix", 400, 0, "P.Prefix"),
                Entry("A.Kill()", "A", "o1", "postfix", 100, 1, "P.Postfix"),
                Entry("B.Foo()", "B", "o2", "transpiler", 400, 0, "P.Transpiler")
            };

            SlashResult r = HookCommand.BuildList(entries, 200, "3.0.0.0");
            Check(r.Kind == "hook", "Kind 应为 hook");
            Check((bool)r.Data["ok"], "ok 应为 true");
            Check((int)r.Data["count"] == 2, "count 应是 2 个被补目标（实得 " + r.Data["count"] + "）");
            Check((int)r.Data["patchCount"] == 3, "patchCount 应是 3");
            Check((int)r.Data["hookCount"] == 2, "hookCount 应是 2 个 (target, owner) 对");
            Check((bool)r.Data["truncated"] == false, "未超上限不应截断");
            Check((int)r.Data["ownerCount"] == 2, "ownerCount 应是 2");

            var hooks = r.Data["hooks"] as List<Dictionary<string, object>>;
            Check(hooks != null && hooks.Count == 2, "hooks 应有 2 条");
            Check(hooks[0]["target"].Equals("A.Kill()") && hooks[0]["owner"].Equals("o1"), "hooks 按 target/owner 升序");
            Check((int)hooks[0]["patchCount"] == 2, "A/o1 应有 2 条补丁");
            Check((int)hooks[0]["priority"] == 100, "priority 应取该 (target,owner) 的最小值");
            Check((int)hooks[0]["prefixes"] == 1 && (int)hooks[0]["postfixes"] == 1, "应按 kind 计数");
            Check(((List<int>)hooks[0]["priorities"]).Count == 2, "priorities 应列出全部不同优先级");

            var owners = r.Data["owners"] as List<Dictionary<string, object>>;
            Check(owners != null && owners.Count == 2, "owners 汇总应有 2 个");
            Check(owners[0]["owner"].Equals("o1") && (int)owners[0]["patchCount"] == 2,
                "owners 应按 patchCount 降序（o1 在前）");
            var types = r.Data["types"] as List<Dictionary<string, object>>;
            Check(types != null && types.Count == 2 && types[0]["type"].Equals("A"), "types 汇总应按 patchCount 降序");

            Boolean lineOk = false;
            foreach (string line in r.Lines) if (line.IndexOf("A.Kill()", StringComparison.Ordinal) >= 0) lineOk = true;
            Check(lineOk, "Lines 应包含目标行");
            Check(r.Lines[0].IndexOf("2 个目标", StringComparison.Ordinal) >= 0, "首行应报目标数");
            Check(r.Lines[0].IndexOf("3.0.0.0", StringComparison.Ordinal) >= 0, "首行应带 0Harmony 版本");

            SlashResult cut = HookCommand.BuildList(entries, 1, null);
            Check((bool)cut.Data["truncated"], "超过上限应标记 truncated");
            Check((cut.Data["hooks"] as List<Dictionary<string, object>>).Count == 1, "截断后只返回 1 条");
            Check((int)cut.Data["hookCount"] == 2, "hookCount 仍是全量");
            Check(((List<Dictionary<string, object>>)cut.Data["owners"]).Count == 2, "汇总不受上限影响");

            SlashResult empty = HookCommand.BuildList(null, 200, null);
            Check((int)empty.Data["count"] == 0 && (int)empty.Data["patchCount"] == 0, "空快照应给出 0 而不是抛");
        }

        private static void BuildCheckCases()
        {
            MethodBase trim = typeof(string).GetMethod("Trim", Type.EmptyTypes);
            Check(trim != null, "前置：string.Trim() 应存在");

            var patches = new List<HookCommand.PatchEntry>
            {
                Entry("System.String.Trim()", "System.String", "roslynprobe.leak", "prefix", 400, 0, "P.Prefix"),
                Entry("System.String.Trim()", "System.String", "roslynprobe.leak", "postfix", 400, 1, "P.Postfix")
            };
            HookCommand.MethodCheck check = HookCommand.MakeCheck(trim, patches, null);
            Check(check.Layers == 2, "两层补丁（实得 " + check.Layers + "）");
            Check(check.Patches.Count == 2, "补丁条目应 2 条");
            Check(check.Rejit != null && check.Rejit.ContainsKey("available"), "ReJIT 探测应给出 available");
            Check(check.MethodText != null && check.MethodText.IndexOf("System.String.Trim", StringComparison.Ordinal) >= 0,
                "方法文本应含全名（实得 " + check.MethodText + "）");

            HookCommand.TargetSpec spec;
            SlashResult error;
            HookCommand.TryParseTarget("System.String::Trim", out spec, out error);
            var resolved = new HookCommand.ResolvedTarget();
            resolved.Spec = spec;
            resolved.Type = typeof(string);
            resolved.Methods = new List<MethodBase> { trim };
            resolved.TypeOnly = false;

            SlashResult r = HookCommand.BuildCheck("System.String::Trim", spec, resolved,
                new List<HookCommand.MethodCheck> { check }, "3.0.0.0");
            Check(r.Kind == "hook" && (bool)r.Data["ok"], "check 应 ok");
            Check((int)r.Data["matchCount"] == 1, "matchCount 应为 1");
            Check((bool)r.Data["patched"] && (int)r.Data["layers"] == 2, "patched/layers 应汇总");
            Check((bool)r.Data["typeOnly"] == false, "方法级目标");
            var methods = r.Data["methods"] as List<Dictionary<string, object>>;
            Check(methods != null && methods.Count == 1, "methods 应 1 条");
            Check((int)methods[0]["layers"] == 2 && (bool)methods[0]["patched"], "方法项应报层数");
            Check(methods[0].ContainsKey("rejit"), "方法项应带 rejit");
            Boolean lineOk = false;
            foreach (string line in r.Lines)
                if (line.IndexOf("Harmony: 2 层", StringComparison.Ordinal) >= 0) lineOk = true;
            Check(lineOk, "Lines 应报 “Harmony: 2 层”");
            Boolean rejitLine = false;
            foreach (string line in r.Lines) if (line.IndexOf("ReJIT:", StringComparison.Ordinal) >= 0) rejitLine = true;
            Check(rejitLine, "Lines 应报 ReJIT 行");

            // 未打补丁的方法
            HookCommand.MethodCheck clean = HookCommand.MakeCheck(trim, null, null);
            SlashResult r2 = HookCommand.BuildCheck("x", spec, resolved,
                new List<HookCommand.MethodCheck> { clean }, null);
            Check((bool)r2.Data["patched"] == false && (int)r2.Data["layers"] == 0, "无补丁应报 patched=false");
            Boolean cleanLine = false;
            foreach (string line in r2.Lines)
                if (line.IndexOf("未打补丁", StringComparison.Ordinal) >= 0) cleanLine = true;
            Check(cleanLine, "Lines 应报 “未打补丁”");

            // 补丁信息读失败：仍要 ok，让调用方看到部分事实
            HookCommand.MethodCheck broken = HookCommand.MakeCheck(trim, null, "FileNotFoundException: 0Harmony");
            SlashResult r3 = HookCommand.BuildCheck("x", spec, resolved,
                new List<HookCommand.MethodCheck> { broken }, null);
            Boolean brokenLine = false;
            foreach (string line in r3.Lines)
                if (line.IndexOf("不可判定", StringComparison.Ordinal) >= 0) brokenLine = true;
            Check(brokenLine, "补丁信息读失败应报不可判定");
            Check((string)((List<Dictionary<string, object>>)r3.Data["methods"])[0]["patchInfoError"] != null,
                "patchInfoError 应落 Data");
        }

        private static void ProbeRejitCases()
        {
            MethodBase trim = typeof(string).GetMethod("Trim", Type.EmptyTypes);
            Dictionary<string, object> d = null;
            try { d = HookCommand.ProbeRejit(trim); }
            catch (Exception ex)
            {
                Check(false, "ProbeRejit 不应抛（实抛 " + ex.GetType().Name + "）");
                return;
            }
            Check(d != null, "ProbeRejit 应返回字典");
            Check(d["available"] is bool, "available 应是 bool");
            Check(d.ContainsKey("updated"), "应有 updated 键");
            if (!(bool)d["available"])
            {
                Check(d["reason"] is string && ((string)d["reason"]).Length > 0,
                    "取不到 MethodBodyUpdater 时必须写 reason（实得 “" + (d["reason"] == null ? "<null>" : d["reason"]) + "”）");
                Check(d["updated"] == null, "取不到时 updated 应为 null（不猜）");
            }
            else
            {
                Check(d["updatedMethodTokenCount"] is int, "可用时应有 token 计数");
            }

            Dictionary<string, object> nullProbe = HookCommand.ProbeRejit(null);
            Check(nullProbe != null && (bool)nullProbe["available"] == false
                && nullProbe["reason"] is string, "method=null 也要给出 reason");
        }

        private static void ProvideCases()
        {
            var ctx = new SlashContext();

            SlashResult r = SafeProvide("list", ctx);
            Check(r != null && r.Kind == "hook", "provider(list) 应返回 hook 结果");
            Check(r.Data != null && r.Data.ContainsKey("ok"), "provider(list) Data 应含 ok");
            if ((bool)r.Data["ok"])
                Check(r.Data.ContainsKey("hooks"), "Harmony 可用时应有 hooks");
            else
                Check(r.Data["error"] is string && r.Data["error"].ToString().IndexOf("0Harmony", StringComparison.Ordinal) >= 0,
                    "Harmony 不可用时应点名 0Harmony（实得 " + (r.Data["error"] == null ? "<null>" : r.Data["error"].ToString()) + "）");

            SlashResult bad = SafeProvide("nope", ctx);
            Check(bad != null && (bool)bad.Data["ok"] == false, "provider 也要拒绝未知子命令");

            SlashResult noOwner = SafeProvide("off", ctx);
            Check(noOwner != null && noOwner.Data.ContainsKey("usage"), "provider(off 缺参) 应是用法页");

            SlashResult checkBad = SafeProvide("check", ctx);
            Check(checkBad != null && checkBad.Data.ContainsKey("usage"), "provider(check 缺目标) 应是用法页");

            SlashResult resolveBad = SafeProvide("check System.NoSuchType9999", ctx);
            Check(resolveBad != null && (bool)resolveBad.Data["ok"] == false
                && resolveBad.Lines[0].IndexOf("未找到类型", StringComparison.Ordinal) >= 0,
                "provider(check 未知类型) 应在解析阶段就报错（不依赖 Harmony）");

            SlashResult off3 = SafeProvide("off roslynprobe.leak", ctx);
            Check(off3 != null && off3.Kind == "hook" && off3.Data.ContainsKey("ok"),
                "provider(off 合法 owner) 不应抛");
        }

        private static SlashResult SafeProvide(string rest, SlashContext ctx)
        {
            try { return HookCommand.Provide(rest, ctx); }
            catch (Exception ex)
            {
                Check(false, "Provide(\"" + rest + "\") 抛了 " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static void InnerFinalizerCases()
        {
            // 只挂 inner finalizer 的方法：list 不能漏、check 不能报“未打补丁”（fix1/B2）。
            var entries = new List<HookCommand.PatchEntry>
            {
                Entry("A.Bar()", "A", "o1", "innerFinalizer", 400, 0, "P.InnerFinalizer")
            };

            SlashResult r = HookCommand.BuildList(entries, 200, null);
            Check((int)r.Data["count"] == 1, "只挂 inner finalizer 的方法应进 list（实得 count=" + r.Data["count"] + "）");
            Check((int)r.Data["patchCount"] == 1, "patchCount 应含 inner finalizer");
            var hooks = r.Data["hooks"] as List<Dictionary<string, object>>;
            Check(hooks != null && hooks.Count == 1, "hooks 应有 1 条");
            Check(hooks != null && (int)hooks[0]["innerFinalizers"] == 1, "innerFinalizers 应单独计数");
            Check(hooks != null && (int)hooks[0]["patchCount"] == 1, "hook 的 patchCount 应含 inner finalizer");
            Boolean innerLine = false;
            foreach (string line in r.Lines)
                if (line.IndexOf("inner", StringComparison.Ordinal) >= 0) innerLine = true;
            Check(innerLine, "行里应显示 inner 计数");

            MethodBase trim = typeof(string).GetMethod("Trim", Type.EmptyTypes);
            HookCommand.MethodCheck check = HookCommand.MakeCheck(trim, entries, null);
            Check(check.InnerFinalizers == 1 && check.Layers == 1,
                "MethodCheck 应把 inner finalizer 计入层数（否则 check 误报“未打补丁”；实得 layers=" + check.Layers + "）");

            SlashResult cr = HookCommand.BuildCheck("A.Bar", null, null,
                new List<HookCommand.MethodCheck> { check }, null);
            Check((bool)cr.Data["patched"] && (int)cr.Data["layers"] == 1, "check 汇总应看到这层补丁");
            var methods = cr.Data["methods"] as List<Dictionary<string, object>>;
            Check(methods != null && (int)methods[0]["innerFinalizers"] == 1, "check 的方法项应带 innerFinalizers");

            // 汇总口径（fix1/B1）：available=true 但 updated=null（token 读不出）⇒ 不可判定，不是“确定没动过”。
            HookCommand.MethodCheck unknown = HookCommand.MakeCheck(trim, null, null);
            unknown.Rejit = new Dictionary<string, object>
            {
                ["available"] = true,
                ["updated"] = null,
                ["reason"] = "目标方法的 MetadataToken 读不出来（0）"
            };
            SlashResult ur = HookCommand.BuildCheck("x", null, null,
                new List<HookCommand.MethodCheck> { unknown }, null);
            Check((bool)ur.Data["rejitUnknownAny"], "updated=null ⇒ 汇总必须算“不可判定”");
            Check((bool)ur.Data["rejitTouchedAny"] == false, "updated=null 不能算成“被动过”");
            Boolean unknownLine = false;
            foreach (string line in ur.Lines)
                if (line.IndexOf("不可判定", StringComparison.Ordinal) >= 0) unknownLine = true;
            Check(unknownLine, "ReJIT 行应写“不可判定 —— reason”");

            // ReadOptionalCollection 的守卫（2.4.1 上没有 InnerFinalizers 字段：走“成员不存在 → null 且无错”）。
            string error;
            List<object> got = HookCommand.ReadOptionalCollection(new FakeOptional(), "InnerFinalizers", out error);
            Check(got != null && got.Count == 2 && error == null, "成员存在应读出 2 项");
            got = HookCommand.ReadOptionalCollection(new FakeOptional(), "NoSuchCollection", out error);
            Check(got == null && error == null, "成员不存在 → null 且不是错误（2.4.1 的路径）");
            got = HookCommand.ReadOptionalCollection(new FakeOptional(), "NotACollection", out error);
            Check(got == null && error != null, "非集合成员 → null + error");
            got = HookCommand.ReadOptionalCollection(null, "InnerFinalizers", out error);
            Check(got == null, "owner 为 null → null");

            // KindOfOptional 的映射（避免 listing 出来的 kind 名漂移）
            // 真正的行为断言（替掉原来的占位）：updated=null 不得报 conflict，且 reason 必须落进 methods Data。
            var unknownMethods = ur.Data["methods"] as List<Dictionary<string, object>>;
            Check(unknownMethods != null && (bool)unknownMethods[0]["conflict"] == false,
                "updated=null（不可判定）不得报 conflict");
            var unknownRejit = unknownMethods == null ? null : unknownMethods[0]["rejit"] as Dictionary<string, object>;
            Check(unknownRejit != null && unknownRejit["reason"] is string && ((string)unknownRejit["reason"]).Length > 0,
                "不可判定的 reason 必须落进 methods[].rejit");

            // 【fix3/审查(b)】强情形：**真挂一层补丁** + ReJIT 探测 updated=null。
            // 这正是 fix1(B1) 要防的谎报形态：「读不出」不得被当成「确定没被 ReJIT 过」。
            var strongPatches = new List<HookCommand.PatchEntry>
            {
                Entry("System.String.Trim()", "System.String", "roslynprobe.leak", "prefix", 400, 0, "P.Prefix")
            };
            HookCommand.MethodCheck strong = HookCommand.MakeCheck(trim, strongPatches, null);
            Check(strong.Layers == 1, "前置：强情形样本必须真挂一层补丁（实得 " + strong.Layers + "）");
            strong.Rejit = new Dictionary<string, object>
            {
                ["available"] = true,
                ["updated"] = null,
                ["reason"] = "目标方法的 MetadataToken 读不出来（0）"
            };
            SlashResult sr = HookCommand.BuildCheck("System.String::Trim", null, null,
                new List<HookCommand.MethodCheck> { strong }, null);
            Check((bool)sr.Data["patched"] && (int)sr.Data["layers"] == 1,
                "强情形：patched/layers 应为 true/1（实得 " + sr.Data["patched"] + "/" + sr.Data["layers"] + "）");
            Check((bool)sr.Data["rejitUnknownAny"],
                "强情形：updated=null ⇒ 汇总必须算“不可判定”（变异 m4 回退口径时本项会 FALSE）");
            var strongMethods = sr.Data["methods"] as List<Dictionary<string, object>>;
            Check(strongMethods != null && (bool)strongMethods[0]["conflict"] == false,
                "强情形：不可判定不得报 conflict（否则会误报“Harmony 补丁与 ReJIT 叠加”的假警告）");
            // fix4：必须**限定到汇总行**再措辞断言 —— 逐方法明细行 "ReJIT: 不可判定 —— <reason>" 本身就含
            // “不可判定”，扫全量行会让这条断言在 m4 回退下仍然通过（假绿，复审指出 :543）。
            Boolean strongSummarySeen = false;
            Boolean strongSummarySaysUnknown = false;
            Boolean strongSummaryLiesMissed = false;
            foreach (string line in sr.Lines)
            {
                if (!line.TrimStart().StartsWith("合计:", StringComparison.Ordinal)) continue;   // 只认汇总行（前缀“合计:”）
                strongSummarySeen = true;
                if (line.IndexOf("不可判定", StringComparison.Ordinal) >= 0) strongSummarySaysUnknown = true;
                if (line.IndexOf("未发现被 MethodBodyUpdater 动过的方法", StringComparison.Ordinal) >= 0) strongSummaryLiesMissed = true;
            }
            Check(strongSummarySeen, "强情形：应存在一行汇总（前缀“合计:”）—— 否则下面的断言是空转");
            Check(strongSummarySaysUnknown, "强情形：汇总行必须说“不可判定”（只看汇总行；逐方法行的“不可判定”不算）");
            Check(!strongSummaryLiesMissed, "强情形：汇总行绝不能谎报“未发现被 MethodBodyUpdater 动过的方法”");

            // finalizer 与 innerFinalizer 不得互相吞（kind 计数必须分离）。
            SlashResult mix = HookCommand.BuildList(new List<HookCommand.PatchEntry>
            {
                Entry("A.Bar()", "A", "o1", "finalizer", 400, 0, "P.F"),
                Entry("A.Bar()", "A", "o1", "innerFinalizer", 400, 1, "P.IF")
            }, 200, null);
            var mixHooks = mix.Data["hooks"] as List<Dictionary<string, object>>;
            Check(mixHooks != null && (int)mixHooks[0]["finalizers"] == 1 && (int)mixHooks[0]["innerFinalizers"] == 1,
                "finalizer 与 innerFinalizer 必须分开计数（实得 finalizers="
                    + (mixHooks == null ? "?" : mixHooks[0]["finalizers"].ToString()) + "）");
        }

        private static void WildcardCases()
        {
            Check(HookCommand.WildcardMatch("Assembly-CSharp", "Assembly-CSharp"), "精确匹配");
            Check(!HookCommand.WildcardMatch("Assembly-CSharp", "System.Private.CoreLib"), "不匹配");
            Check(HookCommand.WildcardMatch("System.*", "System.Private.CoreLib"), "* 后缀");
            Check(HookCommand.WildcardMatch("*.CoreLib", "System.Private.CoreLib"), "* 前缀");
            Check(HookCommand.WildcardMatch("Ass?mbly-CSharp", "Assembly-CSharp"), "? 单字符");
            Check(!HookCommand.WildcardMatch("Ass?mbly", "Assembly-CSharp"), "? 不吞多个字符");
            Check(HookCommand.WildcardMatch("*", "anything"), "全通配");
            Check(HookCommand.WildcardMatch(null, "anything"), "null 模式 = 不过滤");
        }

        // ---------------- 小工具 ----------------

        /// <summary>给 ReadOptionalCollection 用的合成 “Patches 对象”：一个集合字段 + 一个非集合字段。</summary>
        private sealed class FakeOptional
        {
            public readonly int[] InnerFinalizers = new int[] { 1, 2 };
            public int NotACollection = 7;
        }

        private static HookCommand.PatchEntry Entry(string target, string type, string owner, string kind,
            int priority, int index, string patchMethod)
        {
            var e = new HookCommand.PatchEntry();
            e.Target = target;
            e.TypeName = type;
            e.Owner = owner;
            e.PatchKind = kind;
            e.Priority = priority;
            e.Index = index;
            e.PatchMethod = patchMethod;
            e.AssemblyName = "Assembly-CSharp";
            return e;
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
    }
}
