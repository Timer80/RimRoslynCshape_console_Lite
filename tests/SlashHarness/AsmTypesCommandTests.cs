// RoslynProbe —— /asm 与 /types 的零依赖单测。
//
// 约定同 tests/SlashHarness/SlashRegistryTests.cs：不用 xunit（校验工程零 PackageReference），
// 每个测试类暴露 public static int Run() 返回失败数，由 SlashTestHarness 反射统一调用。
// 每个 Run() 首尾 ResetForTests / ResetForTests+RegisterAll，避免与同进程其它用例的表互相污染。
//
// 覆盖：
//   /asm   —— 登记形状、无参全量清单（Lines 与 Data 同源）、filter（name/location 子串、大小写不敏感、无命中）、
//             动态程序集分支（AssemblyBuilder 直接喂 ShapeAssembly，不依赖宿主进程是否真有动态程序集）。
//   /types —— 短名/展示全名（元数后缀、嵌套类型）、索引三元组、Match 的精确/大小写不敏感/无命中/多候选全列、
//             分派（无参 → 用法；无命中 → 空数组；带点的全名不猜）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynProbe;

namespace RoslynProbe.Tests
{
    /// <summary>/asm 的零依赖单测。返回失败数。</summary>
    public static class AsmCommandTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";

        public static int Run()
        {
            Failures.Clear();
            SlashRegistry.ResetForTests();
            AsmCommand.Register();

            Case("登记：verb/kind/usage 与分派命中", RegisterCase);
            Case("无参：全量清单与 Data 形状（Lines 与 Data 同源）", NoFilterCase);
            Case("filter：name/location 子串、大小写不敏感、无命中", FilterCase);
            Case("ShapeAssembly：静态程序集与动态程序集两个分支", ShapeCase);

            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();

            Console.WriteLine("AsmCommandTests: 失败 " + Failures.Count);
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        private static void RegisterCase()
        {
            SlashEntry e;
            Check(SlashRegistry.TryGet("asm", out e), "/asm 应已登记");
            Check(e != null && e.Kind == "asm", "Kind 应为 asm");
            Check(e != null && e.Handler != null, "Handler 不应为 null（不是 stub）");
            // 命名口径（契约 v1.1 §3.5，与 Gc/Diag/Threads 同款的断言）：纯 BCL 指令的 handler 必须叫 Provide。
            Check(typeof(AsmCommand).GetMethod("Provide", BindingFlags.Public | BindingFlags.Static,
                null, new Type[] { typeof(string), typeof(SlashContext) }, null) != null,
                "纯 BCL 指令的 handler 应叫 Provide(契约 v1.1 §3.5)");
            Check(e != null && e.Handler != null && e.Handler.Method.Name == "Provide",
                "登记的 Handler 应就是 Provide 这个方法（防止漂回 Handle），实际 " + Show(e == null || e.Handler == null ? null : e.Handler.Method.Name));
            Check(e != null && e.Usage.StartsWith("[filter] ", StringComparison.Ordinal), "Usage 应以 [filter] 开头");
            Check(e != null && e.Usage.IndexOf("——", StringComparison.Ordinal) > 0, "Usage 应含 —— 分隔符（help 同源要求）");

            SlashResult r;
            Check(SlashRegistry.TryDispatch("/asm", new SlashContext { Channel = "test" }, out r), "/asm 应被分派");
            Check(r != null && r.Kind == "asm", "分派结果的 Kind 应为 asm");
            Check(SlashRegistry.TryDispatch("/ASM", new SlashContext(), out r), "verb 大小写不敏感");
        }

        private static void NoFilterCase()
        {
            SlashResult r;
            Check(SlashRegistry.TryDispatch("/asm", new SlashContext(), out r), "分派 /asm");
            Check(r.Data != null, "Data 不应为 null");
            Check((string)r.Data["filter"] == "", "无 filter 时 Data[filter] 应为空串");

            int count = (int)r.Data["count"];
            int total = (int)r.Data["total"];
            List<Dictionary<string, object>> list = Items(r);
            Check(count == list.Count, "Data[count] 应等于 assemblies 条数");
            Check(total >= count && total > 0, "Data[total] 应 >= count 且 > 0");
            Check(count == total, "无 filter 时 count 应等于 total，实际 " + count + " vs " + total);
            Check(list.Count > 0, "已加载程序集清单不应为空");

            string selfName = typeof(AsmCommand).Assembly.GetName().Name;
            bool sawSelf = false;
            foreach (Dictionary<string, object> item in list)
            {
                Check(item.ContainsKey("name") && item.ContainsKey("version")
                    && item.ContainsKey("location") && item.ContainsKey("dynamic"), "每项应有 name/version/location/dynamic 四键");
                Check(!string.IsNullOrEmpty((string)item["name"]), "name 不应为空");
                Check(item["version"] is string, "version 应是 string");
                Check(item["location"] is string, "location 应是 string");
                Check(item["dynamic"] is bool, "dynamic 应是 bool");
                if ((string)item["name"] == selfName)
                {
                    sawSelf = true;
                    Check((bool)item["dynamic"] == false, "本测试程序集不应是动态程序集");
                    Check(((string)item["location"]).Length > 0, "本测试程序集应有磁盘位置");
                }
            }
            Check(sawSelf, "清单里应含本测试程序集 " + selfName);

            // Lines 与 Data 同源：1 行头 + count 行 + 1 行尾注（无 filter 且非空时没有无匹配行）。
            Check(r.Lines.Count == list.Count + 2,
                "Lines 条数应为 count+2，实际 " + r.Lines.Count + " vs " + (list.Count + 2));
            Check(r.Lines[0].StartsWith("已加载程序集: " + count + " 个", StringComparison.Ordinal),
                "首行应报条数，实际 " + Show(r.Lines[0]));

            // 排序稳定：连续两项 (name, version) 不递减。
            for (int i = 1; i < list.Count; i++)
            {
                int c = string.Compare((string)list[i - 1]["name"], (string)list[i]["name"], StringComparison.OrdinalIgnoreCase);
                Check(c <= 0, "清单应按 name 升序（第 " + i + " 项乱序）");
                if (c != 0) continue;
                Check(string.CompareOrdinal((string)list[i - 1]["version"], (string)list[i]["version"]) <= 0,
                    "同名程序集应按 version 升序");
            }
        }

        private static void FilterCase()
        {
            string selfName = typeof(AsmCommand).Assembly.GetName().Name;
            SlashResult r;

            Check(SlashRegistry.TryDispatch("/asm " + selfName, new SlashContext(), out r), "分派 /asm <name>");
            List<Dictionary<string, object>> hit = Items(r);
            Check(hit.Count >= 1, "按自身程序集名过滤应至少命中 1 条");
            Check((string)r.Data["filter"] == selfName, "Data[filter] 应回显过滤串");
            bool sawSelf = false;
            foreach (Dictionary<string, object> item in hit)
            {
                if ((string)item["name"] == selfName) sawSelf = true;
                Check(AsmCommand.MatchesFilter(item, selfName),
                    "返回项应真的命中过滤串: " + (string)item["name"]);
            }
            Check(sawSelf, "命中集里应有 " + selfName);
            Check((int)r.Data["count"] == hit.Count, "Data[count] 应等于命中条数");
            Check((int)r.Data["total"] >= hit.Count, "Data[total] 应 >= 命中条数");
            Check(r.Lines.Count == hit.Count + 2, "有命中时 Lines 应为 count+2，实际 " + r.Lines.Count);

            // 大小写不敏感：同一集合。
            SlashResult upper;
            Check(SlashRegistry.TryDispatch("/asm " + selfName.ToUpperInvariant(), new SlashContext(), out upper), "分派大写过滤");
            Check(Items(upper).Count == hit.Count, "大小写不敏感：命中条数应一致");

            // 无命中：空数组 + 无匹配行，且 total 仍报全量。
            SlashResult none;
            Check(SlashRegistry.TryDispatch("/asm NoSuchAssembly_ZZZ_W4", new SlashContext(), out none), "分派无命中过滤");
            Check((int)none.Data["count"] == 0, "无命中时 count 应为 0");
            Check(Items(none).Count == 0, "无命中时 assemblies 应为空数组");
            Check((int)none.Data["total"] > 0, "无命中时 total 仍应报全量程序集数");
            bool sawNoMatch = false;
            foreach (string line in none.Lines) if (line.IndexOf("无匹配程序集", StringComparison.Ordinal) >= 0) sawNoMatch = true;
            Check(sawNoMatch, "无命中时应有一行明说无匹配");

            // filter 也覆盖 location（取一条实际程序集的位置尾巴当过滤器）。
            if (hit.Count > 0)
            {
                string loc = (string)hit[0]["location"];
                if (loc.Length > 1)
                {
                    string frag = loc.Substring(loc.Length - 8);
                    SlashResult byLoc;
                    Check(SlashRegistry.TryDispatch("/asm " + frag, new SlashContext(), out byLoc), "分派位置子串过滤");
                    Check(Items(byLoc).Count >= 1, "location 子串过滤应至少命中 1 条（frag=" + frag + "）");
                }
            }
        }

        private static void ShapeCase()
        {
            string dynName = "W4DynProbeAsm_" + Guid.NewGuid().ToString("N").Substring(0, 8);   // 幂等：同进程重跑不撞名
            Assembly dyn = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName(dynName), AssemblyBuilderAccess.Run);
            Dictionary<string, object> d = AsmCommand.ShapeAssembly(dyn);
            Check((bool)d["dynamic"] == true, "动态程序集应报 dynamic=true");
            Check((string)d["name"] == dynName, "动态程序集名应可读，实际 " + Show((string)d["name"]));
            Check((string)d["location"] == "", "动态程序集无磁盘位置（应为空串），实际 " + Show((string)d["location"]));
            Check(d["version"] is string, "动态程序集 version 仍应是 string（可为空）");

            Dictionary<string, object> s = AsmCommand.ShapeAssembly(typeof(AsmCommand).Assembly);
            Check((bool)s["dynamic"] == false, "静态程序集应报 dynamic=false");
            Check(((string)s["location"]).Length > 0, "静态程序集应有磁盘位置");

            Check(AsmCommand.MatchesFilter(d, "W4DynProbe"), "filter 应命中动态程序集名");
            Check(!AsmCommand.MatchesFilter(d, "不可能出现的串"), "不匹配的 filter 应返回 false");
            Check(!AsmCommand.MatchesFilter(null, "x"), "item 为 null 时 Match 应返回 false 而不是抛");
            Check(!AsmCommand.MatchesFilter(s, ""), "空 filter 应返回 false（调用点已保证非空）");
        }

