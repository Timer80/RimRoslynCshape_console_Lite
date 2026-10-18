// RoslynProbe —— Slash harness 的可执行跑手(S11-5b)。
//
// 解决的实测问题:tests/SlashHarness/SlashHarness.csproj 的引用块沿用主工程语义(Private=false,不外带载荷),
// 于是「只引用 SlashHarness.dll 的临时跑手」在反射 GetTypes() 时会抛未捕获的 ReflectionTypeLoadException(进程崩),
// 每人不得不先手工把游戏/引擎/Roslyn 载荷摆到能解析的位置。
//
// 形态 = link(不是 ProjectReference):本工程把 ..\\..\\Source\\**\\*.cs、本目录 **\\*.cs 与本文一起编成
// 单个 SlashHarnessRunner.dll。理由:①全程只有一个程序集,不存在「引用 SlashHarness.dll 但载荷摆不齐」的形态;
// ②不依赖 SlashHarness.csproj 的 OutputPath 语义,也没有跨程序集同名类型(CS0436)噪声。详见 SlashHarnessRunner.csproj 头注释。
//
// 载荷发现(与 csproj 配合,均不写死只在一台机器成立的绝对路径):
//   构建期:RimWorldDir = -p:RimWorldDir > $env:RIMWORLD_DIR > 仓库相对位置(向上四级);拿不到就 <Error> 响亮报错。
//          游戏三件 + CodeAnalysis 四件以 Private=true 拷进输出目录(「自带载荷」),并把 RimWorldDir 写进 payload-root.txt。
//   运行期:AssemblyLoadContext.Default.Resolving 按 [--payload-dir > $env:RIMWORLD_DIR > 输出目录 > payload-root.txt]
//          这几个根,各展开成 <root> / <root>\Mods\Roslyn\Roslyn / <root>\..\..\workshop\content\294100\2009463077\<版本>\Assemblies
//          (版本子目录名从 <root>Version.txt 推;Harmony **真身优先** —— 影子副本 Mods\2009463077\Current\Assemblies 只有
//          --allow-shadow-harmony 才参与,见 docs/slash-fleet-rules.md §10)依次按名字找;
//          未命中会往 stderr 打印 [载荷未命中](含全部探测目录),而不是让异常悄悄冒出来。
//   编译面 == 运行面:启动时把运行面解析到的 0Harmony.dll 与构建期写下的 payload-compile-harmony.txt 里那份比 SHA256,
//          不一致、或落到影子副本目录 ⇒ 响亮报错并以 2 退出 —— 不让 §10 的坑在测试工具里复活。
//
// 用法:dotnet run --project tests/SlashHarness/SlashHarnessRunner.csproj -- --twice
//       退出码:0 = 每遍都 0 失败;1 = 有测试失败;2 = 用法错误 / 载荷缺失 / 编译面≠运行面 / 跑手崩溃。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RoslynProbe.Tests;

namespace RoslynProbe.HarnessRunner
{
    /// <summary>
    /// Slash harness 的进程入口:在同一进程里连续跑 N 遍 <see cref="SlashTestHarness.RunAll"/>,
    /// 每遍逐类打印(测试类自己打印逐类行,本跑手加 [第 N/M 遍] 前缀归因)并打印该遍失败合计;
    /// 只有每一遍都是 0 失败才返回 0。--twice 即 §8.4 的「连续两遍全绿」验收。
    /// </summary>
    public static class RunnerMain
    {
        /// <summary>构建期由 SlashHarnessRunner.csproj 写进输出目录的载荷根标记(内容 = RimWorldDir)。</summary>
        private const string PayloadRootFileName = "payload-root.txt";

        /// <summary>缺了就跑不起来的载荷(与 csproj 里 Private=true 的七件一一对应)。</summary>
        private static readonly string[] RequiredPayloads =
        {
            "Assembly-CSharp.dll",
            "UnityStubs.dll",
            "0Harmony.dll",
            "Microsoft.CodeAnalysis.dll",
            "Microsoft.CodeAnalysis.CSharp.dll",
            "Microsoft.CodeAnalysis.Scripting.dll",
            "Microsoft.CodeAnalysis.CSharp.Scripting.dll",
        };

