// RoslynProbe —— /env 的纯逻辑单测(零依赖:public static int Run() 返回失败数,由 SlashTestHarness 反射发现)。
//
// 覆盖:登记项形状 / 四种模式的路由与 Data 键 / 未知子参数回用法页 /
// 开关标注表(引擎-游戏-mod-未知-裸参数) / WAYLITH_* 过滤 / 垫片重定向解析 /
// layer 与 waylithclr 的 reason 约定 / Lines 分组 / 奇怪输入不抛。
//
// 真机独有的值(WaylithClr 类型是否存在、注册表里 Vulkan 隐式层有几条)不在本离线套件覆盖范围内 ——
// 断言写成「要么有值、要么有非空 reason」,两种环境都成立;真机验收见
// docs/boundary-suite/s11-8/REPORT-specials.md §7(/env 的 P/Invoke 与 layer 读数)——
// 该读数测于 b45e570 产物,行为面核验见该文 §12。

using System;
using System.Collections.Generic;
using System.Reflection;
using RoslynProbe;

namespace RoslynProbe.Tests
{
    public static class EnvCommandTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";
        private static int checks;

        public static int Run()
        {
            Failures.Clear();
            checks = 0;
            currentCase = "(none)";

            SlashRegistry.ResetForTests();       // 隔离:先清空表,只登记本指令
            EnvCommand.Register();

            Case("登记项:/env 的 verb/Kind/Usage/重名拒绝", RegisterCase);
            Case("分派:无参 = 三段全列 + runtime 基线", AllModeCase);
            Case("分派:args 只渲染 args+env", ArgsModeCase);
            Case("分派:waylithclr 只渲染 waylithclr", WaylithClrModeCase);
            Case("分派:layer 只渲染 layer", LayerModeCase);
            Case("分派:未知子参数 → 用法页(不抛)", UnknownModeCase);
            Case("解析:大小写/空白/多余 token", ParseEdgeCase);
            Case("标注:引擎/游戏/mod 开关表", ArgTableCase);
            Case("标注:未知开关与裸参数", ArgUnknownCase);
            Case("环境变量:WAYLITH_* 过滤与文档清单", EnvFilterCase);
            Case("M3/N4 回归:Vulkan 验证层判定与引擎真身同口径(真值源 + 端到端 requested 双门)", EnableVulkanValidationCase);
            Case("错误文案:自带 Win32 码表(不引入 Microsoft.Win32.Primitives)", DescribeWin32Case);
            Case("垫片:redirects 解析与模式匹配", RedirectCase);
            Case("layer:开关读取、reason 与 unavailable 约定", LayerReasonCase);
            Case("I2 回归:/env layer 不得改变已加载程序集集合(注册表走 P/Invoke)", NoAssemblyLoadCase);
            Case("waylithclr:reason 约定与 IsSupported 便利键", WaylithClrReasonCase);
            Case("Lines:分组渲染齐全", LinesCase);
            Case("健壮性:奇怪输入不抛、Kind 恒为 env", RobustCase);

            // 收尾:还原真实表,避免影响同进程其它写手的测试。
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();

            Console.WriteLine("EnvCommandTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- 用例 ----------------

        private static void RegisterCase()
        {
            SlashEntry e;
            Check(SlashRegistry.TryGet("env", out e), "/env 应已登记");
            Check(e != null && e.Kind == "env", "Kind 应为 env，实际 " + Show(e == null ? null : e.Kind));
            Check(e != null && e.Handler != null, "Handler 不应为 null（stub 才留 null）");
            Check(e != null && e.Usage != null && e.Usage.StartsWith("[args|waylithclr|layer]", StringComparison.Ordinal),
                "Usage 参数段应为 [args|waylithclr|layer]，实际 " + Show(e == null ? null : e.Usage));
            Check(e != null && e.Usage.IndexOf("——", StringComparison.Ordinal) > 0, "Usage 应含 '——' 分隔符");
            Check(e != null && e.Group == "运行时", "Group 应为 运行时");
            // 契约 v1.1 §3.5:纯 BCL 指令的 handler 也叫 Provide(与 Gc/Diag/Threads 同款命名断言)
            Check(typeof(EnvCommand).GetMethod("Provide", BindingFlags.Public | BindingFlags.Static,
                null, new Type[] { typeof(string), typeof(SlashContext) }, null) != null,
                "纯 BCL 指令的 handler 应叫 Provide(契约 v1.1 §3.5)");
            Check(e != null && e.Handler != null && e.Handler.Method.Name == "Provide",
                "登记项 Handler 必须绑到 Provide（改名漂回去会红），实际 " + Show(e == null || e.Handler == null ? null : e.Handler.Method.Name));

            bool threw = false;
            try { EnvCommand.Register(); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw, "同一 verb 重复登记应抛（重名拒绝）");
        }

        private static void AllModeCase()
        {
            SlashResult r = Dispatch("/env");
            Check(r != null && r.Kind == "env", "Kind 应为 env");
            Check(r != null && r.Data != null, "Data 不应为 null");
            Check(r != null && r.Data["mode"] as string == "all", "无参时 mode 应为 all");
            Check(r != null && Has(r.Data, "args") && Has(r.Data, "env") && Has(r.Data, "waylithClr")
                && Has(r.Data, "runtime") && Has(r.Data, "layer"), "无参应带 args/env/waylithClr/runtime/layer");
            Check(r.Data["ok"] as bool? == true, "Data[ok] 应为 true");

            List<string> argv = r.Data["args"] as List<string>;
            Check(argv != null && argv.Count == Environment.GetCommandLineArgs().Length,
                "args 应是 Environment.GetCommandLineArgs() 的全量（含 argv[0]）");
            Check(r.Data["env"] is Dictionary<string, string>, "env 应是 name->value 字典");
            Check(r.Data["envKnown"] is List<Dictionary<string, object>>, "envKnown 应是列表");
            List<Dictionary<string, object>> flags = r.Data["flags"] as List<Dictionary<string, object>>;
            Check(flags != null && argv != null && flags.Count == argv.Count, "flags 与 args 一一对应");

            Dictionary<string, object> rt = Sub(r, "runtime");
            Check(Has(rt, "version") && Has(rt, "rid") && Has(rt, "framework") && Has(rt, "processArch"),
                "runtime 四键齐全（version/rid/framework/processArch）");

            Check(StartsWithAny(r.Lines, "启动参数"), "应渲染启动参数组");
            Check(StartsWithAny(r.Lines, "运行时:"), "无参应渲染运行时基线");
        }

        private static void ArgsModeCase()
        {
            SlashResult r = Dispatch("/env args");
            Check(Has(r.Data, "args") && Has(r.Data, "env"), "args 模式应带 args/env");
            Check(!Has(r.Data, "waylithClr") && !Has(r.Data, "layer"), "args 模式不应带 waylithClr/layer");
            Check(Has(r.Data, "runtime"), "runtime 基线始终在 Data 里（规格把它与 args/env 并列）");
            Check(!StartsWithAny(r.Lines, "运行时:"), "args 模式的 Lines 只该有 args+env 段");
            Check(StartsWithAny(r.Lines, "启动参数"), "args 模式应有启动参数组");
            Check(StartsWithAny(r.Lines, "环境变量 WAYLITH_*"), "args 模式应有环境变量组");
        }

        private static void WaylithClrModeCase()
        {
            SlashResult r = Dispatch("/env waylithclr");
            Dictionary<string, object> wc = Sub(r, "waylithClr");
            Check(wc != null, "waylithclr 模式应带 waylithClr");
            Check(Has(wc, "found") && Has(wc, "types") && Has(wc, "reason"), "waylithClr 应带 found/types/reason");
            Check(!Has(r.Data, "args") && !Has(r.Data, "layer"), "waylithclr 模式不应带 args/layer");
            bool found = wc["found"] as bool? == true;
            Check(found || !string.IsNullOrEmpty(wc["reason"] as string), "找不到 WaylithClr 时必须给非空 reason");
            Check(wc["typeCount"] is int, "typeCount 应是 int");
        }

        private static void LayerModeCase()
        {
            SlashResult r = Dispatch("/env layer");
            Dictionary<string, object> ly = Sub(r, "layer");
            Check(ly != null, "layer 模式应带 layer");
            Check(Has(ly, "switches") && Has(ly, "switchesMissing") && Has(ly, "runtimeSwitches")
                && Has(ly, "shunt") && Has(ly, "vulkan") && Has(ly, "switchSource"),
                "layer 应带 switches/switchesMissing/runtimeSwitches/shunt/vulkan/switchSource");
            Check(!Has(r.Data, "args"), "layer 模式不应带 args");

            Dictionary<string, object> sw = ly["switches"] as Dictionary<string, object>;
            Dictionary<string, object> rts = ly["runtimeSwitches"] as Dictionary<string, object>;
            List<string> miss = ly["switchesMissing"] as List<string>;
            Check(sw != null && rts != null && miss != null, "switches/runtimeSwitches/switchesMissing 类型");
            Check(sw.Count + rts.Count + miss.Count >= EnvCommand.DocumentedSwitchKeys.Length,
                "文档化的 22 条启动期开关都要有交代（读到或 missing）");

            Dictionary<string, object> vk = ly["vulkan"] as Dictionary<string, object>;
            Check(vk != null && vk["registered"] is List<Dictionary<string, object>>, "vulkan.registered 应是列表");
            Check(vk != null && vk["requested"] is List<Dictionary<string, object>>, "vulkan.requested 应是列表");
            Check(vk != null && vk["env"] is Dictionary<string, string>, "vulkan.env 应是 name->value 字典");

            Dictionary<string, object> shunt = ly["shunt"] as Dictionary<string, object>;
            Check(shunt != null && shunt["redirects"] is List<Dictionary<string, object>>, "shunt.redirects 应是列表");
            Check(shunt != null && shunt["assemblies"] is Dictionary<string, object>, "shunt.assemblies 应是字典");
        }

        private static void UnknownModeCase()
        {
            SlashResult r = Dispatch("/env nope");
            Check(r != null && r.Kind == "env", "未知子参数不换 Kind");
            Check(r.Data["usage"] as bool? == true, "未知子参数应回用法页（usage=true）");
            Check(r.Data["ok"] as bool? == false, "用法页 ok 应为 false");
            Check(r.Data["unknown"] as string == "nope", "Data[unknown] 应回显原始 rest");
            Check(r.Data["modes"] is List<string>, "用法页应列出可用模式");

            r = Dispatch("/env args bogus");   // 多余 token 不是错
            Check(r.Data["ok"] as bool? == true && r.Data["mode"] as string == "args", "多余 token 应被忽略而不是报错");
            Check(IndexOfAny(r.Lines, "忽略多余参数") >= 0, "多余 token 应在 Lines 里留痕");
        }

        private static void ParseEdgeCase()
        {
            string extra;
            Check(EnvCommand.ParseMode("", out extra) == "" && extra == null, "空 rest → 全列");
            Check(EnvCommand.ParseMode("   ", out extra) == "", "空白 rest → 全列");
            Check(EnvCommand.ParseMode("ARGS", out extra) == "args", "大小写不敏感");
            Check(EnvCommand.ParseMode(" WaylithClr ", out extra) == "waylithclr", "Trim + 小写化");
            Check(EnvCommand.ParseMode("layer", out extra) == "layer" && extra == null, "单 token 无 extra");
            Check(EnvCommand.ParseMode("args x y", out extra) == "args" && extra == "x y", "extra 收第 2 个 token 起");
            Check(EnvCommand.ParseMode("bogus", out extra) == null, "未知 token → null");
            Check(EnvCommand.ParseMode("/env", out extra) == null, "带 '/' 的 token 不算子参数");
        }

        private static void ArgTableCase()
        {
            Check(Scope("-Headless") == "engine", "-Headless 应是引擎开关");
            Check(Scope("headless") == "engine", "裸 headless 也命中（引擎侧是 Trim('-','+') 后全等比较）");
            Check(Scope("+NoWindow") == "engine", "'+' 前缀引擎也认");
            Check(Scope("-EnableVulkanValidation") == "engine", "-EnableVulkanValidation 是引擎开关");
            Check(Scope("-NoFileLog") == "engine", "-NoFileLog 是引擎开关");
            Check(Scope("-quicktest") == "game", "-quicktest 是游戏开关");
            Check(Scope("-QuitAfterBoot") == "game", "-QuitAfterBoot 是游戏开关");
            Check(Scope("-savedatafolder=C:\\tmp\\rw") == "game", "-savedatafolder 是游戏开关");
            Check(Scope("-mod=brrainz.harmony;ue.roslyn") == "game", "-mod= 是游戏开关");
            Check(Scope("-RoslynConsole") == "mod", "裸 -RoslynConsole 是 mod 开关");
            Check(Scope("-RoslynConsole=probe2") == "mod", "-RoslynConsole=名 是 mod 开关");
            Check(Scope("RoslynConsole") == "mod", "裸词也命中（mod 侧允许裸 key）");

            Dictionary<string, object> f = EnvCommand.DescribeArg(1, "-savedatafolder=C:\\tmp\\rw");
            Check(f["value"] as string == "C:\\tmp\\rw", "值应在第一个 '=' 之后原样保留（含反斜杠）");
            f = EnvCommand.DescribeArg(1, "-WindowSize=1920,1080");
            Check(f["name"] as string == "WindowSize" && f["value"] as string == "1920,1080", "name/value 按第一个 '=' 拆");
            f = EnvCommand.DescribeArg(1, "-WindowSize");
            Check(((string)f["note"]).IndexOf("需要取值", StringComparison.Ordinal) >= 0, "带值开关缺值时应提示需要取值");
            f = EnvCommand.DescribeArg(1, "-mod=brrainz.harmony;ue.roslyn");
            Check(f["value"] as string == "brrainz.harmony;ue.roslyn", "mod= 的值原样保留");
            f = EnvCommand.DescribeArg(1, "+quicktest");
            Check(f["scope"] as string == "game" && ((string)f["note"]).IndexOf("Verse", StringComparison.Ordinal) >= 0,
                "'+' 前缀的 game 开关要提示 Verse 侧读不到");
            f = EnvCommand.DescribeArg(0, "C:\\game\\RimWorld.exe");
            Check(f["scope"] as string == "exe" && f["name"] as string == "RimWorld.exe", "argv[0] 标成 exe");

            Check(EnvCommand.ArgTable.Count >= 31, "开关表至少 31 条（引擎 20 + 游戏 10 + mod）");
        }

        private static void ArgUnknownCase()
        {
            Dictionary<string, object> f = EnvCommand.DescribeArg(1, "-definitely-not-a-switch");
            Check(f["scope"] as string == "unknown", "未知开关 scope=unknown");
            Check(f["known"] as bool? == false, "未知开关 known=false");
            Check(f["value"] == null, "未知开关没有值");

            f = EnvCommand.DescribeArg(1, "-foo=bar");
            Check(f["name"] as string == "foo" && f["value"] as string == "bar", "未知开关也拆 name=value");

            f = EnvCommand.DescribeArg(1, "1+1");
            Check(f["scope"] as string == "positional", "无前缀裸 token 是 positional");
            f = EnvCommand.DescribeArg(1, "");
            Check(f["scope"] as string == "positional", "空 token 不抛");
        }

        private static void EnvFilterCase()
        {
            Check(EnvCommand.IsWaylithEnvName("WAYLITH_VULKAN_DEVICE"), "WAYLITH_ 前缀应命中");
            Check(EnvCommand.IsWaylithEnvName("waylith_x"), "大小写不敏感");
            Check(!EnvCommand.IsWaylithEnvName("PATH"), "非 WAYLITH 不算");
            Check(!EnvCommand.IsWaylithEnvName(""), "空串不算");
            Check(!EnvCommand.IsWaylithEnvName(null), "null 不算");
            Check(EnvCommand.KnownWaylithEnv.Length == 11, "文档化的 WAYLITH_* 是 11 条");

            SlashResult r = Dispatch("/env args");
            Dictionary<string, string> env = r.Data["env"] as Dictionary<string, string>;
            int realCount = 0;
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                if (EnvCommand.IsWaylithEnvName(e.Key as string)) realCount++;
            Check(env != null && env.Count == realCount, "env 字典条数应等于本进程真实的 WAYLITH_* 条数");
            List<Dictionary<string, object>> known = r.Data["envKnown"] as List<Dictionary<string, object>>;
            Check(known != null && known.Count == 11, "envKnown 应列全 11 条（含未设置）");
        }

        /// <summary>
        /// M3 回归(与引擎**真身**同口径):引擎是
        /// <c>Environment.CommandLine.Contains("-EnableVulkanValidation")</c>(GraphicsDevice.cs:174/577) ——
        /// <c>String.Contains(String)</c> = 序数、区分大小写、要求前导 '-'。
        ///
        /// 双门(修复轮 3 / N4):
        ///   (a) **真值源**:旗标串来自 EnvCommand.VulkanValidationSwitch(调用点也用同一个),这里把三个轴钉在它身上;
        ///   (b) **端到端**:走 AddVulkanValidationRequest(= IncludeLayer 真正调用的那条路径),
        ///       断言 requested 有没有登记 —— 这样"调用点把前导 '-' 去掉"这类改动也会红,不再只是局部字面量被钉。
        /// </summary>
        private static void EnableVulkanValidationCase()
        {
            // (a) 真值源:必须有前导 '-' 且大小写精确
            Check(EnvCommand.VulkanValidationSwitch == "-EnableVulkanValidation",
                "旗标串真值源必须是 \"-EnableVulkanValidation\"，实际 " + Show(EnvCommand.VulkanValidationSwitch));
            Check(EnvCommand.VulkanValidationSwitch.Length > 0 && EnvCommand.VulkanValidationSwitch[0] == '-',
                "旗标串必须带前导 '-'（去掉它会让 +Enable/裸旗标重新谎报）");

            string needle = EnvCommand.VulkanValidationSwitch;
            Check(EnvCommand.CommandLineContainsIn("RimWorld.exe -EnableVulkanValidation -Headless", needle), "标准写法命中");
            Check(!EnvCommand.CommandLineContainsIn("RimWorld.exe -enablevulkanvalidation", needle), "全小写**不**命中(引擎区分大小写)");
            Check(!EnvCommand.CommandLineContainsIn("RimWorld.exe -ENABLEVULKANVALIDATION", needle), "全大写也不命中");
            Check(!EnvCommand.CommandLineContainsIn("RimWorld.exe +EnableVulkanValidation", needle), "'+' 前缀不命中(引擎要求前导 '-')");
            Check(!EnvCommand.CommandLineContainsIn("RimWorld.exe EnableVulkanValidation", needle), "裸旗标不命中(缺前导 '-')");
            Check(!EnvCommand.CommandLineContainsIn("RimWorld.exe -Headless", needle), "没有就不命中");
            // 引擎的 Contains 很笨:子串命中就算(我们不比引擎更聪明,只求同口径)
            Check(EnvCommand.CommandLineContainsIn("-xx-EnableVulkanValidation-yy", needle), "子串命中即算(照抄引擎的 Contains 语义,比 token 名更松)");
            Check(!EnvCommand.CommandLineContainsIn("-XXEnableVulkanValidationYY", needle), "缺前导 '-' 的粘连串不命中");
            Check(!EnvCommand.CommandLineContainsIn(null, needle), "null 命令行安全");
            Check(!EnvCommand.CommandLineContainsIn("RimWorld.exe", null), "null needle 安全");

            // (b) 端到端:调用点真正走的那条路径
            Check(VulkanRequestCount("RimWorld.exe -EnableVulkanValidation -Headless") == 1, "标准写法:应登记 1 条验证层请求");
            Check(VulkanRequestCount("RimWorld.exe -enablevulkanvalidation") == 0, "全小写:不得登记(引擎不认)");
            Check(VulkanRequestCount("RimWorld.exe +EnableVulkanValidation") == 0, "'+' 前缀:不得登记(引擎不认)");
            Check(VulkanRequestCount("RimWorld.exe EnableVulkanValidation") == 0, "裸旗标:不得登记(引擎不认)");
            Check(VulkanRequestCount("RimWorld.exe -Headless") == 0, "无关命令行:不得登记");
            Check(VulkanRequestCount(null) == 0, "null 命令行:不得登记且不抛");
            Check(VulkanRequestCount("-x-EnableVulkanValidation") == 1, "子串粘连:按引擎口径仍登记");

            List<Dictionary<string, object>> req = BuildVulkanRequest("RimWorld.exe -EnableVulkanValidation");
            Check(req.Count == 1 && req[0]["layer"] as string == "VK_LAYER_KHRONOS_validation", "登记项应点名 VK_LAYER_KHRONOS_validation");
            Check(req.Count == 1 && ((string)req[0]["from"]).IndexOf(EnvCommand.VulkanValidationSwitch, StringComparison.Ordinal) >= 0,
                "登记项的 from 应回显真值源旗标串");
        }

        private static List<Dictionary<string, object>> BuildVulkanRequest(string commandLine)
        {
            var requested = new List<Dictionary<string, object>>();
            EnvCommand.AddVulkanValidationRequest(requested, commandLine);
            return requested;
        }

        private static int VulkanRequestCount(string commandLine)
        {
            return BuildVulkanRequest(commandLine).Count;
        }

        /// <summary>错误文案走自带小码表(不 new Win32Exception ⇒ 不引入 Microsoft.Win32.Primitives)。</summary>
        private static void DescribeWin32Case()
        {
            Check(EnvCommand.DescribeWin32(2).IndexOf("ERROR_FILE_NOT_FOUND", StringComparison.Ordinal) == 0, "2 应给出 FILE_NOT_FOUND 文案");
            Check(EnvCommand.DescribeWin32(5).IndexOf("ERROR_ACCESS_DENIED", StringComparison.Ordinal) == 0, "5 应给出 ACCESS_DENIED 文案");
            Check(EnvCommand.DescribeWin32(259).IndexOf("ERROR_NO_MORE_ITEMS", StringComparison.Ordinal) == 0, "259 应给出 NO_MORE_ITEMS 文案");
            Check(EnvCommand.DescribeWin32(1012).IndexOf("ERROR_CANTREAD", StringComparison.Ordinal) == 0,
                "1012 应给出 CANTREAD 文案（真实语义是「无法读取配置注册表项」，不是 NO_MORE_ITEMS）");
            Check(EnvCommand.DescribeWin32(1012).IndexOf("NO_MORE_ITEMS", StringComparison.Ordinal) < 0, "1012 不得再混进 NO_MORE_ITEMS");
            Check(EnvCommand.DescribeWin32(31337).Length > 0 && EnvCommand.DescribeWin32(31337).IndexOf("31337", StringComparison.Ordinal) >= 0,
                "未收录码要给固定兜底并带上码本身");
            Check(EnvCommand.DescribeWin32(1008).Length > 0, "已知码不抛");
        }

        private static void RedirectCase()
        {
            List<Dictionary<string, object>> list = EnvCommand.ParseRedirects(
                "UnityEngine*=UnityStubs;Unity.Newtonsoft.Json=;Unity.*=UnityStubs;Assembly-CSharp-firstpass=UnityStubs");
            Check(list.Count == 4, "四条重定向，实际 " + list.Count);
            Check(list[0]["pattern"] as string == "UnityEngine*" && list[0]["target"] as string == "UnityStubs", "第一条");
            Check(list[1]["target"] as string == "", "空目标要保留成空串");
            Check(EnvCommand.ParseRedirects(null).Count == 0, "null → 空表");
            Check(EnvCommand.ParseRedirects("").Count == 0, "空串 → 空表");
            Check(EnvCommand.ParseRedirects("; ; A=B ;").Count == 1, "空段要跳过");

            Check(EnvCommand.PatternMatches("UnityEngine*", "UnityEngine.CoreModule"), "尾部 '*' 当前缀匹配");
            Check(!EnvCommand.PatternMatches("UnityEngine*", "UnityStubs"), "前缀不匹配");
            Check(EnvCommand.PatternMatches("Assembly-CSharp-firstpass", "assembly-csharp-firstpass"), "全等忽略大小写");
            Check(!EnvCommand.PatternMatches("Unity.*", "UnityStubs"), "Unity.* 不该匹配 UnityStubs");
            Check(!EnvCommand.PatternMatches(null, "x") && !EnvCommand.PatternMatches("x", null), "null 安全");
        }

        private static void LayerReasonCase()
        {
            SlashResult r = Dispatch("/env layer");
            Dictionary<string, object> ly = Sub(r, "layer");
            Dictionary<string, object> sw0 = ly["switches"] as Dictionary<string, object>;
            bool ok = ly["ok"] as bool? == true;
            Check(sw0 != null && ok == (sw0.Count > 0), "ok 的口径应是「读到了 WaylithClr.* 键」");
            Check(ok || !string.IsNullOrEmpty(ly["reason"] as string), "layer.ok=false 时必须给非空 reason");
            Check(ly["reason"] == null || ly["reason"] is string, "reason 应是 string 或 null");
            Check(ly["shuntEvidence"] is bool, "shuntEvidence 应是 bool(垫片旁证)");
            Check(ly["okMeaning"] is string, "okMeaning 应说明 ok 的口径");
            Check(ly["unavailable"] is List<string>, "unavailable 应是列表");

            object v;
            bool got = EnvCommand.ReadSwitch("WaylithClr.SkipAccessChecks", out v);
            Check(got == (v != null), "ReadSwitch 的返回 true 当且仅当 value 非 null");
            Check(!EnvCommand.ReadSwitch("", out v) && !EnvCommand.ReadSwitch(null, out v), "空键/ null 键安全返回 false");
            Check(EnvCommand.DocumentedSwitchKeys.Length == 22, "文档化的启动期开关是 22 条（RimWorld 1.7 实测 configProperties）");
            Check(EnvCommand.NoteTextFor("WaylithClr.SkipAccessChecks").IndexOf("访问检查", StringComparison.Ordinal) >= 0,
                "已收录开关要给出说明文本(说明表命中)");
            Check(EnvCommand.NoteTextFor("No.Such.Switch").Length > 0, "未收录开关要给出非空兜底说明");
            Check(EnvCommand.NoteTextFor(null).Length > 0, "null 键也要安全给出兜底说明");

            // M5:硬失败单列(读开关抛了)与「没注册」分开记
            Dictionary<string, string> swErr = ly["switchErrors"] as Dictionary<string, string>;
            Check(swErr != null, "layer.switchErrors 应是 Dictionary<string,string>");

            object v2;
            string err2;
            bool got2 = EnvCommand.ReadSwitch("WaylithClr.SkipAccessChecks", out v2, out err2);
            Check(got2 == (v2 != null), "ReadSwitch 三参重载语义不变");
            Check(got2 || err2 == null || err2.Length > 0, "失败时 error 要么为 null(没注册)要么是非空原因(硬失败)");
            bool got3 = EnvCommand.ReadSwitch("", out v2, out err2);
            Check(!got3 && !string.IsNullOrEmpty(err2), "空键要给出非空原因");

            // I1:注册项的 path 必须带 hive 前缀(不再恒等于常量子键)
            List<Dictionary<string, object>> reg = vk_registered(ly);
            foreach (Dictionary<string, object> rg in reg)
            {
                string hive = rg["hive"] as string;
                string path = rg["path"] as string;
                Check(hive == "HKCU" || hive == "HKLM", "hive 应是 HKCU/HKLM，实际 " + Show(hive));
                Check(path != null && path.StartsWith(hive + "\\", StringComparison.Ordinal),
                    "path 应带 hive 前缀，实际 " + Show(path));
                Check(rg["value"] is int || rg["value"] is string || rg["value"] == null, "value 应是 int/string/null");
                break;   // 只看第一条形状
            }

            // 垫片推断的字段形状（值与机器相关,只断言类型与键）
            Dictionary<string, object> shunt = ly["shunt"] as Dictionary<string, object>;
            List<Dictionary<string, object>> rds = shunt["redirects"] as List<Dictionary<string, object>>;
            foreach (Dictionary<string, object> rd in rds)
            {
                Check(rd["sourceLoaded"] is List<string>, "每条重定向都要有 sourceLoaded");
                Check(rd["targetLoaded"] is bool, "每条重定向都要有 targetLoaded");
                Check(rd["inferredActive"] is bool, "每条重定向都要有 inferredActive");
                break;   // 只需要看第一条的形状
            }
            Check(shunt["assemblies"] is Dictionary<string, object>, "shunt.assemblies 是字典");
        }

        private static void WaylithClrReasonCase()
        {
            SlashResult r = Dispatch("/env waylithclr");
            Dictionary<string, object> wc = Sub(r, "waylithClr");
            bool found = wc["found"] as bool? == true;
            Check(found || !string.IsNullOrEmpty(wc["reason"] as string), "found=false 时必须给非空 reason");
            Check(Has(wc, "isSupported") && Has(wc, "isSupportedFrom") && Has(wc, "isSupportedError"),
                "IsSupported 便利键三件套始终存在");
            Check(wc["types"] is List<Dictionary<string, object>>, "types 应是列表");

            List<Type> types = EnvCommand.FindWaylithClrTypes();
            Check(types != null, "FindWaylithClrTypes 不应返回 null");
            Check((types.Count > 0) == found, "found 应与实际找到的类型数一致");

            // M5:硬失败单列(typeErrors 与「本进程没有 WaylithClr」是两件事)
            Check(wc["typeErrors"] is List<string>, "waylithClr.typeErrors 应是 List<string>");
            List<string> typeErrors;
            List<Type> types2 = EnvCommand.FindWaylithClrTypes(out typeErrors);
            Check(typeErrors != null && types2 != null && types2.Count == types.Count, "out 重载与无参重载结果一致");
            List<Dictionary<string, object>> tl = wc["types"] as List<Dictionary<string, object>>;
            foreach (Dictionary<string, object> td in tl)
            {
                Check(Has(td, "nameError"), "每个类型条目都要有 nameError 键");
                break;
            }
        }

        /// <summary>
        /// I2 回归:诊断命令不得把新程序集拉进 AppDomain(已加载集合 = 引用面/短名索引的输入)。
        /// 修复前 ResolveType 里的 Assembly.Load("Microsoft.Win32.Registry") 会把该程序集带进来。
        ///
        /// 两条自我防御(不然这条会对修复前后都红/都绿,零区分力):
        ///   1) **先热身**:/env 的某个分支**可能**带来「BCL 首次使用」式的既有程序集装载(某些运行器里见过
        ///      System.Runtime.InteropServices / System.Text.Encoding.Extensions / System.Threading …;
        ///      也有的运行器增量是 [])—— 它随运行器与已用面而异,是运行时自身代价、与本指令无关。
        ///      **正因为非普适**:既不能据此写断言(会 flaky),也不能不热身就比对集合
        ///      (干净进程里单独跑本用例,那样会对**修复后**的代码也红)。所以先 dispatch 一次 /env args 吃掉可能的首用代价。
        ///   2) 只断言 **load-bearing** 的那一类:名字含 Registry 的程序集不得因本指令进入 AppDomain。
        ///      (热身之后仍可能有个别 BCL 首用增长,不做断言 —— 它对修复前后没有区分力;变异验证见报告。)
        /// </summary>
        private static void NoAssemblyLoadCase()
        {
            Dispatch("/env args");                 // 热身:吃掉可能的 BCL 首用代价(不碰注册表;该代价随运行器而异)

            HashSet<string> before = LoadedNames();
            Dispatch("/env layer");
            Dispatch("/env");
            HashSet<string> after = LoadedNames();

            var added = new List<string>();
            foreach (string n in after) if (!before.Contains(n)) added.Add(n);
            added.Sort(StringComparer.Ordinal);

            var registryAdded = new List<string>();
            foreach (string n in added) if (n.IndexOf("Registry", StringComparison.OrdinalIgnoreCase) >= 0) registryAdded.Add(n);
            Check(registryAdded.Count == 0, "本指令不得新增名字含 Registry 的程序集，实际新增: " + string.Join(", ", registryAdded.ToArray()));
            Check(!after.Contains("Microsoft.Win32.Registry"), "Microsoft.Win32.Registry 不得进 AppDomain(修复前 ResolveType 会带进来)");
            Check(!added.Contains("Microsoft.Win32.Primitives"), "错误文案不得引入 Microsoft.Win32.Primitives(自带码表,不 new Win32Exception)");
        }

        private static HashSet<string> LoadedNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a == null) continue;
                try { set.Add(a.GetName().Name); }
                catch { }
            }
            return set;
        }

        private static List<Dictionary<string, object>> vk_registered(Dictionary<string, object> ly)
        {
            Dictionary<string, object> vk = ly["vulkan"] as Dictionary<string, object>;
            return vk == null ? new List<Dictionary<string, object>>() : (vk["registered"] as List<Dictionary<string, object>>);
        }

        private static void LinesCase()
        {
            SlashResult r = Dispatch("/env");
            Check(r.Lines.Count >= 4, "全列至少有启动参数/环境变量/WaylithClr/层注册/运行时几行");
            Check(StartsWithAny(r.Lines, "启动参数"), "有启动参数段");
            Check(StartsWithAny(r.Lines, "环境变量 WAYLITH_*"), "有环境变量段");
            Check(StartsWithAny(r.Lines, "WaylithClr"), "有 WaylithClr 段");
            Check(StartsWithAny(r.Lines, "层注册"), "有层注册段");
            Check(StartsWithAny(r.Lines, "运行时:"), "有运行时段");
            foreach (string l in r.Lines) Check(l != null, "Lines 里不应有 null");
        }

        private static void RobustCase()
        {
            string[] weird = new string[]
            {
                "/env args waylithclr layer", "/env \targs", "/env 中文", "/env -x", "/env args=",
                "/env  layer  ", "/env waylithclr layer", "/env ", "/env   ", "/env layer extra tokens",
                "/env ARGS", "/env  waylithclr"
            };
            foreach (string s in weird)
            {
                SlashResult r = null;
                bool threw = false;
                try { r = Dispatch(s); }
                catch (Exception) { threw = true; }
                Check(!threw && r != null && r.Kind == "env", "输入 " + Show(s) + " 不应抛且 Kind=env");
            }
        }

        // ---------------- 断言/工具 ----------------

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

        private static SlashResult Dispatch(string input)
        {
            SlashResult r;
            SlashRegistry.TryDispatch(input, new SlashContext { Channel = "test" }, out r);
            return r;
        }

        private static bool Has(Dictionary<string, object> data, string key)
        {
            return data != null && data.ContainsKey(key);
        }

        private static Dictionary<string, object> Sub(SlashResult r, string key)
        {
            if (r == null || r.Data == null || !r.Data.ContainsKey(key)) return null;
            return r.Data[key] as Dictionary<string, object>;
        }

        private static string Scope(string token)
        {
            return EnvCommand.DescribeArg(1, token)["scope"] as string;
        }

        private static bool StartsWithAny(List<string> lines, string prefix)
        {
            foreach (string l in lines) if (l != null && l.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        private static int IndexOfAny(List<string> lines, string needle)
        {
            for (int i = 0; i < lines.Count; i++)
                if (lines[i] != null && lines[i].IndexOf(needle, StringComparison.Ordinal) >= 0) return i;
            return -1;
        }

        private static string Show(string s) { return s == null ? "<null>" : "\"" + s + "\""; }
    }
}