        private static List<Dictionary<string, object>> Items(SlashResult r)
        {
            return (List<Dictionary<string, object>>)r.Data["assemblies"];
        }

        private static void Case(string name, Action body)
        {
            currentCase = name;
            try { body(); }
            catch (Exception ex)
            {
                Failures.Add("[" + name + "] 用例本身抛出: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) Failures.Add("[" + currentCase + "] " + message);
        }

        private static string Show(string s) { return s == null ? "<null>" : "\"" + s + "\""; }
    }

    /// <summary>/types 的零依赖单测。返回失败数。</summary>
    public static class TypesCommandTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";

        public static int Run()
        {
            Failures.Clear();
            SlashRegistry.ResetForTests();
            TypesCommand.Register();

            Case("登记：verb/kind/usage 与分派命中", RegisterCase);
            Case("短名与展示全名：元数后缀、嵌套类型", NamingCase);
            Case("BuildIndex：显式程序集列表 → {fullName, ns, assembly}", BuildCase);
            Case("F1：扫描账本可证伪（scanned+failed=非动态数；明细条数一致）", BookkeepingCase);
            Case("F2：索引失效指纹忽略动态程序集", FingerprintCase);
            Case("观察 1(a)：缺依赖程序集回退 GetTypes 后 salvage 出部分类型", LoaderSalvageCase);
            Case("M3 守卫：一个类型都救不回时记 Failed 而不是 Partial（0-salvage 边界）", ZeroSalvageCase);
            Case("M4 守卫：回退路径只 salvage 对外可见类型（internal 不得进索引）", ExportedFilterCase);
            Case("观察 2：新增非动态程序集必须让索引重建（反向对照）", FreshAssemblyRebuildCase);
            Case("Match：精确 / 大小写不敏感 / 无命中 / 多候选全列", MatchCase);
            Case("分派：无参 → 用法；无命中 → 空数组；带点全名不猜", DispatchCase);
            Case("分派：真实短名命中当前程序集的类型（含大小写兜底）", RealTypeCase);

            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();

            Console.WriteLine("TypesCommandTests: 失败 " + Failures.Count);
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        private static void RegisterCase()
        {
            SlashEntry e;
            Check(SlashRegistry.TryGet("types", out e), "/types 应已登记");
            Check(e != null && e.Kind == "types", "Kind 应为 types");
            Check(e != null && e.Handler != null, "Handler 不应为 null（不是 stub）");
            // 命名口径（契约 v1.1 §3.5，与 Gc/Diag/Threads 同款的断言）：纯 BCL 指令的 handler 必须叫 Provide。
            Check(typeof(TypesCommand).GetMethod("Provide", BindingFlags.Public | BindingFlags.Static,
                null, new Type[] { typeof(string), typeof(SlashContext) }, null) != null,
                "纯 BCL 指令的 handler 应叫 Provide(契约 v1.1 §3.5)");
            Check(e != null && e.Handler != null && e.Handler.Method.Name == "Provide",
                "登记的 Handler 应就是 Provide 这个方法（防止漂回 Handle），实际 " + Show(e == null || e.Handler == null ? null : e.Handler.Method.Name));
            Check(e != null && e.Usage.IndexOf("——", StringComparison.Ordinal) > 0, "Usage 应含 —— 分隔符");

            SlashResult r;
            Check(SlashRegistry.TryDispatch("/types SlashResult", new SlashContext { Channel = "test" }, out r), "/types 应被分派");
            Check(r != null && r.Kind == "types", "分派结果 Kind 应为 types");
            Check(SlashRegistry.TryDispatch("/TYPES SlashResult", new SlashContext(), out r), "verb 大小写不敏感");
        }

        private static void NamingCase()
        {
            Check(TypesCommand.ShortName(typeof(List<>)) == "List", "List 泛型的短名应为 List");
            Check(TypesCommand.ShortName(typeof(Dictionary<,>.Enumerator)) == "Enumerator", "嵌套类型短名应为其叶名");
            Check(TypesCommand.DisplayFullName(typeof(List<>)) == "System.Collections.Generic.List",
                "List 泛型展示全名应为 System.Collections.Generic.List，实际 " + Show(TypesCommand.DisplayFullName(typeof(List<>))));
            Check(TypesCommand.DisplayFullName(typeof(Dictionary<,>.Enumerator)) == "System.Collections.Generic.Dictionary.Enumerator",
                "嵌套泛型展示全名应逐段去元数后缀，实际 " + Show(TypesCommand.DisplayFullName(typeof(Dictionary<,>.Enumerator))));
            Check(TypesCommand.DisplayFullName(typeof(int)) == "System.Int32", "非泛型全名不变");
            Check(TypesCommand.ShortName(typeof(TypesCommandTests)) == "TypesCommandTests", "普通类型短名不变");
        }

        private static void BuildCase()
        {
            TypesCommand.ScanStats stats;
            Dictionary<string, List<TypesCommand.Candidate>> idx =
                TypesCommand.BuildIndex(new Assembly[] { typeof(TypesCommand).Assembly }, out stats);
            Check(stats.AssembliesScanned == 1, "只喂 1 个程序集时 AssembliesScanned 应为 1，实际 " + stats.AssembliesScanned);
            Check(stats.AssembliesFailed == 0 && stats.AssembliesPartial == 0, "单个健康程序集不应有失败/部分账");
            Check(stats.Failures.Count == 0, "无失败时应无明细");
            Check(idx.Count > 0, "索引不应为空");

            string asmName = typeof(TypesCommand).Assembly.GetName().Name;
            Check(idx.ContainsKey("SlashResult"), "索引应含短名 SlashResult");
            List<TypesCommand.Candidate> cands = idx["SlashResult"];
            Check(cands.Count >= 1, "SlashResult 应至少 1 个候选");
            bool sawExact = false;
            foreach (TypesCommand.Candidate c in cands)
            {
                Check(c.Ns == "RoslynProbe", "SlashResult 的 ns 应为 RoslynProbe，实际 " + Show(c.Ns));
                Check(c.FullName == "RoslynProbe.SlashResult", "全名应为 RoslynProbe.SlashResult，实际 " + Show(c.FullName));
                Check(c.Assembly == asmName, "程序集应为 " + asmName + "，实际 " + Show(c.Assembly));
                if (c.FullName == "RoslynProbe.SlashResult") sawExact = true;
            }
            Check(sawExact, "应命中 RoslynProbe.SlashResult");

            // 动态程序集不进索引（BuildIndex 里 IsDynamic 跳过）：null 混合也不应抛。
            TypesCommand.ScanStats nullStats;
            Dictionary<string, List<TypesCommand.Candidate>> empty =
                TypesCommand.BuildIndex(new Assembly[] { null }, out nullStats);
            Check(nullStats.AssembliesScanned == 0 && empty.Count == 0, "全是 null 的程序集列表应得到空索引且不抛");
            Check(nullStats.AssembliesFailed == 0, "null 是跳过不是失败（不该混进失败账）");

            // 动态程序集：有意跳过，既不算成功也不算失败。
            Assembly dynAsm = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("W4TypesDynProbe_" + Guid.NewGuid().ToString("N").Substring(0, 8)), AssemblyBuilderAccess.Run);
            TypesCommand.ScanStats dynStats;
            Dictionary<string, List<TypesCommand.Candidate>> dynIdx =
                TypesCommand.BuildIndex(new Assembly[] { dynAsm }, out dynStats);
            Check(dynStats.AssembliesScanned == 0 && dynStats.AssembliesFailed == 0,
                "动态程序集应被有意跳过（不进成功也不进失败账）");
            Check(dynIdx.Count == 0, "只喂动态程序集应得到空索引");

            // 泛型类型的短名去元数后缀：换 BCL 程序集（它真的声明了 Dictionary / List 的泛型）。
            TypesCommand.ScanStats bclStats;
            Dictionary<string, List<TypesCommand.Candidate>> bcl =
                TypesCommand.BuildIndex(new Assembly[] { typeof(Dictionary<,>).Assembly }, out bclStats);
            Check(bclStats.AssembliesScanned == 1, "只喂 BCL 一个程序集时 AssembliesScanned 应为 1，实际 " + bclStats.AssembliesScanned);
            Check(bcl.ContainsKey("Dictionary"), "索引应按去元数后缀的短名建键：Dictionary 泛型的键是 Dictionary");
            Check(bcl.ContainsKey("List"), "索引应按去元数后缀的短名建键：List 泛型的键是 List");
            Check(!bcl.ContainsKey("Dictionary" + "`2"), "索引键不应保留元数后缀");
        }

        private static void BookkeepingCase()
        {
            // 抓一份当前程序集快照，对**同一个数组**建索引：账本必须守恒（不依赖缓存状态，可复现）。
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            int nonDynamic = 0;
            foreach (Assembly asm in all)
            {
                if (asm == null) continue;
                bool dynamic;
                try { dynamic = asm.IsDynamic; }
                catch { dynamic = false; }   // 与 BuildIndex 记账一致：IsDynamic 抛 → 计进失败账
                if (!dynamic) nonDynamic++;
            }
            Check(nonDynamic > 0, "活进程里应至少 1 个非动态程序集");

            TypesCommand.ScanStats st;
            TypesCommand.BuildIndex(all, out st);
            Check(st.AssembliesScanned > 0, "应至少扫成功 1 个程序集");
            Check(st.AssembliesScanned + st.AssembliesFailed == nonDynamic,
                "账本守恒：AssembliesScanned+AssembliesFailed 应等于非动态程序集数，实际 "
                + st.AssembliesScanned + "+" + st.AssembliesFailed + " vs " + nonDynamic);
            Check(st.AssembliesPartial <= st.AssembliesScanned, "部分成功者应计入 AssembliesScanned");
            Check(st.Failures.Count == st.AssembliesFailed + st.AssembliesPartial,
                "明细条数应等于 失败+部分（不多不少），实际 " + st.Failures.Count + " vs "
                + (st.AssembliesFailed + st.AssembliesPartial));
            foreach (Dictionary<string, object> f in st.Failures)
            {
                Check(f.ContainsKey("name") && f.ContainsKey("error") && f.ContainsKey("partial"),
                    "每条明细应有 name/error/partial 三键");
                Check(!string.IsNullOrEmpty((string)f["name"]), "失败程序集应具名，原因：" + (string)f["error"]);
                Check(!string.IsNullOrEmpty((string)f["error"]), "每条明细应给出原因");
            }
        }

        private static void FingerprintCase()
        {
            Assembly core = typeof(TypesCommand).Assembly;
            Assembly bcl = typeof(Dictionary<,>).Assembly;
            Assembly dyn = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("W4FingerprintDynProbe_" + Guid.NewGuid().ToString("N").Substring(0, 8)), AssemblyBuilderAccess.Run);

            string fpCore = TypesCommand.Fingerprint(new Assembly[] { core });
            Check(fpCore.Length > 0, "非动态程序集的指纹不应为空");
            Check(fpCore == TypesCommand.Fingerprint(new Assembly[] { core, dyn }),
                "F2：指纹应忽略动态程序集（Roslyn 每次脚本提交都不该让索引重建）");
            Check(fpCore == TypesCommand.Fingerprint(new Assembly[] { dyn, core, null }),
                "F2：动态项与 null 项都应被忽略，且与顺序无关");
            Check(fpCore != TypesCommand.Fingerprint(new Assembly[] { core, bcl }),
                "多一个非动态程序集必须改变指纹（否则该重建的不重建）");
            Check(TypesCommand.Fingerprint(null) == "", "null 输入指纹应为空串");

            // 端到端：索引收敛后，再冒出一个动态程序集（= Roslyn 脚本提交）不该让索引重建。
            // 用**实例同一性**断言，因为重建后的统计可能与旧的一致（症状只能这样证）。
            Dictionary<string, List<TypesCommand.Candidate>> stable = null;
            for (int i = 0; i < 8; i++)
            {
                TypesCommand.ScanStats ignored;
                Dictionary<string, List<TypesCommand.Candidate>> cur = TypesCommand.EnsureIndex(out ignored);
                if (stable != null && object.ReferenceEquals(stable, cur)) { stable = cur; break; }
                stable = cur;
            }
            Check(stable != null, "索引应能构建出来（活进程）");

            Assembly dynAfterWarm = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("W4CacheDynProbe_" + Guid.NewGuid().ToString("N").Substring(0, 8)), AssemblyBuilderAccess.Run);
            TypesCommand.ScanStats ignoredAfter;
            Dictionary<string, List<TypesCommand.Candidate>> afterDyn = TypesCommand.EnsureIndex(out ignoredAfter);
            Check(object.ReferenceEquals(stable, afterDyn),
                "F2：索引收敛后新增动态程序集，" + dynAfterWarm.GetName().Name + " 不该让索引重建（缓存应命中同一实例）");
        }

        /// <summary>
        /// 用 Roslyn 现场编一个 DLL 到内存（测试夹具）—— 用来造「全新非动态程序集」（观察 2）与
        /// 「缺依赖程序集」（观察 1）。Roslyn 的引用在 tests/SlashHarness/SlashHarness.csproj 与 tests/RoslynProbe.Tests.csproj
        /// 里都有（两侧都引 Microsoft.CodeAnalysis.CSharp），所以这里不需要额外依赖。
        /// </summary>
        private static byte[] EmitLib(string asmName, string source, byte[] depBytes)
        {
            var refs = new List<MetadataReference>
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location)   // 与 corelib 不同的另一件，避免重复引用
            };
            if (depBytes != null) refs.Add(MetadataReference.CreateFromImage(depBytes));

            var compilation = CSharpCompilation.Create(asmName,
                new[] { CSharpSyntaxTree.ParseText(source) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using (var ms = new MemoryStream())
            {
                var res = compilation.Emit(ms);   // EmitResult 在 Microsoft.CodeAnalysis.Emit 里，用 var 免一个 using
                Check(res.Success, "夹具应能编出 " + asmName + "：" + FirstDiag(res.Diagnostics));
                return ms.ToArray();
            }
        }

        private static string FirstDiag(IEnumerable<Diagnostic> diags)
        {
            foreach (Diagnostic d in diags)
                if (d.Severity == DiagnosticSeverity.Error) return d.ToString();
            return "(无 error 诊断)";
        }

        /// <summary>夹具命名后缀：每轮 RunAll 都是新名字（审查观察 2：同进程第二遍必须也绿）。</summary>
        private static string NewSuffix()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static void LoaderSalvageCase()
        {
            // 夹具：DepLib 独立编出来但**不加载**；MainLib 里一个类型派生自 DepLib，另一个不依赖它。
            // 只加载 MainLib ⇒ GetExportedTypes 抛原始 loader 异常（.NET 10 实测），回退 GetTypes()
            // 抛 ReflectionTypeLoadException 且 ex.Types 里不依赖缺失引用的那个类型是可用槽。
            string suffix = NewSuffix();
            string depAsm = "W4SalvageDep_" + suffix;
            string mainAsm = "W4SalvageMain_" + suffix;
            byte[] depBytes = EmitLib(depAsm,
                "namespace W4SalvageDep { public class DepBase { } }", null);
            byte[] mainBytes = EmitLib(mainAsm,
                "namespace W4SalvageMain { public class Broken : W4SalvageDep.DepBase { } public class Fine { } }",
                depBytes);

            Assembly main = Assembly.Load(mainBytes);
            Check(!main.IsDynamic, "夹具应是非动态程序集");

            TypesCommand.ScanStats st;
            Dictionary<string, List<TypesCommand.Candidate>> idx =
                TypesCommand.BuildIndex(new Assembly[] { main }, out st);

            Check(st.AssembliesPartial == 1,
                "观察 1(a)：缺依赖但能 salvage 的程序集应记 AssembliesPartial=1，实际 partial="
                + st.AssembliesPartial + " failed=" + st.AssembliesFailed);
            Check(st.AssembliesFailed == 0, "救回至少一个类型就不该记完全失败");
            Check(st.AssembliesScanned == 1, "部分成功者应计入 AssembliesScanned");
            Check(st.Failures.Count == 1, "应恰好一条账本明细，实际 " + st.Failures.Count);
            if (st.Failures.Count >= 1)
            {
                Check((bool)st.Failures[0]["partial"] == true, "该明细应标 partial=true");
                Check((string)st.Failures[0]["name"] == mainAsm, "明细应点名缺依赖的那个程序集 " + mainAsm);
                Check(((string)st.Failures[0]["error"]).IndexOf("回退 GetTypes()", StringComparison.Ordinal) >= 0,
                    "明细应说明是回退 GetTypes() 救的，实际 " + Show((string)st.Failures[0]["error"]));
            }
            Check(idx.ContainsKey("Fine"), "salvage 应保住不依赖缺失引用的 Fine");
            Check(!idx.ContainsKey("Broken"), "加载不出来的 Broken 不该被当成可用候选");
        }

        private static void FreshAssemblyRebuildCase()
        {
            // 反向对照（审查观察 2）：先让索引收敛，再塞进一个**全新的非动态程序集**；
            // 指纹必须随之改变 ⇒ EnsureIndex 必须重建（否则有人把缓存改成永久缓存也能全绿）。
            // 名字带 Guid ⇒ 同进程第二遍 RunAll 时"旧索引里没有这个类型"依然成立（幂等）。
            string suffix = NewSuffix();
            string typeName = "FreshMade" + suffix;
            string asmName = "W4FreshNonDyn_" + suffix;

            Dictionary<string, List<TypesCommand.Candidate>> stable = null;
            for (int i = 0; i < 8; i++)
            {
                TypesCommand.ScanStats ignored;
                Dictionary<string, List<TypesCommand.Candidate>> cur = TypesCommand.EnsureIndex(out ignored);
                if (stable != null && object.ReferenceEquals(stable, cur)) { stable = cur; break; }
                stable = cur;
            }
            Check(stable != null, "索引应能收敛");
            Check(!stable.ContainsKey(typeName), "收敛后的旧索引里本来不该有新类型（对照前提）：" + typeName);

            byte[] bytes = EmitLib(asmName,
                "namespace W4FreshNonDyn { public class " + typeName + " { } }", null);
            Assembly fresh = Assembly.Load(bytes);
            Check(!fresh.IsDynamic, "夹具程序集应是非动态的");

            TypesCommand.ScanStats stats;
            Dictionary<string, List<TypesCommand.Candidate>> after = TypesCommand.EnsureIndex(out stats);
            Check(!object.ReferenceEquals(stable, after),
                "观察 2：新增非动态程序集必须让索引重建（同一实例 = 永久缓存，判定失败）");
            Check(after.ContainsKey(typeName), "重建后的索引应含新程序集的类型 " + typeName);
        }

        private static void ZeroSalvageCase()
        {
            // 0-salvage 边界（核对者 M3 的守卫）：GetExportedTypes 失败 → 回退 GetTypes 抛 RTLEx，
            // 但**一个对外可见类型都救不回** ⇒ 必须记 AssembliesFailed，不能因为"走到了 RTLEx 分支"就算 partial。
            // 夹具：唯一一个类型派生自不加载的 DepLib ⇒ ex.Types 只有一个 null 槽。
            string suffix = NewSuffix();
            string depAsm = "W4ZeroDep_" + suffix;
            string mainAsm = "W4ZeroMain_" + suffix;
            byte[] depBytes = EmitLib(depAsm, "namespace W4ZeroDep { public class DepBase { } }", null);
            byte[] mainBytes = EmitLib(mainAsm,
                "namespace W4ZeroMain { public class OnlyBroken : W4ZeroDep.DepBase { } }", depBytes);

            Assembly main = Assembly.Load(mainBytes);
            Check(!main.IsDynamic, "夹具应是非动态程序集");

            TypesCommand.ScanStats st;
            Dictionary<string, List<TypesCommand.Candidate>> idx =
                TypesCommand.BuildIndex(new Assembly[] { main }, out st);

            Check(st.AssembliesPartial == 0,
                "M3 守卫：一个类型都没救回时 AssembliesPartial 必须为 0，实际 " + st.AssembliesPartial);
            Check(st.AssembliesFailed == 1, "应记 AssembliesFailed=1，实际 " + st.AssembliesFailed);
            Check(st.AssembliesScanned == 0, "完全失败的程序集不该计入 AssembliesScanned，实际 " + st.AssembliesScanned);
            Check(st.Failures.Count == 1, "应恰好一条账本明细，实际 " + st.Failures.Count);
            if (st.Failures.Count >= 1)
            {
                Check((bool)st.Failures[0]["partial"] == false, "该明细应标 partial=false");
                Check((string)st.Failures[0]["name"] == mainAsm, "明细应点名 " + mainAsm);
                Check(((string)st.Failures[0]["error"]).IndexOf("一个也保不住", StringComparison.Ordinal) >= 0,
                    "明细应写「一个也保不住」，实际 " + Show((string)st.Failures[0]["error"]));
            }
            Check(idx.Count == 0, "没有任何候选该进索引，实际 " + idx.Count);
        }

        private static void ExportedFilterCase()
        {
            // 过滤语义（核对者 M4 的守卫）：回退路径只能把**对外可见**的类型 salvage 进来；
            // internal 类型（IsVisible=false）不得进索引，否则 /types 会把查不到也用不上的类型当候选。
            // 夹具：一个 Broken（依赖缺失）+ 一个 internal + 一个 public，且 DepLib 不加载（逼出回退路径）。
            string suffix = NewSuffix();
            string depAsm = "W4FilterDep_" + suffix;
            string mainAsm = "W4FilterMain_" + suffix;
            string shownName = "Shown" + suffix;
            string hiddenName = "Hidden" + suffix;
            byte[] depBytes = EmitLib(depAsm, "namespace W4FilterDep { public class DepBase { } }", null);
            byte[] mainBytes = EmitLib(mainAsm,
                "namespace W4FilterMain { public class Broken : W4FilterDep.DepBase { } "
                + "internal class " + hiddenName + " { } public class " + shownName + " { } }", depBytes);

            Assembly main = Assembly.Load(mainBytes);
            Check(!main.IsDynamic, "夹具应是非动态程序集");

            // 前置断言（防守卫静默失效）：本用例的「internal 不得进索引」只有在
            // **internal 类型真的出现在类型表里（非 null 槽）**时才有意义 —— 若哪天 CLR 对这个形态给 null 槽，
            // 那条断言会变成 vacuous（前提没了却仍绿）。所以先把前提本身钉死。
            bool hiddenSlotNonNull = false;
            bool shownSlotNonNull = false;
            Type[] rawTypes = null;
            try { rawTypes = main.GetTypes(); }
            catch (ReflectionTypeLoadException rtlex) { rawTypes = rtlex.Types; }   // 本夹具必走这条：Broken 依赖缺失
            if (rawTypes != null)
            {
                foreach (Type t in rawTypes)
                {
                    if (t == null) continue;
                    if (t.Name == hiddenName) hiddenSlotNonNull = true;
                    if (t.Name == shownName) shownSlotNonNull = true;
                }
            }
            Check(hiddenSlotNonNull,
                "前置：internal 类型 " + hiddenName + " 必须在 GetTypes()/RTLEx.Types 里有非 null 槽（否则 M4 守卫静默失效）");
            Check(shownSlotNonNull,
                "前置：public 类型 " + shownName + " 必须在 GetTypes()/RTLEx.Types 里有非 null 槽");

            TypesCommand.ScanStats st;
            Dictionary<string, List<TypesCommand.Candidate>> idx =
                TypesCommand.BuildIndex(new Assembly[] { main }, out st);

            Check(st.AssembliesPartial == 1, "回退路径应记 partial=1，实际 " + st.AssembliesPartial);
            Check(st.AssembliesFailed == 0, "救回 public 类型就不该记失败");
            Check(idx.ContainsKey(shownName), "public 类型必须被 salvage 进来");
            Check(!idx.ContainsKey(hiddenName),
                "M4 守卫：internal 类型不得进索引（IsVisible=false），实际进了：" + hiddenName);
            Check(!idx.ContainsKey("Broken"), "依赖缺失的类型不得进索引");
            if (idx.ContainsKey(shownName))
            {
                TypesCommand.Candidate c = idx[shownName][0];
                Check(c.Ns == "W4FilterMain", "salvage 进来的类型命名空间应为 W4FilterMain，实际 " + Show(c.Ns));
                Check(c.Assembly == mainAsm, "程序集字段应为 " + mainAsm + "，实际 " + Show(c.Assembly));
            }
        }

        private static void MatchCase()
        {
            string mode;
            var syn = new Dictionary<string, List<TypesCommand.Candidate>>(StringComparer.Ordinal);
            syn["Dup"] = new List<TypesCommand.Candidate>
            {
                new TypesCommand.Candidate { ShortName = "Dup", FullName = "B.Ns.Dup", Ns = "B.Ns", Assembly = "Beta" },
                new TypesCommand.Candidate { ShortName = "Dup", FullName = "A.Ns.Dup", Ns = "A.Ns", Assembly = "Alpha" }
            };

            List<TypesCommand.Candidate> h1 = TypesCommand.Match(syn, "Dup", out mode);
            Check(mode == "exact", "精确命中 mode 应为 exact，实际 " + Show(mode));
            Check(h1.Count == 2, "多候选应全部列出（不猜一个），实际 " + h1.Count);
            Check(h1[0].Ns == "A.Ns" && h1[1].Ns == "B.Ns", "候选应稳定按 ns 升序（即使喂进来的表没排序）");

            List<TypesCommand.Candidate> h2 = TypesCommand.Match(syn, "dup", out mode);
            Check(mode == "ignorecase", "大小写不敏感兜底 mode 应为 ignorecase，实际 " + Show(mode));
            Check(h2.Count == 2, "大小写不敏感命中也应列全部候选");

            List<TypesCommand.Candidate> h3 = TypesCommand.Match(syn, "Nope", out mode);
            Check(mode == "none" && h3.Count == 0, "无命中应返回空列表且 mode=none");

            Check(TypesCommand.Match(null, "Dup", out mode).Count == 0, "索引为 null 应返回空列表而不是抛");
            Check(TypesCommand.Match(syn, "", out mode).Count == 0, "空短名应返回空列表");
            Check(TypesCommand.Match(syn, null, out mode).Count == 0, "null 短名应返回空列表");

            // 真实索引上再验一次大小写兜底（与精确解一致）。
            TypesCommand.ScanStats realStats;
            Dictionary<string, List<TypesCommand.Candidate>> real =
                TypesCommand.BuildIndex(new Assembly[] { typeof(TypesCommand).Assembly }, out realStats);
            List<TypesCommand.Candidate> exactHits = TypesCommand.Match(real, "SlashResult", out mode);
            List<TypesCommand.Candidate> lowerHits = TypesCommand.Match(real, "slashresult", out mode);
            Check(exactHits.Count == lowerHits.Count, "精确与大小写兜底应给出同一集合");
            Check(lowerHits.Count >= 1, "slashresult 兜底应至少命中 1 条");
        }

        private static void DispatchCase()
        {
            SlashResult r;
            Check(SlashRegistry.TryDispatch("/types", new SlashContext(), out r), "分派无参 /types");
            Check(r.Kind == "types", "用法结果的 Kind 仍应是 types");
            Check(r.Data != null && r.Data.ContainsKey("usage") && (bool)r.Data["usage"], "无参应返回用法（Data[usage]=true）");
            Check(r.Lines.Count >= 2, "用法应给出至少 2 行");
            Check(!r.Data.ContainsKey("candidates"), "用法结果不应带候选键");

            Check(SlashRegistry.TryDispatch("/types NoSuchShortNameZzz", new SlashContext(), out r), "分派无命中 /types");
            Check((string)r.Data["short"] == "NoSuchShortNameZzz", "Data[short] 应回显查询串");
            Check((int)r.Data["count"] == 0, "无命中 count 应为 0");
            Check(((List<Dictionary<string, object>>)r.Data["candidates"]).Count == 0, "无命中 candidates 应为空数组");
            Check((int)r.Data["assembliesScanned"] > 0, "应报告扫描了多少个程序集");
            Check(r.Data.ContainsKey("assembliesFailed"), "Data 应有 assembliesFailed（F1 的可证伪账本）");
            Check(r.Data.ContainsKey("assembliesPartial"), "Data 应有 assembliesPartial（RTLEx 部分成功）");
            Check(r.Data.ContainsKey("assemblyFailures"), "Data 应有 assemblyFailures 明细");
            Check((int)r.Data["assembliesFailed"] >= 0 && (int)r.Data["assembliesPartial"] >= 0, "账本应为非负整数");
            List<Dictionary<string, object>> failures = (List<Dictionary<string, object>>)r.Data["assemblyFailures"];
            Check(failures.Count == (int)r.Data["assembliesFailed"] + (int)r.Data["assembliesPartial"],
                "明细条数应等于 失败 + 部分（可证伪：不多不少）");
            foreach (Dictionary<string, object> f in failures)
            {
                Check(f.ContainsKey("name") && f.ContainsKey("error") && f.ContainsKey("partial"),
                    "每条明细应有 name/error/partial 三键");
                Check(!string.IsNullOrEmpty((string)f["error"]), "每条明细应给出失败原因");
            }
            Check((int)r.Data["assembliesPartial"] <= (int)r.Data["assembliesScanned"],
                "部分成功者应包含在成功计数里（scanned = 完全成功 + 部分成功）");

            Check(SlashRegistry.TryDispatch("/types System.Collections.Generic.List", new SlashContext(), out r), "分派带点全名");
            Check((int)r.Data["count"] == 0, "带命名空间的全名不查（不猜）→ 候选为空");
            bool hinted = false;
            foreach (string line in r.Lines) if (line.IndexOf("短名", StringComparison.Ordinal) >= 0) hinted = true;
            Check(hinted, "带点查询应提示只按短名匹配");
        }

        private static void RealTypeCase()
        {
            SlashResult r;
            Check(SlashRegistry.TryDispatch("/types SlashResult", new SlashContext(), out r), "分派 /types SlashResult");
            List<Dictionary<string, object>> cands = (List<Dictionary<string, object>>)r.Data["candidates"];
            Check(cands.Count >= 1, "SlashResult 应至少 1 个候选");
            Check((int)r.Data["count"] == cands.Count, "Data[count] 应等于候选条数");
            bool sawSelf = false;
            foreach (Dictionary<string, object> c in cands)
            {
                Check(c.ContainsKey("fullName") && c.ContainsKey("ns") && c.ContainsKey("assembly"),
                    "每项应有 fullName/ns/assembly 三键");
                if ((string)c["fullName"] == "RoslynProbe.SlashResult")
                {
                    sawSelf = true;
                    Check((string)c["ns"] == "RoslynProbe", "ns 应为 RoslynProbe");
                    Check((string)c["assembly"] == typeof(TypesCommand).Assembly.GetName().Name, "assembly 应是当前程序集");
                }
            }
            Check(sawSelf, "候选里应有 RoslynProbe.SlashResult");

            // 大小写兜底：同一集合。
            SlashResult lower;
            Check(SlashRegistry.TryDispatch("/types slashresult", new SlashContext(), out lower), "分派小写 /types");
            Check(((List<Dictionary<string, object>>)lower.Data["candidates"]).Count == cands.Count,
                "大小写兜底应给出与精确一致的候选数");
            bool noted = false;
            foreach (string line in lower.Lines) if (line.IndexOf("大小写不敏感", StringComparison.Ordinal) >= 0) noted = true;
            Check(noted, "大小写兜底应在行里明说");

            // 多候选：List 至少要有 BCL 的 System.Collections.Generic.List 一条，且排序稳定。
            Check(SlashRegistry.TryDispatch("/types List", new SlashContext(), out r), "分派 /types List");
            List<Dictionary<string, object>> lists = (List<Dictionary<string, object>>)r.Data["candidates"];
            Check(lists.Count >= 1, "List 应至少 1 个候选");
            bool sawBcl = false;
            foreach (Dictionary<string, object> c in lists)
                if ((string)c["fullName"] == "System.Collections.Generic.List") sawBcl = true;
            Check(sawBcl, "候选里应有 System.Collections.Generic.List");
            for (int i = 1; i < lists.Count; i++)
            {
                string p = (string)lists[i - 1]["fullName"], q = (string)lists[i]["fullName"];
                Check(string.CompareOrdinal(p, q) <= 0, "候选应按 fullName 升序（第 " + i + " 项乱序: " + p + " > " + q + "）");
            }
            if (lists.Count > 1)
            {
                bool multi = false;
                foreach (string line in r.Lines) if (line.IndexOf("多候选不猜", StringComparison.Ordinal) >= 0) multi = true;
                Check(multi, "多候选时应在行里明说不猜");
            }
        }

        private static void Case(string name, Action body)
        {
            currentCase = name;
            try { body(); }
            catch (Exception ex)
            {
                Failures.Add("[" + name + "] 用例本身抛出: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) Failures.Add("[" + currentCase + "] " + message);
        }

        private static string Show(string s) { return s == null ? "<null>" : "\"" + s + "\""; }
    }
}