        /// <summary>游戏侧的传递依赖:没外带,由 Resolving 从载荷根解析;缺了不一定立刻出事,所以只报不拦。</summary>
        private static readonly string[] TransitivePayloads =
        {
            "Waylith.dll",
            "Brutal.Core.Common.dll",
            "RimWorld.dll",
        };

        /// <summary>
        /// 运行期解析不到的程序集名(去重)。测试自造的夹具程序集(W4 的 W4*Dep 一类)解析不到是正常的,
        /// 所以不逐条刷屏:收尾汇总一行,--payload-verbose 才逐次打印。
        /// </summary>
        private static readonly HashSet<string> RuntimeMisses = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>--payload-verbose:逐次打印 Resolving 未命中(排查载荷根时用)。</summary>
        private static bool verbosePayloadMisses;

        /// <summary>Harmony mod 的 workshop 发布号(= 目录名)。</summary>
        private const string HarmonyPublishedFileId = "2009463077";

        /// <summary>影子副本目录后缀:Mods\2009463077\Current\Assemblies 里的 0Harmony 是 2.4.1,不是真身(docs/slash-fleet-rules.md §10)。</summary>
        private static readonly string ShadowHarmonyDirSuffix =
            Path.Combine("Mods", HarmonyPublishedFileId, "Current", "Assemblies");

        /// <summary>构建期写下的「编译面用的 0Harmony.dll」路径标记(启动时用它比对运行面)。</summary>
        private const string CompileHarmonyMarkerFileName = "payload-compile-harmony.txt";

        /// <summary>--allow-shadow-harmony:允许探测并接受影子副本(默认拒绝,见文件头「编译面 == 运行面」)。</summary>
        private static bool allowShadowHarmony;

        public static int Main(string[] args)
        {
            int passes = 1;
            var explicitRoots = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? string.Empty;
                if (arg.Length == 0) continue;
                if (arg == "--twice") { passes = 2; continue; }
                if (arg == "--once") { passes = 1; continue; }
                if (arg == "--payload-verbose") { verbosePayloadMisses = true; continue; }
                if (arg == "--allow-shadow-harmony") { allowShadowHarmony = true; continue; }
                if (arg == "--help" || arg == "-h") { PrintUsage(Console.Out); return 0; }
                if (arg == "--payload-dir")
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("[用法错误] --payload-dir 需要一个目录参数");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    explicitRoots.Add(args[++i]);
                    continue;
                }
                const string payloadDirPrefix = "--payload-dir=";
                if (arg.StartsWith(payloadDirPrefix, StringComparison.Ordinal))
                {
                    explicitRoots.Add(arg.Substring(payloadDirPrefix.Length));
                    continue;
                }
                Console.Error.WriteLine("[用法错误] 未知参数: " + arg);
                PrintUsage(Console.Error);
                return 2;
            }

            List<string> probeDirs;
            try { probeDirs = BuildProbeDirs(explicitRoots); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[载荷错误] 组装探测目录失败: " + ex.GetType().Name + ": " + ex.Message);
                return 2;
            }

            InstallPayloadResolver(probeDirs);

            Console.WriteLine("SlashHarnessRunner: 跑 " + passes + " 遍;载荷探测目录 " + probeDirs.Count + " 个:");
            foreach (string dir in probeDirs) Console.WriteLine("  probe: " + dir);
            Console.WriteLine("载荷自检(必需 " + RequiredPayloads.Length + " 件):");
            int missing = ReportPayloadPresence(probeDirs);
            if (missing > 0)
            {
                Console.Error.WriteLine("[载荷缺失] 有 " + missing + " 个必需载荷在任何探测目录都找不到 —— 编译面与运行面不一致。");
                Console.Error.WriteLine("            处置:指出 RimWorld 根再重跑,例如 $env:RIMWORLD_DIR='<RimWorld 根>' 或 --payload-dir <RimWorld 根>。");
                return 2;
            }

            if (CheckHarmonySurface(probeDirs) != 0) return 2;

            var perPass = new string[passes];
            bool crashed = false;
            bool anyFailures = false;

            for (int pass = 1; pass <= passes; pass++)
            {
                int failures;
                string crash;
                if (!RunOnePass(pass, passes, out failures, out crash))
                {
                    crashed = true;
                    perPass[pass - 1] = "第 " + pass + "/" + passes + " 遍:跑手崩溃";
                    Console.Error.WriteLine("[第 " + pass + "/" + passes + " 遍] 跑手崩溃:" + Environment.NewLine + crash);
                    continue;
                }
                perPass[pass - 1] = "第 " + pass + "/" + passes + " 遍:失败 " + failures;
                if (failures != 0) anyFailures = true;
                Console.WriteLine("[第 " + pass + "/" + passes + " 遍] 失败合计 = " + failures);
            }

            ReportLoadedPayloads();
            bool shadowLoaded = ShadowHarmonyIsLoaded();
            ReportRuntimeMisses();
            Console.WriteLine("===== 结论 =====");
            foreach (string line in perPass) Console.WriteLine("  " + line);
            if (!crashed && !anyFailures && !shadowLoaded)
            {
                Console.WriteLine("  每遍都是 0 失败(§8.4「连续两遍全绿」达成)");
                return 0;
            }
            if (shadowLoaded)
            {
                Console.WriteLine("  未全绿:进程加载了影子副本 0Harmony(编译面≠运行面)");
                return 2;
            }
            Console.WriteLine(crashed ? "  未全绿:跑手崩溃" : "  未全绿:有测试失败");
            return crashed ? 2 : 1;
        }

        /// <summary>
        /// 跑一遍 <see cref="SlashTestHarness.RunAll"/>。整遍输出经前缀 writer 归因到本遍;
        /// 反射型崩溃(ReflectionTypeLoadException)在这里收敛成可读诊断,绝不让它把进程带走。
        /// </summary>
        private static bool RunOnePass(int passNo, int passCount, out int failures, out string crash)
        {
            failures = 0;
            crash = null;

            TextWriter real = Console.Out;
            var tee = new PassPrefixWriter(real, "[第" + passNo + "/" + passCount + "遍] ");
            Console.SetOut(tee);
            try
            {
                failures = SlashTestHarness.RunAll();
                return true;
            }
            catch (ReflectionTypeLoadException ex)
            {
                crash = "ReflectionTypeLoadException: " + ex.Message + Environment.NewLine + DescribeLoaderExceptions(ex);
                return false;
            }
            catch (Exception ex)
            {
                crash = ex.GetType().FullName + ": " + ex.Message + Environment.NewLine + ex.StackTrace;
                return false;
            }
            finally
            {
                try { tee.FlushPending(); } catch { /* 归因前缀写失败不该掩盖本轮结果 */ }
                Console.SetOut(real);
            }
        }

        /// <summary>把 RTLEx 的 LoaderExceptions 去重后逐条列出(逐个报「哪个文件没找到」)。</summary>
        private static string DescribeLoaderExceptions(ReflectionTypeLoadException ex)
        {
            var sb = new StringBuilder();
            Exception[] loaders = ex.LoaderExceptions;
            if (loaders == null || loaders.Length == 0)
            {
                sb.Append("  (LoaderExceptions 为空:类型装载失败但没有给出具体加载器异常)").Append(Environment.NewLine);
                return sb.ToString();
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int shown = 0;
            foreach (Exception e in loaders)
            {
                if (e == null) continue;
                string key = e.GetType().FullName + "|" + e.Message;
                if (!seen.Add(key)) continue;
                shown++;
                sb.Append("  loader[").Append(shown).Append("] ").Append(e.GetType().FullName).Append(": ").Append(e.Message).Append(Environment.NewLine);

                var fnf = e as FileNotFoundException;
                if (fnf != null && !string.IsNullOrEmpty(fnf.FileName))
                    sb.Append("           file: ").Append(fnf.FileName).Append(Environment.NewLine);
                var tle = e as TypeLoadException;
                if (tle != null && !string.IsNullOrEmpty(tle.TypeName))
                    sb.Append("           type: ").Append(tle.TypeName).Append(Environment.NewLine);
            }
            sb.Append("  去重后 ").Append(shown).Append(" 类加载器异常(共 ").Append(loaders.Length).Append(" 条)。");
            return sb.ToString();
        }

        /// <summary>
        /// 载荷探测根:显式参数优先,其次 $env:RIMWORLD_DIR,再次运行目录(Private=true 拷进来的自带载荷),
        /// 最后是构建期写下的 payload-root.txt。再各自展开成三处已知布局。
        /// </summary>
        private static List<string> BuildProbeDirs(List<string> explicitRoots)
        {
            var roots = new List<string>();
            foreach (string root in explicitRoots) AddRoot(roots, root, true);
            AddRoot(roots, Environment.GetEnvironmentVariable("RIMWORLD_DIR"), true);
            AddRoot(roots, AppContext.BaseDirectory, false);

            string marker = Path.Combine(AppContext.BaseDirectory, PayloadRootFileName);
            if (File.Exists(marker))
            {
                string[] lines = File.ReadAllLines(marker);
                if (lines.Length > 0) AddRoot(roots, lines[0], false);
            }

            var dirs = new List<string>();
            foreach (string root in roots)
            {
                AddDir(dirs, root);
                AddDir(dirs, Path.Combine(root, "Mods", "Roslyn", "Roslyn"));
                AddHarmonyDirs(dirs, root);
            }
            return dirs;
        }

        /// <summary>
        /// Harmony 载荷目录:**真身优先**(workshop 的 &lt;发布号&gt;/&lt;版本&gt;/Assemblies)。
        /// 「版本」子目录名从游戏自己的 Version.txt 推(1.7.4979 rev109 ⇒ 1.7);本 mod 的 LoadFolders.xml 对 &lt;v1.7&gt;
        /// 给出的目录也正是 1.7(它给的 `/` 对应 mod 根,本 mod 根下没有 Assemblies)。
        /// 影子副本 Mods/&lt;发布号&gt;/Current/Assemblies(0Harmony 2.4.1,规则 §10)**默认不探**,只有 --allow-shadow-harmony 才加入。
        /// </summary>
        private static void AddHarmonyDirs(List<string> dirs, string root)
        {
            string version = DetectGameVersionShort(root);
            if (version != null)
            {
                AddDir(dirs, Path.Combine(root, "..", "..", "workshop", "content", "294100", HarmonyPublishedFileId, version, "Assemblies"));
                AddDir(dirs, Path.Combine(root, "Mods", HarmonyPublishedFileId, version, "Assemblies"));
                AddDir(dirs, Path.Combine(root, "..", "..", "..", "workshop", "content", "294100", HarmonyPublishedFileId, version, "Assemblies"));
            }
            if (allowShadowHarmony) AddDir(dirs, Path.Combine(root, ShadowHarmonyDirSuffix));
        }

        /// <summary>从 &lt;root&gt;Version.txt 首行读「主.次」版本(1.7.4979 rev109 ⇒ 1.7);读不到返回 null。</summary>
        private static string DetectGameVersionShort(string root)
        {
            try
            {
                string txt = Path.Combine(root, "Version.txt");
                if (!File.Exists(txt)) return null;
                string[] lines = File.ReadAllLines(txt);
                if (lines.Length == 0) return null;
                Match m = Regex.Match(lines[0].Trim(), @"^[0-9]+[.][0-9]+");
                return m.Success ? m.Value : null;
            }
            catch (Exception) { return null; }
        }

        private static void AddRoot(List<string> roots, string dir, bool warnIfMissing)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            string full;
            try { full = Path.GetFullPath(dir.Trim().Trim('"')); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[载荷警告] 载荷根无法解析: " + dir + " (" + ex.Message + ")");
                return;
            }
            if (!Directory.Exists(full))
            {
                if (warnIfMissing) Console.Error.WriteLine("[载荷警告] 载荷根不存在: " + full);
                return;
            }
            foreach (string existing in roots)
                if (string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)) return;
            roots.Add(full);
        }

        private static void AddDir(List<string> dirs, string dir)
        {
            if (!Directory.Exists(dir)) return;
            string full = Path.GetFullPath(dir);
            foreach (string existing in dirs)
                if (string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)) return;
            dirs.Add(full);
        }

        private static void InstallPayloadResolver(List<string> probeDirs)
        {
            AssemblyLoadContext.Default.Resolving += (context, name) => ResolvePayload(context, name, probeDirs);
        }

        private static Assembly ResolvePayload(AssemblyLoadContext context, AssemblyName name, List<string> probeDirs)
        {
            if (name == null || string.IsNullOrEmpty(name.Name)) return null;
            string file = name.Name + ".dll";
            foreach (string dir in probeDirs)
            {
                string path = Path.Combine(dir, file);
                if (!File.Exists(path)) continue;
                try
                {
                    return context.LoadFromAssemblyPath(path);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[载荷加载失败] " + path + " -> " + ex.GetType().Name + ": " + ex.Message);
                    return null;
                }
            }
            RuntimeMisses.Add(name.Name);
            if (verbosePayloadMisses)
                Console.Error.WriteLine("[载荷未命中] " + name.FullName + "(探测目录: " + string.Join(" | ", probeDirs.ToArray()) + ")");
            return null;
        }

        /// <summary>
        /// 「编译面 == 运行面」的 Harmony 自检:打印运行面解析到的 0Harmony.dll(路径 / AssemblyVersion / FileVersion / SHA256),
        /// 与构建期写下的编译面那份比 SHA256;不一致、或运行面落到影子副本目录 ⇒ 响亮报错并返回非 0。
        /// </summary>
        private static int CheckHarmonySurface(List<string> probeDirs)
        {
            string runtime = FindIn(probeDirs, "0Harmony.dll");
            if (runtime == null) return 0;   // 必需件自检已经报过缺件

            string runtimeSha = PayloadSha256(runtime);
            bool shadow = IsShadowHarmonyPath(runtime);

            Console.WriteLine("Harmony 面(编译面 == 运行面 自检):");
            Console.WriteLine("  运行面: " + runtime);
            Console.WriteLine("          AssemblyVersion " + PayloadAssemblyVersion(runtime) + " / FileVersion " + PayloadFileVersion(runtime));
            Console.WriteLine("          SHA256 " + runtimeSha);

            string marker = Path.Combine(AppContext.BaseDirectory, CompileHarmonyMarkerFileName);
            string compilePath = null;
            if (File.Exists(marker))
            {
                string[] lines = File.ReadAllLines(marker);
                if (lines.Length > 0 && lines[0].Trim().Length > 0) compilePath = lines[0].Trim();
            }

            if (compilePath == null)
            {
                Console.Error.WriteLine("  [编译面未知] 输出目录里没有 " + CompileHarmonyMarkerFileName + " —— 无法与编译面比对(构建期写标记失败?)");
            }
            else if (!File.Exists(compilePath))
            {
                Console.Error.WriteLine("  [编译面不可读] " + compilePath + " 不在本机 —— 跳过比对,只能按上面那份的版本判断");
            }
            else
            {
                string compileSha = PayloadSha256(compilePath);
                Console.WriteLine("  编译面: " + compilePath);
                Console.WriteLine("          SHA256 " + compileSha);
                if (!string.Equals(compileSha, runtimeSha, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("[编译面≠运行面] 0Harmony.dll 两份 SHA256 不一致 —— 编译用一个版本、跑的是另一个版本。");
                    Console.Error.WriteLine("            处置:确认 HarmonyPayloadDir 指到真身,再重建跑手(见 tests/SlashHarness/README.md)。");
                    return 1;
                }
            }

            if (compilePath != null && IsShadowHarmonyPath(compilePath))
            {
                if (!allowShadowHarmony)
                {
                    Console.Error.WriteLine("[编译面≠运行面] **编译面**用的就是影子副本 0Harmony(2.4.1):" + compilePath);
                    Console.Error.WriteLine("            处置:别设 -p:SLASH_RUNNER_ALLOW_SHADOW_HARMONY=true(让它取真身 3.0.0);确实要用影子副本才加 --allow-shadow-harmony(§10)。");
                    return 1;
                }
                Console.Error.WriteLine("[载荷警告] 编译面用的是影子副本 0Harmony(--allow-shadow-harmony):" + compilePath + " —— 任何 Harmony 结论不得以影子副本为准(§10)。");
            }

            if (!shadow) return 0;
            if (allowShadowHarmony)
            {
                Console.Error.WriteLine("[载荷警告] 正在用影子副本 0Harmony(--allow-shadow-harmony):" + runtime + " —— 任何 Harmony 结论不得以影子副本为准(§10)。");
                return 0;
            }
            Console.Error.WriteLine("[编译面≠运行面] 运行面的 0Harmony.dll 落在影子副本目录(2.4.1),不是真身 3.0.0:");
            Console.Error.WriteLine("            实际: " + runtime);
            Console.Error.WriteLine("            处置:让 HarmonyPayloadDir 指真身(workshop 的 content/294100/2009463077/<版本>/Assemblies);确实要用影子副本才加 --allow-shadow-harmony(见 docs/slash-fleet-rules.md §10)。");
            return 1;
        }

        /// <summary>打印本进程实际加载的那几份载荷(运行面的直接证据)。</summary>
        private static void ReportLoadedPayloads()
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in RequiredPayloads) wanted.Add(StripDllSuffix(name));
            foreach (string name in TransitivePayloads) wanted.Add(StripDllSuffix(name));

            Console.WriteLine("载荷的加载面(本进程实际加载了哪一份):");
            bool any = false;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                AssemblyName an = asm.GetName();
                if (an == null || an.Name == null || !wanted.Contains(an.Name)) continue;
                string location;
                try { location = asm.IsDynamic ? "(动态程序集)" : asm.Location; }
                catch (Exception) { location = "(不可读)"; }
                Console.WriteLine("  loaded: " + an.Name + " " + an.Version + " <= " + location);
                any = true;
            }
            if (!any) Console.WriteLine("  (本轮测试没有加载这些载荷)");
        }

        /// <summary>进程真的加载了影子副本 0Harmony 吗?(--allow-shadow-harmony 下只警告,不判负)</summary>
        private static bool ShadowHarmonyIsLoaded()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                AssemblyName an = asm.GetName();
                if (an == null || an.Name != "0Harmony") continue;
                string location;
                try { location = asm.IsDynamic ? string.Empty : asm.Location; }
                catch (Exception) { location = string.Empty; }
                if (!IsShadowHarmonyPath(location)) continue;
                if (allowShadowHarmony)
                {
                    Console.Error.WriteLine("[载荷警告] 进程加载的是影子副本 0Harmony:" + location);
                    return false;
                }
                Console.Error.WriteLine("[编译面≠运行面] 进程真的加载了影子副本 0Harmony(2.4.1):" + location);
                Console.Error.WriteLine("            处置:让 HarmonyPayloadDir 指真身并重建;见 docs/slash-fleet-rules.md §10。");
                return true;
            }
            return false;
        }

        private static string StripDllSuffix(string fileName)
        {
            return fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - 4) : fileName;
        }

        private static bool IsShadowHarmonyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return Path.GetFullPath(path).IndexOf(ShadowHarmonyDirSuffix, StringComparison.OrdinalIgnoreCase) >= 0; }
            catch (Exception) { return false; }
        }

        private static string PayloadAssemblyVersion(string path)
        {
            try
            {
                AssemblyName an = AssemblyName.GetAssemblyName(path);
                return an == null || an.Version == null ? "(未知)" : an.Version.ToString();
            }
            catch (Exception ex) { return "(读取失败 " + ex.GetType().Name + ")"; }
        }

        private static string PayloadFileVersion(string path)
        {
            try
            {
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(path);
                if (fvi == null) return "(未知)";
                return (fvi.FileVersion ?? "(空)") + " / product " + (fvi.ProductVersion ?? "(空)");
            }
            catch (Exception ex) { return "(读取失败 " + ex.GetType().Name + ")"; }
        }

        private static string PayloadSha256(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(fs));
            }
            catch (Exception ex) { return "(读取失败 " + ex.GetType().Name + ")"; }
        }

        /// <summary>收尾汇总运行期未命中的程序集名(不刷屏,但也不掩盖)。</summary>
        private static void ReportRuntimeMisses()
        {
            if (RuntimeMisses.Count == 0) return;
            var names = new List<string>(RuntimeMisses);
            names.Sort(StringComparer.Ordinal);
            Console.Error.WriteLine("[载荷] 运行期有 " + names.Count + " 个程序集未能从载荷目录解析: " + string.Join(", ", names.ToArray()));
            Console.Error.WriteLine("        测试自造的夹具程序集(W4 的 W4*Dep 一类)属正常;若名单里有游戏/Roslyn 载荷名,请核对载荷根或 --payload-dir。");
            Console.Error.WriteLine("        加 --payload-verbose 可看到逐次未命中。");
        }

        private static int ReportPayloadPresence(List<string> probeDirs)
        {
            int missing = 0;
            foreach (string name in RequiredPayloads)
            {
                string found = FindIn(probeDirs, name);
                if (found == null)
                {
                    missing++;
                    Console.Error.WriteLine("  [缺] " + name);
                }
                else
                {
                    Console.WriteLine("  " + name + "  <=  " + found);
                }
            }
            foreach (string name in TransitivePayloads)
            {
                string found = FindIn(probeDirs, name);
                Console.WriteLine("  " + name + (found == null ? "  (未预置,按需经 Resolving 解析)" : "  <=  " + found));
            }
            return missing;
        }

        private static string FindIn(List<string> probeDirs, string fileName)
        {
            foreach (string dir in probeDirs)
            {
                string path = Path.Combine(dir, fileName);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static void PrintUsage(TextWriter w)
        {
            w.WriteLine("用法: dotnet run --project tests/SlashHarness/SlashHarnessRunner.csproj -- [选项]");
            w.WriteLine("  --once                        跑一遍 SlashTestHarness.RunAll()(默认)");
            w.WriteLine("  --twice                       同一进程里连续跑两遍(§8.4 幂等验收)");
            w.WriteLine("  --payload-dir <RimWorld 根>   追加载荷探测根,优先于 $env:RIMWORLD_DIR(可重复)");
            w.WriteLine("  --payload-verbose             逐次打印 Resolving 未命中(排查载荷根时用)");
            w.WriteLine("  --allow-shadow-harmony        允许探测/接受影子副本 0Harmony 2.4.1(默认拒绝,§10)");
            w.WriteLine("  -h | --help                   本页");
            w.WriteLine("退出码: 0 = 每遍都 0 失败 | 1 = 有测试失败 | 2 = 用法错误 / 载荷缺失 / 跑手崩溃");
            w.WriteLine("环境: RIMWORLD_DIR 指定 RimWorld 根(优先于构建期由仓库相对位置推出的那个)");
            w.WriteLine("注意: Harmony 默认只认真身 3.0.0(workshop 的 content/294100/2009463077/1.7/Assemblies)");
        }

        /// <summary>
        /// 本遍输出的前缀 writer:逐行把测试类自己打印的逐类行改写成「[第 N/M 遍] 原文」,
        /// 这样两遍的输出不会混成一段无法归因的文本。只加前缀,不改内容。
        /// </summary>
        private sealed class PassPrefixWriter : TextWriter
        {
            private readonly TextWriter inner;
            private readonly string prefix;
            private readonly StringBuilder pending = new StringBuilder();

            public PassPrefixWriter(TextWriter inner, string prefix)
            {
                this.inner = inner;
                this.prefix = prefix;
            }

            public override Encoding Encoding { get { return inner.Encoding; } }

            public override void Write(char value)
            {
                if (value == '\n') { Emit(); return; }
                if (value == '\r') return;
                pending.Append(value);
            }

            public override void Write(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                for (int i = 0; i < value.Length; i++) Write(value[i]);
            }

            public override void WriteLine(string value)
            {
                Write(value);
                Write('\n');
            }

            public override void Flush() { inner.Flush(); }

            /// <summary>把没有换行结尾的残留一行也吐出去(异常路径下会用到)。</summary>
            public void FlushPending()
            {
                if (pending.Length > 0) Emit();
            }

            private void Emit()
            {
                inner.WriteLine(prefix + pending.ToString());
                pending.Clear();
            }
        }
    }
}
