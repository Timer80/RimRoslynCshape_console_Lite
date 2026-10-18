// RoslynProbe —— /env:启动期锁定的事实(启动参数 + WAYLITH_* 环境变量 + WaylithClr 兼容层 + 运行时 + Vulkan 层注册)。
//
// 纯 BCL/反射,不需要姿势 B(TOOLSTACK-TO-SLASH-1.7 §1 那一行:「启动期锁定 ⇒ 只能读(→ /env)」)。
// 本文件不碰 Verse/Unity/ConsoleLog:这些事实全都能从进程内读出来 —— Environment / AppContext /
// AppDomain+反射 / 只读文件 / 注册表。线程契约:全部只读、无副作用,三个通道(游戏线程或读线程)都可直接调。
//
// 读不到的**不抛异常**:一律写进 Data 的 reason 字段并在 Lines 里明确印出「不可得」。两类典型:
//   * WaylithClr 的 ReJIT 面定义在 System.Private.CoreLib.dll(只有 Waylith 运行时才有)⇒ 普通 CoreCLR 下找不到类型;
//   * Vulkan 隐式层注册项在 HKCU/HKLM 注册表里,而 Microsoft.Win32.Registry 不在 net10.0 基础框架引用面内
//     (本工程/校验工程零 PackageReference)⇒ 用 P/Invoke advapi32 读,读不到就写 reason。
//
// **不得扰动被测系统**:本指令绝不 Assembly.Load,也绝不改任何进程状态 —— 已加载程序集集合是引用面/短名索引的
// 输入(NOTE-reference-surface),诊断命令把它改掉就会改变后续求值的结果。注册表因此走 P/Invoke advapi32:
// 只开只读句柄、立即 RegCloseKey;**P/Invoke 路径与错误文案都不引入任何程序集**(错误码用自带小码表,
// 刻意不 new Win32Exception —— 它首次调用会引入 Microsoft.Win32.Primitives)。
// 说明:「BCL 首次使用」可能带来既有程序集的装载(某些运行器里见过 System.Runtime.InteropServices /
// System.Text.Encoding.Extensions / System.Threading),它随运行器与已用面而异,是运行时自身代价、
// 与本指令无关(哪个分支都一样),因此**不作为断言**(见测试里 NoAssemblyLoadCase 的注释)。
//
// 事实来源(TEXT38 文档,逐条已在源码/实测里核对过):
//   STOCK-TOOLS-1.7.md §1 引擎开关 20 条 / §2 游戏开关 10 条 / §3 环境变量 11 条;
//   KNOWN-1.7.md §2 启动器命令行开关(含 -mod= / -disable-compute-shaders);
//   DEBUG-BREAKPOINTS-1.7.md §4 + RimWorld.runtimeconfig.json 的 22 条 configProperties;
//   RENDERDOC-PREP-1.7.md §2.4 Vulkan 层注册(HKCU/HKLM\SOFTWARE\Khronos\Vulkan\ImplicitLayers)。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace RoslynProbe
{
    /// <summary>
    /// <c>/env [args|waylithclr|layer]</c> —— 启动期锁定的事实:启动参数(含引擎/游戏/mod 旗标标注)、
    /// <c>WAYLITH_*</c> 环境变量、<c>WaylithClr</c> 静态面、运行时基线、层注册。
    /// 无参 = 三段全列 + 运行时基线;子参数只渲染自己那一段。
    /// </summary>
    public static class EnvCommand
    {
        internal const string Verb = "env";
        internal const string Kind = "env";

        /// <summary>RegisterAll 反射调用(无参、public static、返回 void)。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = Verb,
                Usage = "[args|waylithclr|layer] —— 启动参数、WAYLITH_* 环境变量、WaylithClr 兼容层、运行时与层注册",
                Group = "运行时",
                Kind = Kind,
                Handler = Provide
            });
        }

        // ==================== 分派 ====================

        /// <summary>指令实现(契约 v1.1 §3.5:纯 BCL 指令的 handler 也叫 Provide,与 Gc/Diag/Threads 同形)。</summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            string extra;
            string mode = ParseMode(rest, out extra);
            if (mode == null) return Usage(rest);

            var r = new SlashResult { Kind = Kind };
            var data = new Dictionary<string, object>();
            data["mode"] = mode.Length == 0 ? "all" : mode;

            if (mode.Length == 0 || mode == "args") IncludeArgs(data, r.Lines);
            if (mode.Length == 0 || mode == "waylithclr") IncludeWaylithClr(data, r.Lines);
            if (mode.Length == 0 || mode == "layer") IncludeLayer(data, r.Lines);

            // 运行时基线:四条路都在 Data 里给(规格把 runtime 与 args/env/waylithClr 并列),
            // 但 Lines 只在无参(全列)时渲染它 —— 子参数承诺「只看一段」。
            IncludeRuntime(data);
            if (mode.Length == 0) RenderRuntime((Dictionary<string, object>)data["runtime"], r.Lines);

            if (!string.IsNullOrEmpty(extra)) r.Lines.Add("（忽略多余参数: " + extra + "）");

            data["ok"] = true;
            r.Data = data;
            return r;
        }

        /// <summary>
        /// 解析子参数。返回 <c>""</c>(无参 ⇒ 全列)/ <c>"args"</c> / <c>"waylithclr"</c> / <c>"layer"</c>(已小写化);
        /// 不认识的 token 返回 <c>null</c>(调用点给用法页,绝不静默猜)。<paramref name="extra"/> 收第 2 个 token 起的多余参数。
        /// </summary>
        internal static string ParseMode(string rest, out string extra)
        {
            extra = null;
            string t = (rest ?? "").Trim();
            if (t.Length == 0) return "";

            string[] parts = t.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";

            string mode = parts[0].ToLowerInvariant();
            if (mode != "args" && mode != "waylithclr" && mode != "layer") return null;

            if (parts.Length > 1) extra = string.Join(" ", parts, 1, parts.Length - 1);
            return mode;
        }

        /// <summary>不认识的子参数 ⇒ 用法页(Kind 仍是 env,Data 带 usage/unknown/modes),不抛。</summary>
        internal static SlashResult Usage(string rest)
        {
            SlashResult r = SlashResults.Usage(Kind,
                "用法: /env [args|waylithclr|layer]",
                "  args       —— 启动参数全量 + 引擎/游戏/mod 旗标标注 + WAYLITH_* 环境变量",
                "  waylithclr —— 反射 WaylithClr 的静态字段/属性(读不到写 reason,不抛)",
                "  layer      —— WaylithClr 兼容层开关(AppContext) + 垫片程序集(AppDomain) + Vulkan 层注册(注册表)",
                "  无参       —— 以上三段全列(Data 另外始终带 runtime 基线)",
                "不认识的子参数按本用法页返回,不静默猜。");
            r.Data["unknown"] = rest ?? "";
            r.Data["modes"] = new List<string> { "args", "waylithclr", "layer" };
            return r;
        }

        // ==================== args + env ====================

        internal static void IncludeArgs(Dictionary<string, object> data, List<string> lines)
        {
            string[] argv = SafeCommandLineArgs();
            var raw = new List<string>(argv);

            var flags = new List<Dictionary<string, object>>();
            var byScope = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.Ordinal);
            for (int i = 0; i < argv.Length; i++)
            {
                Dictionary<string, object> f = DescribeArg(i, argv[i]);
                flags.Add(f);
                string scope = (string)f["scope"];
                List<Dictionary<string, object>> bucket;
                if (!byScope.TryGetValue(scope, out bucket))
                {
                    bucket = new List<Dictionary<string, object>>();
                    byScope[scope] = bucket;
                }
                bucket.Add(f);
            }

            data["args"] = raw;
            data["argCount"] = raw.Count;
            data["flags"] = flags;

            lines.Add("启动参数(共 " + raw.Count + " 项,含 argv[0]):");
            RenderScope(lines, byScope, "exe", "可执行文件");
            RenderScope(lines, byScope, "engine", "引擎开关(Waylith)");
            RenderScope(lines, byScope, "game", "游戏开关(Verse)");
            RenderScope(lines, byScope, "mod", "mod 开关");
            RenderScope(lines, byScope, "positional", "裸参数(无 '-' 前缀)");
            RenderScope(lines, byScope, "unknown", "未识别开关(**不在** STOCK-TOOLS 的引擎 20 / 游戏 10 / mod 表里)");

            // ---- WAYLITH_* 环境变量:§1「环境变量也是启动参数」 ----
            var envSorted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var envNames = new List<string>();
            IDictionary all = null;
            try { all = Environment.GetEnvironmentVariables(); }
            catch { all = null; }
            if (all != null)
            {
                foreach (DictionaryEntry e in all)
                {
                    string name = e.Key as string;
                    if (!IsWaylithEnvName(name)) continue;
                    string val = e.Value == null ? "" : Convert.ToString(e.Value, CultureInfo.InvariantCulture);
                    envSorted[name ?? ""] = val ?? "";
                    envNames.Add(name);
                }
            }
            envNames.Sort(StringComparer.Ordinal);

            data["env"] = envSorted;

            var known = new List<Dictionary<string, object>>();
            var knownNames = new List<string>();
            foreach (string[] row in KnownWaylithEnv)
            {
                bool set = envSorted.ContainsKey(row[0]);
                var d = new Dictionary<string, object>();
                d["name"] = row[0];
                d["note"] = row[1];
                d["set"] = set;
                d["value"] = set ? (object)envSorted[row[0]] : null;
                known.Add(d);
                knownNames.Add(row[0]);
            }
            data["envKnown"] = known;
            data["envCount"] = envSorted.Count;

            lines.Add("环境变量 WAYLITH_*(已设置 " + envSorted.Count + " 条 / 文档化 " + KnownWaylithEnv.Length + " 条):");
            foreach (string n in envNames)
            {
                string note = NoteForWaylithEnv(n);
                lines.Add("  " + n + " = " + envSorted[n] + " —— " + note);
            }
            foreach (Dictionary<string, object> d in known)
            {
                if ((bool)d["set"]) continue;
                lines.Add("  (未设置) " + d["name"] + " —— " + d["note"]);
            }
        }

        /// <summary>单条启动参数的标注。scope: exe | engine | game | mod | positional | unknown。</summary>
        internal static Dictionary<string, object> DescribeArg(int index, string token)
        {
            string raw = token ?? "";
            var f = new Dictionary<string, object>();
            f["index"] = index;
            f["token"] = raw;

            if (index == 0)
            {
                f["scope"] = "exe";
                f["name"] = SafeStr(delegate { return Path.GetFileName(raw); });
                f["value"] = null;
                f["known"] = true;
                f["note"] = "进程可执行文件(argv[0])";
                return f;
            }

            string body = raw.Trim();
            bool hasPrefix = body.Length > 0 && (body[0] == '-' || body[0] == '+');
            bool plusPrefix = body.Length > 0 && body[0] == '+';
            string core = body.Trim('-', '+');            // 引擎侧 HasSwitch 就是 Trim('-','+') 后全等比较
            string name = core, value = null;
            int eq = core.IndexOf('=');
            if (eq >= 0)
            {
                name = core.Substring(0, eq);
                value = core.Substring(eq + 1);
            }

            ArgSpec spec = null;
            if (name.Length > 0) ArgTable.TryGetValue(name, out spec);

            f["name"] = name;
            f["value"] = value;
            if (spec != null)
            {
                string note = spec.Note;
                if (value == null && spec.ValueHint != null)
                    note += "（本开关需要取值: " + spec.ValueHint + "）";
                if (plusPrefix && spec.Scope == "game")
                    note += "（'+' 前缀:游戏侧 GenCommandLine 只认裸 key 或 -key,本条不会被 Verse 读到）";
                f["scope"] = spec.Scope;
                f["known"] = true;
                f["note"] = note;
            }
            else
            {
                f["scope"] = hasPrefix ? "unknown" : "positional";
                f["known"] = false;
                f["note"] = hasPrefix
                    ? "不在已知开关表里(引擎 20 / 游戏 10 / mod 表都没有);可能是新版本开关或拼写错误"
                    : "无 '-' 前缀的裸参数(mod 开关可裸写;其余多半是位置参数)";
            }
            return f;
        }

        /// <summary>WAYLITH 开头的环境变量名才算(§3 那 11 条)。</summary>
        internal static bool IsWaylithEnvName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.StartsWith("WAYLITH", StringComparison.OrdinalIgnoreCase);
        }

        internal static string[] SafeCommandLineArgs()
        {
            try
            {
                string[] a = Environment.GetCommandLineArgs();
                if (a != null) return a;
            }
            catch { }
            return new string[0];
        }

        /// <summary>
        /// 引擎真身认的那个旗标串,**唯一定义处**:前导 '-' + 精确大小写。
        /// 调用点(AddVulkanValidationRequest)与回归门共用它 —— 改它就必然红,不会再出现"调用点没网"的形态。
        /// </summary>
        internal const string VulkanValidationSwitch = "-EnableVulkanValidation";

        /// <summary><c>String.Contains(String)</c> 语义 = **序数、区分大小写**(纯函数,便于单测喂串)。</summary>
        internal static bool CommandLineContainsIn(string commandLine, string needle)
        {
            if (string.IsNullOrEmpty(commandLine) || string.IsNullOrEmpty(needle)) return false;
            return commandLine.IndexOf(needle, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 「验证层是否已请求」的**唯一判定点**,也是端到端可测的接缝:命令行文本由调用方传入
        /// (IncludeLayer 传 Environment.CommandLine;套件可以喂任意串)。
        ///
        /// 与引擎**真身**同口径:引擎用的是
        /// <c>Environment.CommandLine.Contains("-EnableVulkanValidation")</c>(GraphicsDevice.cs:174/577)。
        /// 三个轴都得照抄:大小写不敏感会把 -enablevulkanvalidation 谎报成「验证层已请求」(引擎并不会加);
        /// 只看 token 名会把 +EnableVulkanValidation 与裸旗标同样谎报;自己加宽松规则同理。
        /// </summary>
        internal static void AddVulkanValidationRequest(List<Dictionary<string, object>> requested, string commandLine)
        {
            if (requested == null) return;
            if (!CommandLineContainsIn(commandLine, VulkanValidationSwitch)) return;
            var d = new Dictionary<string, object>();
            d["layer"] = "VK_LAYER_KHRONOS_validation";
            d["from"] = "Environment.CommandLine.Contains(\"" + VulkanValidationSwitch + "\") —— 序数、区分大小写、要求前导 '-'";
            requested.Add(d);
        }

        private static void RenderScope(List<string> lines, Dictionary<string, List<Dictionary<string, object>>> byScope, string scope, string title)
        {
            List<Dictionary<string, object>> bucket;
            if (!byScope.TryGetValue(scope, out bucket) || bucket.Count == 0) return;
            lines.Add("  " + title + "(共 " + bucket.Count + " 项):");
            foreach (Dictionary<string, object> f in bucket)
                lines.Add("    [" + f["index"] + "] " + f["token"] + " —— " + f["note"]);
        }

        private static string NoteForWaylithEnv(string name)
        {
            foreach (string[] row in KnownWaylithEnv)
                if (string.Equals(row[0], name, StringComparison.OrdinalIgnoreCase)) return row[1];
            return "未在 1.7 文档表的 WAYLITH_* 变量(可能是新版本或别处的开关)";
        }

        // ==================== runtime ====================

        internal static void IncludeRuntime(Dictionary<string, object> data)
        {
            var rt = new Dictionary<string, object>();
            rt["version"] = SafeStr(delegate { return Environment.Version.ToString(); });
            rt["rid"] = SafeStr(delegate { return RuntimeInformation.RuntimeIdentifier; });
            rt["framework"] = SafeStr(delegate { return RuntimeInformation.FrameworkDescription; });
            rt["processArch"] = SafeStr(delegate { return RuntimeInformation.ProcessArchitecture.ToString(); });
            rt["osDescription"] = SafeStr(delegate { return RuntimeInformation.OSDescription; });
            rt["osArchitecture"] = SafeStr(delegate { return RuntimeInformation.OSArchitecture.ToString(); });
            rt["osVersion"] = SafeStr(delegate { return Environment.OSVersion.ToString(); });
            rt["processorCount"] = Environment.ProcessorCount;
            rt["is64BitProcess"] = Environment.Is64BitProcess;
            rt["runtimeDir"] = SafeStr(delegate { return RuntimeEnvironment.GetRuntimeDirectory(); });
            rt["baseDirectory"] = SafeStr(delegate { return AppContext.BaseDirectory; });
            rt["currentDirectory"] = SafeStr(delegate { return Environment.CurrentDirectory; });
            rt["commandLine"] = SafeStr(delegate { return Environment.CommandLine; });
            rt["runtimeConfigPath"] = FindRuntimeConfigPath();
            data["runtime"] = rt;
        }

        private static void RenderRuntime(Dictionary<string, object> rt, List<string> lines)
        {
            lines.Add("运行时:");
            lines.Add("  framework = " + ShowScalar(rt["framework"]) + "   version = " + ShowScalar(rt["version"]));
            lines.Add("  rid = " + ShowScalar(rt["rid"]) + "   processArch = " + ShowScalar(rt["processArch"]));
            lines.Add("  os = " + ShowScalar(rt["osDescription"]) + " (" + ShowScalar(rt["osArchitecture"]) + ")");
            lines.Add("  processorCount = " + rt["processorCount"] + "   is64BitProcess = " + rt["is64BitProcess"]);
            lines.Add("  runtimeDir = " + ShowScalar(rt["runtimeDir"]));
            lines.Add("  runtimeconfig = " + ShowScalar(rt["runtimeConfigPath"]));
        }

        /// <summary>找 RimWorld.runtimeconfig.json(它是 22 条启动期开关的权威文本;找不到返回 null,不抛)。</summary>
        internal static string FindRuntimeConfigPath()
        {
            string[] dirs = new string[]
            {
                SafeStr(delegate { return AppContext.BaseDirectory; }),
                SafeStr(delegate { return Environment.CurrentDirectory; }),
                SafeStr(delegate { return RuntimeEnvironment.GetRuntimeDirectory(); })
            };
            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string p = SafeStr(delegate { return Path.Combine(dir, "RimWorld.runtimeconfig.json"); });
                if (p != null && SafeStr(delegate { return File.Exists(p) ? "1" : null; }) != null) return p;
            }
            // 兜底:BaseDirectory 下任何 *.runtimeconfig.json(自托管/改名部署)。
            string baseDir = SafeStr(delegate { return AppContext.BaseDirectory; });
            if (!string.IsNullOrEmpty(baseDir))
            {
                string[] files = null;
                try { files = Directory.GetFiles(baseDir, "*.runtimeconfig.json"); }
                catch { files = null; }
                if (files != null && files.Length > 0)
                {
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);   // 目录枚举顺序不是契约,先排序再取
                    return files[0];
                }
            }
            return null;
        }

        // ==================== waylithclr ====================

        internal static void IncludeWaylithClr(Dictionary<string, object> data, List<string> lines)
        {
            var wc = new Dictionary<string, object>();
            List<string> typeErrors;
            List<Type> types = FindWaylithClrTypes(out typeErrors);
            wc["found"] = types.Count > 0;
            wc["typeCount"] = types.Count;
            wc["typeErrors"] = typeErrors;

            var typeList = new List<Dictionary<string, object>>();
            foreach (Type t in types)
            {
                var d = new Dictionary<string, object>();
                string nameErr = null;
                try { d["fullName"] = t.FullName; }
                catch (Exception ex) { d["fullName"] = null; nameErr = "FullName 抛出: " + Describe(ex); }
                try { d["assembly"] = t.Assembly.GetName().Name; }
                catch (Exception ex)
                {
                    d["assembly"] = null;
                    nameErr = (nameErr == null ? "" : nameErr + " / ") + "Assembly.GetName 抛出: " + Describe(ex);
                }
                d["nameError"] = nameErr;

                var fields = new List<Dictionary<string, object>>();
                string fieldError = null;
                try
                {
                    FieldInfo[] fs = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (fs != null) foreach (FieldInfo f in fs) fields.Add(DescribeField(f));
                }
                catch (Exception ex) { fieldError = Describe(ex); }
                d["fields"] = fields;
                d["fieldError"] = fieldError;

                var props = new List<Dictionary<string, object>>();
                string propError = null;
                try
                {
                    PropertyInfo[] ps = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (ps != null) foreach (PropertyInfo p in ps) props.Add(DescribeProperty(p));
                }
                catch (Exception ex) { propError = Describe(ex); }
                d["properties"] = props;
                d["propertyError"] = propError;

                typeList.Add(d);
            }
            wc["types"] = typeList;

            // 便利键:IsSupported 是 ENGINE-BUILTINS 里唯一标了 FeatureSwitchDefinition 的未知量。
            object isSupported = null;
            string isSupportedFrom = null;
            string isSupportedError = null;
            foreach (Dictionary<string, object> d in typeList)
            {
                isSupported = PickIsSupported(d, "fields", isSupported, ref isSupportedFrom, ref isSupportedError);
                isSupported = PickIsSupported(d, "properties", isSupported, ref isSupportedFrom, ref isSupportedError);
            }
            wc["isSupported"] = isSupported;
            wc["isSupportedFrom"] = isSupportedFrom;
            wc["isSupportedError"] = isSupportedError;
            wc["reason"] = types.Count > 0 ? null
                : "本进程的 AppDomain 里没有 namespace/类型 WaylithClr.*(WaylithClr 的 ReJIT 面定义在 System.Private.CoreLib.dll,只有 Waylith 运行时才带)。";

            data["waylithClr"] = wc;

            if (types.Count == 0)
            {
                lines.Add("WaylithClr:不可得 —— " + wc["reason"]);
                return;
            }

            lines.Add("WaylithClr(找到 " + types.Count + " 个类型):");
            if (typeErrors.Count > 0)
                lines.Add("  硬失败(" + typeErrors.Count + " 条,与「本进程本来就没有 WaylithClr」是两件事): " + string.Join(" / ", typeErrors.ToArray()));
            foreach (Dictionary<string, object> d in typeList)
            {
                lines.Add("  " + d["fullName"] + "  [" + d["assembly"] + "]");
                if (d["nameError"] != null) lines.Add("    (类型元数据读取失败: " + d["nameError"] + ")");
                var fields = d["fields"] as List<Dictionary<string, object>>;
                if (fields != null && fields.Count > 0)
                {
                    lines.Add("    静态字段(" + fields.Count + "):");
                    foreach (Dictionary<string, object> fd in fields)
                        lines.Add("      " + fd["name"] + " : " + fd["type"] + " = " + MemberValueText(fd));
                }
                if (d["fieldError"] != null) lines.Add("    静态字段读取失败: " + d["fieldError"]);
                var props = d["properties"] as List<Dictionary<string, object>>;
                if (props != null && props.Count > 0)
                {
                    lines.Add("    静态属性(" + props.Count + "):");
                    foreach (Dictionary<string, object> pd in props)
                        lines.Add("      " + pd["name"] + " : " + pd["type"] + " = " + MemberValueText(pd));
                }
                if (d["propertyError"] != null) lines.Add("    静态属性读取失败: " + d["propertyError"]);
            }
            lines.Add("  IsSupported(便利键) = " + ShowScalar(isSupported)
                + (isSupportedFrom == null ? " —— 该类型没有 IsSupported 成员" : "  <- " + isSupportedFrom));
        }

        private static object PickIsSupported(Dictionary<string, object> typeDict, string key, object current, ref string from, ref string error)
        {
            if (current != null || from != null) return current;
            var list = typeDict[key] as List<Dictionary<string, object>>;
            if (list == null) return current;
            foreach (Dictionary<string, object> m in list)
            {
                if (!string.Equals(m["name"] as string, "IsSupported", StringComparison.Ordinal)) continue;
                from = typeDict["fullName"] + " (static " + (key == "fields" ? "field" : "property") + ")";
                if (m["readable"] is bool && !(bool)m["readable"]) error = m["error"] as string;
                return m["value"];
            }
            return current;
        }

        /// <summary>
        /// 找 WaylithClr 类型:先走 Type.GetType(定义在 System.Private.CoreLib,不必全扫);
        /// 不中再扫 AppDomain 已加载程序集(兼容将来引擎侧也放一份的情形)。全程不抛。
        /// </summary>
        internal static List<Type> FindWaylithClrTypes()
        {
            List<string> ignored;
            return FindWaylithClrTypes(out ignored);
        }

        /// <summary>
        /// 找 WaylithClr 类型:先走 Type.GetType(定义在 System.Private.CoreLib,不必全扫);
        /// 不中再扫 AppDomain 已加载程序集(兼容将来引擎侧也放一份的情形)。全程不抛。
        /// <paramref name="errors"/> 专收「硬失败」(反射调用抛了 / GetTypes 整块或部分失败):
        /// 它与「本进程本来就没有 WaylithClr」是两件事,调用点要分开呈现。
        /// 只读已加载程序集,绝不 Assembly.Load —— 见 layer 里 P/Invoke 那段注释。
        /// </summary>
        internal static List<Type> FindWaylithClrTypes(out List<string> errors)
        {
            errors = new List<string>();
            var found = new List<Type>();
            var seenAsm = new List<Assembly>();

            string[] probes = new string[] { "WaylithClr.MethodBodyUpdater", "WaylithClr.SelfProfiler" };
            foreach (string p in probes)
            {
                Type t = null;
                try { t = Type.GetType(p); }
                catch (Exception ex) { errors.Add("Type.GetType(" + p + ") 抛出: " + Describe(ex)); continue; }
                if (t == null) continue;
                Assembly a = null;
                try { a = t.Assembly; }
                catch (Exception ex) { errors.Add(p + ".Assembly 抛出: " + Describe(ex)); continue; }
                if (a != null && !seenAsm.Contains(a)) seenAsm.Add(a);
            }

            Assembly[] all = null;
            try { all = AppDomain.CurrentDomain.GetAssemblies(); }
            catch (Exception ex) { errors.Add("AppDomain.GetAssemblies() 抛出: " + Describe(ex)); all = null; }
            if (all != null)
            {
                foreach (Assembly a in all)
                {
                    if (a == null) continue;
                    if (seenAsm.Contains(a)) continue;
                    if (!MayContainWaylithClr(a, errors)) continue;
                    seenAsm.Add(a);
                }
            }

            foreach (Assembly a in seenAsm)
            {
                string asmName = SafeStr(delegate { return a.GetName().Name; });
                Type[] ts = null;
                try { ts = a.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    ts = ex.Types;
                    int missing = 0;
                    if (ex.Types != null) foreach (Type x in ex.Types) if (x == null) missing++;
                    errors.Add("程序集 " + asmName + ".GetTypes() 部分类型加载失败(ReflectionTypeLoadException,缺失 " + missing + " 个;已用 ex.Types 的非空部分)");
                }
                catch (Exception ex)
                {
                    errors.Add("程序集 " + asmName + ".GetTypes() 整块失败: " + Describe(ex));
                    ts = null;
                }
                if (ts == null) continue;
                foreach (Type t in ts)
                {
                    if (t == null) continue;
                    string ns = null;
                    try { ns = t.Namespace; }
                    catch (Exception ex) { errors.Add("某个类型的 Namespace 读不到(已跳过): " + Describe(ex)); continue; }
                    if (ns == "WaylithClr") { if (!found.Contains(t)) found.Add(t); }
                }
            }

            found.Sort(delegate (Type a, Type b) { return string.CompareOrdinal(a.FullName, b.FullName); });
            return found;
        }

        /// <summary>只扫「可能带 WaylithClr」的程序集:名字含 Waylith,或核心库/本程序集。名字读不到 → 记一条硬失败并跳过。</summary>
        private static bool MayContainWaylithClr(Assembly a, List<string> errors)
        {
            string n;
            try { n = a.GetName().Name; }
            catch (Exception ex) { errors.Add("程序集名不可得(已跳过该程序集): " + Describe(ex)); return false; }
            if (string.IsNullOrEmpty(n)) return false;
            if (n.IndexOf("Waylith", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // 「RoslynProbe」= 游戏内本 mod 程序集;「SlashHarness」= 仓库的 Slash 测试/编译面程序集
            // (tests/SlashHarness/;本工程原名 SlashCheck,搬入 tests/ 时改名 SlashHarness)—— 这两个是 WaylithClr 之外唯一可能被扫的自有程序集。
            return string.Equals(n, "System.Private.CoreLib", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n, "System.Runtime", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n, "RoslynProbe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n, "SlashHarness", StringComparison.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, object> DescribeField(FieldInfo f)
        {
            var d = new Dictionary<string, object>();
            d["name"] = null;
            d["type"] = null;
            d["const"] = false;
            d["value"] = null;
            d["readable"] = false;
            d["reason"] = null;
            d["error"] = null;

            bool isConst = false;
            bool scalar = false;
            try
            {
                d["name"] = f.Name;
                Type ft = f.FieldType;
                d["type"] = ft == null ? "?" : (ft.FullName ?? ft.Name);
                isConst = f.IsLiteral;
                scalar = IsScalar(ft);
                d["const"] = isConst;
            }
            catch (Exception ex)
            {
                d["error"] = Describe(ex);
                return d;
            }

            if (isConst)
            {
                try { d["value"] = ScalarValue(f.GetRawConstantValue()); d["readable"] = true; }
                catch (Exception ex) { d["error"] = Describe(ex); }
            }
            else if (scalar)
            {
                try { d["value"] = ScalarValue(f.GetValue(null)); d["readable"] = true; }
                catch (Exception ex) { d["error"] = Describe(ex); }
            }
            else
            {
                d["reason"] = "非标量静态字段,不读取(读它会触发该类型的静态初始化;本指令只做只读快照)";
            }
            return d;
        }

        internal static Dictionary<string, object> DescribeProperty(PropertyInfo p)
        {
            var d = new Dictionary<string, object>();
            d["name"] = null;
            d["type"] = null;
            d["value"] = null;
            d["readable"] = false;
            d["reason"] = null;
            d["error"] = null;
            try
            {
                d["name"] = p.Name;
                Type pt = p.PropertyType;
                d["type"] = pt == null ? "?" : (pt.FullName ?? pt.Name);
                if (p.GetIndexParameters().Length > 0) { d["reason"] = "带索引的参数化属性,不读取"; return d; }
                if (p.GetGetMethod(true) == null) { d["reason"] = "没有 getter"; return d; }
                if (!IsScalar(pt)) { d["reason"] = "非标量属性,不读取(避免触发 getter 副作用)"; return d; }
                d["value"] = ScalarValue(p.GetValue(null, null));
                d["readable"] = true;
            }
            catch (Exception ex) { d["error"] = Describe(ex); }
            return d;
        }

        internal static bool IsScalar(Type t)
        {
            if (t == null) return false;
            if (t.IsEnum) return true;
            return t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
                || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)
                || t == typeof(char) || t == typeof(float) || t == typeof(double) || t == typeof(decimal)
                || t == typeof(string);
        }

        /// <summary>只做标量快照:枚举取名、字符串保真、其余类型用 "&lt;类型名&gt;" 占位。</summary>
        internal static object ScalarValue(object v)
        {
            if (v == null) return null;
            Type t = v.GetType();
            if (t.IsEnum) { try { return v.ToString(); } catch { return "<enum>"; } }
            if (t == typeof(string) || t == typeof(bool)) return v;
            if (t == typeof(char)) return v.ToString();
            if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int)
                || t == typeof(uint) || t == typeof(long) || t == typeof(ulong) || t == typeof(float)
                || t == typeof(double) || t == typeof(decimal))
                return v;
            return "<" + t.Name + ">";
        }

        internal static string ShowScalar(object v)
        {
            if (v == null) return "<null>";
            if (v is bool) return ((bool)v) ? "true" : "false";
            if (v is string)
            {
                string s = (string)v;
                if (s.Length > 160) s = s.Substring(0, 160) + "…";
                return "\"" + s + "\"";
            }
            return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "<null>";
        }

        private static string MemberValueText(Dictionary<string, object> m)
        {
            if (m["readable"] is bool && (bool)m["readable"]) return ShowScalar(m["value"]);
            if (m["error"] != null) return "<读取失败: " + m["error"] + ">";
            if (m["reason"] != null) return "<未读取: " + m["reason"] + ">";
            return "<null>";
        }

        // ==================== layer ====================

        internal static void IncludeLayer(Dictionary<string, object> data, List<string> lines)
        {
            var layer = new Dictionary<string, object>();
            var unavailable = new List<string>();

            // ---- 1) WaylithClr 兼容层开关(AppContext ← runtimeconfig.json 的 configProperties) ----
            string switchSource;
            string switchReason;
            List<string> keys = DiscoverSwitchKeys(out switchSource, out switchReason);

            var switches = new Dictionary<string, object>(StringComparer.Ordinal);
            var runtimeSwitches = new Dictionary<string, object>(StringComparer.Ordinal);
            var missing = new List<string>();
            var switchErrors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string k in keys)
            {
                object v;
                string swErr;
                if (!ReadSwitch(k, out v, out swErr))
                {
                    // 「硬失败(读这个键本身抛了)」与「本运行时就没注册这个键」分开记,别都混进"缺失"。
                    missing.Add(k);
                    if (!string.IsNullOrEmpty(swErr)) switchErrors[k] = swErr;
                    continue;
                }
                if (k.StartsWith("WaylithClr.", StringComparison.Ordinal)) switches[k] = v;
                else runtimeSwitches[k] = v;
            }
            layer["switchSource"] = switchSource;
            layer["switchReason"] = switchReason;
            layer["switches"] = switches;
            layer["switchesMissing"] = missing;
            layer["switchErrors"] = switchErrors;
            layer["runtimeSwitches"] = runtimeSwitches;

            // ---- 2) 垫片层:AssemblyNameRedirects + AppDomain 里实际加载了谁 ----
            object redirectsRawObj;
            string redirectsRaw = null;
            if (switches.TryGetValue("WaylithClr.AssemblyNameRedirects", out redirectsRawObj))
                redirectsRaw = Convert.ToString(redirectsRawObj, CultureInfo.InvariantCulture);

            HashSet<string> loaded = LoadedAssemblyNames(unavailable);
            var shimNames = new string[]
            {
                "System.Private.CoreLib", "UnityStubs", "UnityEngine", "UnityEngine.CoreModule",
                "Assembly-CSharp", "Assembly-CSharp-firstpass", "Unity.Newtonsoft.Json",
                "RimWorld", "Waylith", "0Harmony", "RoslynProbe", "SlashHarness"
            };
            var shim = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string n in shimNames) shim[n] = loaded.Contains(n);

            var redirects = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> pair in ParseRedirects(redirectsRaw))
            {
                string pattern = (string)pair["pattern"];
                string target = (string)pair["target"];
                var srcLoaded = new List<string>();
                foreach (string n in loaded) if (PatternMatches(pattern, n)) srcLoaded.Add(n);
                srcLoaded.Sort(StringComparer.Ordinal);
                bool targetLoaded = target.Length > 0 && loaded.Contains(target);
                pair["sourceLoaded"] = srcLoaded;
                pair["targetLoaded"] = targetLoaded;
                pair["inferredActive"] = srcLoaded.Count == 0 && (target.Length == 0 || targetLoaded);
                redirects.Add(pair);
            }

            var shunt = new Dictionary<string, object>();
            shunt["redirectsRaw"] = redirectsRaw;
            shunt["redirects"] = redirects;
            shunt["assemblies"] = shim;
            bool shuntOk = redirectsRaw != null || (shim["UnityStubs"] is bool && (bool)shim["UnityStubs"]);
            layer["shunt"] = shunt;

            // ---- 3) Vulkan 层注册(注册表;进程内只能反射读) ----
            var vk = new Dictionary<string, object>();
            var vkEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IDictionary allEnv = null;
            try { allEnv = Environment.GetEnvironmentVariables(); }
            catch { allEnv = null; }
            if (allEnv != null)
            {
                foreach (DictionaryEntry e in allEnv)
                {
                    string name = e.Key as string;
                    if (!IsVulkanEnvName(name)) continue;
                    string val = e.Value == null ? "" : Convert.ToString(e.Value, CultureInfo.InvariantCulture);
                    vkEnv[name ?? ""] = val ?? "";
                }
            }
            vk["env"] = vkEnv;

            var requested = new List<Dictionary<string, object>>();
            AddVulkanList(requested, vkEnv, "VK_INSTANCE_LAYERS", new char[] { ';', ':' });
            AddVulkanList(requested, vkEnv, "VK_LOADER_LAYERS_ENABLE", new char[] { ',' });
            AddVulkanValidationRequest(requested, SafeStr(delegate { return Environment.CommandLine; }));
            vk["requested"] = requested;

            var disabled = new List<string>();
            string disRaw;
            if (vkEnv.TryGetValue("VK_LOADER_LAYERS_DISABLE", out disRaw) && !string.IsNullOrEmpty(disRaw))
                foreach (string s in disRaw.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    if (s.Trim().Length > 0) disabled.Add(s.Trim());
            vk["disabled"] = disabled;

            var registered = new List<Dictionary<string, object>>();
            var regErrors = new List<string>();
            ReadVulkanImplicitLayers(registered, regErrors);
            vk["registered"] = registered;
            vk["registryReason"] = regErrors.Count == 0 ? null : string.Join("; ", regErrors.ToArray());
            layer["vulkan"] = vk;

            // ---- ok / reason ----
            // ok 的口径(写死,免得含混):「WaylithClr 兼容层已在本进程注册」= AppContext 读到了 WaylithClr.* 键。
            // 垫片程序集只算旁证(shuntEvidence):进程里出现 UnityStubs 也可能只是谁反射加载了它,不足以判定运行时身份。
            bool switchOk = switches.Count > 0;
            layer["ok"] = switchOk;
            layer["shuntEvidence"] = shuntOk;
            layer["okMeaning"] = "AppContext 里读到了 WaylithClr.* 启动期开关(即本进程跑在 Waylith 运行时下)";
            layer["reason"] = switchOk ? null
                : "未检测到 WaylithClr 兼容层:AppContext 里没有 WaylithClr.* 键。"
                  + (string.IsNullOrEmpty(switchReason) ? "" : "（" + switchReason + "）")
                  + (shuntOk
                      ? "AppDomain 里虽然有 UnityStubs 之类的垫片程序集,但垫片表(WaylithClr.AssemblyNameRedirects)读不到,不足以判定运行时身份。"
                      : "AppDomain 里也没有 UnityStubs 等垫片程序集。")
                  + " 本进程多半不在 Waylith 运行时下(或该版本换了开关名)。";
            if (missing.Count > 0)
                unavailable.Add("AppContext 未读到 " + missing.Count + " 个文档化开关(键有改动/不在本运行时的注册面)");
            if (switchErrors.Count > 0)
                unavailable.Add("读开关时硬失败 " + switchErrors.Count + " 个(与「没注册」不是一回事): " + JoinKeyValues(switchErrors));
            if (vk["registryReason"] != null)
                unavailable.Add("Vulkan 隐式层注册表: " + vk["registryReason"]);
            layer["unavailable"] = unavailable;

            data["layer"] = layer;

            // ---- Lines ----
            lines.Add("层注册(WaylithClr 兼容层 / 垫片层 / Vulkan 层):");
            lines.Add("  WaylithClr 兼容层开关:读到 " + switches.Count + " 个" + (missing.Count > 0 ? ",未注册 " + missing.Count + " 个" : "")
                + " —— 来源: " + (switchSource ?? "<不可得>") + (switchReason == null ? "" : "（" + switchReason + "）"));
            var switchKeys = new List<string>(switches.Keys);
            switchKeys.Sort(StringComparer.Ordinal);
            foreach (string k in switchKeys)
            {
                string extra = NoteTextFor(k);
                lines.Add("    " + k + " = " + ShowScalar(switches[k]) + (string.IsNullOrEmpty(extra) ? "" : " —— " + extra));
            }
            if (missing.Count > 0)
            {
                missing.Sort(StringComparer.Ordinal);
                lines.Add("    (未注册) " + string.Join(" / ", missing.ToArray()));
            }
            if (switchErrors.Count > 0)
                lines.Add("    (硬失败,与「未注册」不是一回事) " + JoinKeyValues(switchErrors));
            var rtKeys = new List<string>(runtimeSwitches.Keys);
            rtKeys.Sort(StringComparer.Ordinal);
            if (rtKeys.Count > 0)
            {
                lines.Add("  运行时开关(System.*,非 WaylithClr 而是同期的 configProperties):");
                foreach (string k in rtKeys)
                {
                    string note = NoteTextFor(k);
                    lines.Add("    " + k + " = " + ShowScalar(runtimeSwitches[k]) + (string.IsNullOrEmpty(note) ? "" : " —— " + note));
                }
            }
            lines.Add("  垫片层(AssemblyNameRedirects + AppDomain 实测):");
            if (string.IsNullOrEmpty(redirectsRaw))
                lines.Add("    不可得:AppContext 没有 WaylithClr.AssemblyNameRedirects(本进程不在 Waylith 运行时下,或该版本去掉了垫片表)");
            else
            {
                lines.Add("    原始表 = " + redirectsRaw);
                foreach (Dictionary<string, object> rd in redirects)
                {
                    var srcLoaded = rd["sourceLoaded"] as List<string>;
                    lines.Add("    " + rd["pattern"] + " -> " + (string.IsNullOrEmpty((string)rd["target"]) ? "(空目标)" : (string)rd["target"])
                        + " : 源侧已加载 " + (srcLoaded == null ? 0 : srcLoaded.Count) + " 个"
                        + (srcLoaded != null && srcLoaded.Count > 0 ? "[" + string.Join(", ", srcLoaded.ToArray()) + "]" : "")
                        + ", 目标侧已加载 " + (((bool)rd["targetLoaded"]) ? "是" : "否")
                        + " ⇒ 推断" + (((bool)rd["inferredActive"]) ? "生效" : "未生效"));
                }
            }
            var shimYes = new List<string>();
            foreach (KeyValuePair<string, object> kv in shim) if (kv.Value is bool && (bool)kv.Value) shimYes.Add(kv.Key);
            lines.Add("    已加载的程序集(GET 关注名单): " + (shimYes.Count == 0 ? "<无>" : string.Join(", ", shimYes.ToArray())));
            lines.Add("  Vulkan 层注册(HKCU/HKLM\\SOFTWARE\\Khronos\\Vulkan\\ImplicitLayers;值 0=停用,非 0=启用):");
            if (registered.Count == 0)
                lines.Add("    注册项: " + (vk["registryReason"] == null ? "0 条(两个 hive 都没注册任何隐式层)" : "不可得 —— " + vk["registryReason"]));
            else
                foreach (Dictionary<string, object> rg in registered)
                    lines.Add("    [" + rg["hive"] + "] " + rg["name"] + " = " + ShowScalar(rg["value"])
                        + " (" + EnabledText(rg["enabled"]) + ")"
                        + (rg["error"] == null ? "" : " —— 读取异常: " + rg["error"]));
            if (requested.Count == 0)
                lines.Add("    请求面(环境变量/命令行): 无(没有 VK_INSTANCE_LAYERS / VK_LOADER_LAYERS_ENABLE,CommandLine 里也没有 \"-EnableVulkanValidation\" 序数子串)");
            else
                foreach (Dictionary<string, object> rq in requested)
                    lines.Add("    请求面: " + rq["layer"] + "  <- " + rq["from"]);
            if (disabled.Count > 0)
                lines.Add("    显式排除(VK_LOADER_LAYERS_DISABLE): " + string.Join(", ", disabled.ToArray()));
            if (!switchOk) lines.Add("  ⚠ " + layer["reason"]);
        }

        /// <summary>
        /// 收集启动期开关的键:1.7 文档清单打底(**不会因版本改名而整段丢**),再用 runtimeconfig.json 里扫到的
        /// <c>WaylithClr.*</c> / <c>System.*</c> 键去并(与主机读同一份文本,新版本新增的开关也能自己长出来)。
        /// 值一律走 AppContext(键不存在 ⇒ 记进 switchesMissing)。
        /// </summary>
        internal static List<string> DiscoverSwitchKeys(out string source, out string reason)
        {
            reason = null;
            var keys = new List<string>();
            foreach (string k in DocumentedSwitchKeys) if (!keys.Contains(k)) keys.Add(k);

            bool fromFile = false;
            string path = FindRuntimeConfigPath();
            if (!string.IsNullOrEmpty(path))
            {
                string text = null;
                try { text = File.ReadAllText(path); }
                catch (Exception ex) { reason = "runtimeconfig.json 读失败(" + Describe(ex) + ")"; }
                if (text != null)
                {
                    // 只做键名扫描:configProperties 的键就是 "WaylithClr.X" / "System.Y" 这种带点的名字。
                    // 注意 SDK 给每个自托管 exe 也会塞 System.* 键,所以「扫到 System.*」≠「本进程是 Waylith 运行时」。
                    int i = 0;
                    while (i < text.Length)
                    {
                        int q = text.IndexOf('"', i);
                        if (q < 0) break;
                        int q2 = text.IndexOf('"', q + 1);
                        if (q2 < 0) break;
                        string key = text.Substring(q + 1, q2 - q - 1);
                        i = q2 + 1;
                        if (!key.StartsWith("WaylithClr.", StringComparison.Ordinal)
                            && !key.StartsWith("System.", StringComparison.Ordinal)) continue;
                        if (!keys.Contains(key)) keys.Add(key);
                    }
                    fromFile = true;   // 文件参与过枚举;它扫到几个键不影响文档清单打底
                }
            }
            else
            {
                reason = "找不到 RimWorld.runtimeconfig.json";
            }

            source = fromFile
                ? "runtimeconfig.json(" + path + ") + 1.7 文档清单(并集)"
                : "1.7 文档清单(硬编码兜底)";
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        /// <summary>读一个启动期开关:先 AppContext.TryGetSwitch(布尔),再 AppContext.GetData(字符串等)。不抛。</summary>
        internal static bool ReadSwitch(string key, out object value)
        {
            string ignored;
            return ReadSwitch(key, out value, out ignored);
        }

        /// <summary>
        /// 同上,但把「读这个键本身抛了」的硬失败单独报出来(<paramref name="error"/> 仅在真失败时非空)。
        /// 调用点据此把「硬失败」与「本运行时就没注册这个键」分开记 —— 两者都不抛。
        /// </summary>
        internal static bool ReadSwitch(string key, out object value, out string error)
        {
            value = null;
            error = null;
            if (string.IsNullOrEmpty(key)) { error = "键为空"; return false; }

            string e1 = null, e2 = null;
            try
            {
                bool b;
                if (AppContext.TryGetSwitch(key, out b)) { value = b; return true; }
            }
            catch (Exception ex) { e1 = "AppContext.TryGetSwitch: " + Describe(ex); }
            try
            {
                object raw = AppContext.GetData(key);
                if (raw != null) { value = ScalarValue(raw); return true; }
            }
            catch (Exception ex) { e2 = "AppContext.GetData: " + Describe(ex); }

            if (e1 != null && e2 != null) error = e1 + " / " + e2;
            else if (e1 != null) error = e1;
            else if (e2 != null) error = e2;
            return false;
        }

        /// <summary>解析 <c>AssemblyNameRedirects</c>(";" 分隔、"A=B" 两段;B 可空)。不抛。</summary>
        internal static List<Dictionary<string, object>> ParseRedirects(string raw)
        {
            var list = new List<Dictionary<string, object>>();
            if (string.IsNullOrEmpty(raw)) return list;
            string[] parts = raw.Split(';');
            foreach (string pRaw in parts)
            {
                string p = (pRaw ?? "").Trim();
                if (p.Length == 0) continue;
                int eq = p.IndexOf('=');
                var d = new Dictionary<string, object>();
                d["pattern"] = eq >= 0 ? p.Substring(0, eq).Trim() : p;
                d["target"] = eq >= 0 ? p.Substring(eq + 1).Trim() : "";
                list.Add(d);
            }
            return list;
        }

        /// <summary>垫片模式匹配:尾部 '*' 当前缀匹配,否则全等(忽略大小写)。</summary>
        internal static bool PatternMatches(string pattern, string assemblyName)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(assemblyName)) return false;
            if (pattern.EndsWith("*", StringComparison.Ordinal))
                return assemblyName.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase);
            return string.Equals(pattern, assemblyName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>已加载程序集的简单名集合(只读 AppDomain,不加载任何东西)。名字读不到的记一条原因。</summary>
        internal static HashSet<string> LoadedAssemblyNames(List<string> errors)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assembly[] all = null;
            try { all = AppDomain.CurrentDomain.GetAssemblies(); }
            catch (Exception ex) { if (errors != null) errors.Add("AppDomain.GetAssemblies() 抛出: " + Describe(ex)); return set; }
            if (all == null) return set;
            foreach (Assembly a in all)
            {
                if (a == null) continue;
                string n;
                try { n = a.GetName().Name; }
                catch (Exception ex) { if (errors != null) errors.Add("程序集名不可得(已跳过): " + Describe(ex)); continue; }
                if (!string.IsNullOrEmpty(n)) set.Add(n);
            }
            return set;
        }

        internal static bool IsVulkanEnvName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("RENDERDOC", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ENABLE_VULKAN", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("VULKAN_", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddVulkanList(List<Dictionary<string, object>> target, Dictionary<string, string> env, string varName, char[] seps)
        {
            string raw;
            if (!env.TryGetValue(varName, out raw) || string.IsNullOrEmpty(raw)) return;
            foreach (string s in raw.Split(seps, StringSplitOptions.RemoveEmptyEntries))
            {
                string v = s.Trim();
                if (v.Length == 0) continue;
                var d = new Dictionary<string, object>();
                d["layer"] = v;
                d["from"] = varName;
                target.Add(d);
            }
        }

        // Vulkan 隐式层注册项在 HKCU/HKLM 的 SOFTWARE\Khronos\Vulkan\ImplicitLayers。
        //
        // 为什么是 P/Invoke 而不是 Microsoft.Win32.Registry:该类在 net10.0 基础框架引用面之外
        // (本工程零 PackageReference ⇒ 编译期不能 using),而用 Assembly.Load 把它反射进来会**把新程序集拉进
        // AppDomain** —— 本项目的引用面/短名索引是「Reset 时刻已加载程序集」的投影(NOTE-reference-surface),
        // 一旦 /env 改变了已加载集合,后续求值的引用面就跟着变。诊断命令不得扰动被测系统,所以只 P/Invoke
        // advapi32:零程序集加载、只开只读句柄并立即关闭。非 Windows / advapi32 不可用时落进 reason,不抛。

        private const int KeyRead = 0x20019;                 // KEY_READ
        private const int ErrorSuccess = 0;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorMoreData = 234;               // ERROR_MORE_DATA
        private const int ErrorNoMoreItems = 259;            // ERROR_NO_MORE_ITEMS
        private const int RegSz = 1;
        private const int RegExpandSz = 2;
        private const int RegDword = 4;
        private static readonly IntPtr HkeyCurrentUser = new IntPtr(unchecked((int)0x80000001));
        private static readonly IntPtr HkeyLocalMachine = new IntPtr(unchecked((int)0x80000002));

        // 三个都显式 ExactSpelling + EntryPoint:Unicode 变体名写死在 EntryPoint 里,
        // 免得依赖「默认拼后缀」的隐式规则(RegCloseKey 本来就没有 A/W 变体,必须 ExactSpelling)。
        [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern int RegOpenKeyEx(IntPtr hKey, string lpSubKey, int ulOptions, int samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll", EntryPoint = "RegEnumValueW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern int RegEnumValue(IntPtr hKey, int dwIndex, StringBuilder lpValueName, ref int lpcchValueName,
            IntPtr lpReserved, out int lpType, byte[] lpData, ref int lpcbData);

        [DllImport("advapi32.dll", EntryPoint = "RegCloseKey", ExactSpelling = true, SetLastError = true)]
        private static extern int RegCloseKey(IntPtr hKey);

        /// <summary>两个 hive 依次读,读到的注册项并进 <paramref name="entries"/>,失败逐条进 <paramref name="errors"/>。</summary>
        internal static void ReadVulkanImplicitLayers(List<Dictionary<string, object>> entries, List<string> errors)
        {
            const string subKey = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";
            ReadRegistryValues("HKCU", HkeyCurrentUser, subKey, entries, errors);
            ReadRegistryValues("HKLM", HkeyLocalMachine, subKey, entries, errors);
        }

        /// <summary>
        /// 读一个注册表子键下的全部值(只读句柄 + 立即 RegCloseKey)。
        /// 键不存在(ERROR_FILE_NOT_FOUND/PATH_NOT_FOUND)= 该 hive 没注册隐式层,不算失败;
        /// 其余 Win32 错误与异常都进 <paramref name="errors"/>。
        /// </summary>
        internal static void ReadRegistryValues(string hive, IntPtr hiveKey, string subKeyPath,
            List<Dictionary<string, object>> entries, List<string> errors)
        {
            IntPtr key = IntPtr.Zero;
            try
            {
                int rc = RegOpenKeyEx(hiveKey, subKeyPath, 0, KeyRead, out key);
                if (rc != ErrorSuccess)
                {
                    if (rc != ErrorFileNotFound && rc != ErrorPathNotFound)
                        errors.Add(hive + ": RegOpenKeyEx(" + subKeyPath + ") 返回 " + rc + "(" + DescribeWin32(rc) + ")");
                    return;
                }

                int index = 0;
                while (true)
                {
                    int nameCap = 512;
                    var nameBuf = new StringBuilder(nameCap);
                    int nameLen = nameCap;
                    byte[] data = new byte[1024];
                    int dataLen = data.Length;
                    int type;
                    rc = RegEnumValue(key, index, nameBuf, ref nameLen, IntPtr.Zero, out type, data, ref dataLen);
                    if (rc == ErrorMoreData)
                    {
                        // 名字或数据超过缓冲:按系统回填的需求量扩一次,重试同一个 index。
                        if (nameLen + 1 > nameCap) { nameCap = nameLen + 1; nameBuf = new StringBuilder(nameCap); nameLen = nameCap; }
                        if (dataLen > data.Length) { data = new byte[dataLen]; }
                        rc = RegEnumValue(key, index, nameBuf, ref nameLen, IntPtr.Zero, out type, data, ref dataLen);
                    }
                    if (rc == ErrorNoMoreItems) break;
                    if (rc != ErrorSuccess)
                    {
                        errors.Add(hive + ": RegEnumValue 第 " + index + " 项返回 " + rc + "(" + DescribeWin32(rc) + ")");
                        break;
                    }

                    string valueName = nameLen > 0 ? nameBuf.ToString(0, nameLen) : "";
                    object value = RegistryValue(data, dataLen, type);
                    var d = new Dictionary<string, object>();
                    d["hive"] = hive;
                    d["path"] = hive + "\\" + subKeyPath;          // hive 前缀:HKCU\\… / HKLM\\…
                    d["name"] = valueName;
                    d["type"] = type;                                // 1=REG_SZ, 2=REG_EXPAND_SZ, 4=REG_DWORD …
                    d["value"] = value;
                    d["enabled"] = (type == RegDword && value is int) ? (object)(((int)value) != 0) : null;   // Vulkan 约定:0 = 停用
                    d["error"] = null;
                    entries.Add(d);
                    index++;
                }
            }
            catch (Exception ex) { errors.Add(hive + ": " + Describe(ex)); }
            finally
            {
                if (key != IntPtr.Zero)
                {
                    try { RegCloseKey(key); }
                    catch { }
                }
            }
        }

        /// <summary>只解析注册表里常见的三种:REG_DWORD → int、REG_SZ/REG_EXPAND_SZ → string;其余留 null(连同 type 一起给出)。</summary>
        internal static object RegistryValue(byte[] data, int length, int type)
        {
            try
            {
                if (type == RegDword && data != null && length >= 4) return BitConverter.ToInt32(data, 0);
                if ((type == RegSz || type == RegExpandSz) && data != null && length >= 2)
                    return System.Text.Encoding.Unicode.GetString(data, 0, length).TrimEnd('\0');
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Win32 错误码 → 文案。**自带小码表 + 固定兜底,刻意不 new System.ComponentModel.Win32Exception** ——
        /// 后者的构造函数在干净进程首次调用会引入 <c>Microsoft.Win32.Primitives</c>(实测),
        /// 与本指令「P/Invoke 路径不引入程序集」的口径冲突。只覆盖本指令真能撞到的码。
        /// </summary>
        internal static string DescribeWin32(int rc)
        {
            switch (rc)
            {
                case 2: return "ERROR_FILE_NOT_FOUND: 系统找不到指定的文件(此处 = 该 hive 没注册这个子键)";
                case 3: return "ERROR_PATH_NOT_FOUND: 系统找不到指定的路径";
                case 5: return "ERROR_ACCESS_DENIED: 拒绝访问";
                case 6: return "ERROR_INVALID_HANDLE: 句柄无效";
                case 87: return "ERROR_INVALID_PARAMETER: 参数错误";
                case 234: return "ERROR_MORE_DATA: 缓冲区不足(已扩容重试)";
                case 259: return "ERROR_NO_MORE_ITEMS: 没有更多数据(枚举正常结束)";
                case 1008: return "ERROR_NO_TOKEN: 试图引用不存在的令牌";
                case 1012: return "ERROR_CANTREAD: 无法读取配置注册表项";
                default: return "Win32 错误码 " + rc;
            }
        }

        /// <summary>开关说明文本(未收录的给兜底说明)。Lines 与 Data 共用同一份表。</summary>
        internal static string NoteTextFor(string key)
        {
            foreach (string[] row in SwitchNotes)
                if (string.Equals(row[0], key, StringComparison.Ordinal)) return row[1];
            return "未收录说明(见 TEXT38\\TOOLSTACK-TO-SLASH-1.7.md 与 RimWorld.runtimeconfig.json)";
        }

        // ==================== 小工具 ====================

        internal static string SafeStr(Func<string> f)
        {
            try { return f(); }
            catch { return null; }
        }

        /// <summary>把 "键 → 原因" 字典压成稳定排序的一行(key=value 用 " / " 连)。</summary>
        internal static string JoinKeyValues(Dictionary<string, string> map)
        {
            if (map == null || map.Count == 0) return "";
            var keys = new List<string>(map.Keys);
            keys.Sort(StringComparer.Ordinal);
            var parts = new List<string>();
            foreach (string k in keys) parts.Add(k + " = " + map[k]);
            return string.Join(" / ", parts.ToArray());
        }

        /// <summary>注册项启用状态的三态印法:true=启用、false=停用、null=未知(不是 REG_DWORD,按 loader 约定判不了)。</summary>
        internal static string EnabledText(object enabled)
        {
            if (enabled is bool) return ((bool)enabled) ? "启用" : "停用";
            return "未知(非 REG_DWORD)";
        }

        /// <summary>把异常压成一行(TargetInvocationException 拆到内层,反射调用才读得懂)。</summary>
        internal static string Describe(Exception ex)
        {
            if (ex == null) return "null";
            Exception e = ex;
            TargetInvocationException tie = ex as TargetInvocationException;
            if (tie != null && tie.InnerException != null) e = tie.InnerException;
            return e.GetType().Name + ": " + (e.Message ?? "");
        }

        // ==================== 静态表 ====================

        internal sealed class ArgSpec
        {
            public readonly string Scope;
            public readonly string Note;
            public readonly string ValueHint;
            public ArgSpec(string scope, string note, string valueHint)
            {
                Scope = scope;
                Note = note;
                ValueHint = valueHint;
            }
        }

        /// <summary>已知开关表(键不含前缀、大小写不敏感)。来源见文件头;不在表里的一律 scope=unknown。</summary>
        internal static readonly Dictionary<string, ArgSpec> ArgTable = BuildArgTable();

        private static Dictionary<string, ArgSpec> BuildArgTable()
        {
            var t = new Dictionary<string, ArgSpec>(StringComparer.OrdinalIgnoreCase);

            // ---- 引擎开关(Waylith;STOCK-TOOLS-1.7 §1,20 条) ----
            t["console"] = new ArgSpec("engine", "在 Windows 上开一个真控制台窗口(AttachConsole/AllocConsole,接 CONOUT$/CONIN$);-headless 也命中同一分支", null);
            t["headless"] = new ArgSpec("engine", "真无头:GraphicsDevice==null,主循环走 RunHeadlessLoop;无 GPU 表面、截不了图", null);
            t["nowindow"] = new ArgSpec("engine", "窗口创建后隐藏(HideWindow),渲染循环完整 ⇒ 可叠加截图", null);
            t["windowsize"] = new ArgSpec("engine", "初始窗口尺寸并置 EngineWindow.SizeLocked", "W,H");
            t["windowpos"] = new ArgSpec("engine", "初始窗口位置并置 EngineWindow.PositionLocked", "X,Y");
            t["verbose"] = new ArgSpec("engine", "日志最低级别 Debug → Trace", null);
            t["nofilelog"] = new ArgSpec("engine", "FileLog.Disabled=true:不再产生 Player.log", null);
            t["nofilelogcallstacks"] = new ArgSpec("engine", "FileLog.CaptureStackTraces=false", null);
            t["disablelogstacktraces"] = new ArgSpec("engine", "WaylithLogFormatter.EnableStackTraces=false(控制台日志不带调用栈)", null);
            t["suppressunitystubs"] = new ArgSpec("engine", "StubWarner.Suppressed:压掉 UnityStubs 的缺失 API 告警", null);
            t["watchshaders"] = new ArgSpec("engine", "Bootstrap.WatchShaders=true → ShaderHotReloader.Watch() 热重载", null);
            t["noassetpack"] = new ArgSpec("engine", "跳过 Assets.zip 资产包,直接读盘资产", null);
            t["assetpack"] = new ArgSpec("engine", "资产包路径(全表唯一用空格取值的开关,不是 -Name=value)", "<path>");
            t["coldboot"] = new ArgSpec("engine", "ColdBoot.Enabled:冷启动路径(忽略 gpu-profile.bin 等缓存)", null);
            t["trackrehydration"] = new ArgSpec("engine", "GpuTextureService.TrackRehydration:纹理复活追踪", null);
            t["kosmickrisp"] = new ArgSpec("engine", "options.UseKosmicKrisp(作用未定位)", null);
            t["texcachecompression"] = new ArgSpec("engine", "纹理缓存压缩算法;缺省 lz4,未知值也落 lz4", "none|zstd|lz4hc|lz4");
            t["displaycapturecompatibilitymode"] = new ArgSpec("engine", "DisallowFullscreenExclusive=true → 以 VK_FULL_SCREEN_EXCLUSIVE_DISALLOWED_EXT 建 swapchain(录屏/抓帧必开)", null);
            t["nocrashdialog"] = new ArgSpec("engine", "ShowCrashDialog=false:崩溃不弹框(自动化常用)", null);
            t["crashhandler"] = new ArgSpec("engine", "拉起边车 Waylith.CrashHandler.exe(边车未随包 ⇒ 惰性)", null);
            t["crashdumpmini"] = new ArgSpec("engine", "崩溃边车:mini dump(与 -CrashDumpHeap 都不给 = full)", null);
            t["crashdumpheap"] = new ArgSpec("engine", "崩溃边车:heap dump", null);
            t["crashui"] = new ArgSpec("engine", "崩溃边车:UI", null);
            t["crashcapturebreakpoints"] = new ArgSpec("engine", "崩溃边车:抓断点", null);
            t["enablevulkanvalidation"] = new ArgSpec("engine", "加 VK_LAYER_KHRONOS_validation + VkValidationFeaturesEXT(BestPractices/同步校验);它走 Environment.CommandLine.Contains,不走 HasSwitch", null);

            // ---- 游戏开关(Verse;STOCK-TOOLS-1.7 §2 + KNOWN-1.7 §2) ----
            t["quicktest"] = new ArgSpec("game", "进 Entry 场景即 LoadScene(\"Play\")(QuickStarter)", null);
            t["savedatafolder"] = new ArgSpec("game", "重定向存档+配置根:ModsConfig.xml/Prefs.xml/ShaderCache/TextureCache/HardwareProfile 全随之迁移", "<dir>");
            t["screenshotafterboot"] = new ArgSpec("game", "boot 后第 120 帧 CaptureScreenshot 落盘", "<png>");
            t["quitafterboot"] = new ArgSpec("game", "boot 后第 180 帧干净退出(自动化闭环)", null);
            t["mod"] = new ArgSpec("game", "命令行激活模组列表;只改内存不写 ModsConfig.xml,且关不掉官方 DLC", "<a;b>(亦可用空格取下一个参数)");
            t["texformat"] = new ArgSpec("game", "纹理格式覆盖", "<...>");
            t["texquality"] = new ArgSpec("game", "纹理质量覆盖", "<float>");
            t["bakemodassetpacks"] = new ArgSpec("game", "只烘焙 mod 资产包然后退出", "<dir>");
            t["noevictatlassources"] = new ArgSpec("game", "图集源不驱逐(GlobalTextureAtlasManager)", null);
            t["nocompressatlases"] = new ArgSpec("game", "图集不压缩", null);
            t["legacy-xml-deserializer"] = new ArgSpec("game", "用旧版 XML 反序列化器(LoadedModManager)", null);
            t["disable-compute-shaders"] = new ArgSpec("game", "关 compute shader(Verse UnityData)", null);

            // ---- mod 开关 ----
            t["roslynconsole"] = new ArgSpec("mod", "RoslynProbe 的通道开关:裸旗标 = v1 stdio 终端;=名 = v2 命名管道名(空值告警并回退 v1)", "[=管道名]");

            return t;
        }

        /// <summary>11 条引擎读的 WAYLITH_* 环境变量 + 说明(STOCK-TOOLS-1.7 §3)。</summary>
        internal static readonly string[][] KnownWaylithEnv = new string[][]
        {
            new string[] { "WAYLITH_VULKAN_DEVICE", "设备名子串(大小写不敏感)强制选卡" },
            new string[] { "WAYLITH_VRAM_BUDGET_MB", "正整数:设备本地堆上限传 VMA pHeapSizeLimit" },
            new string[] { "WAYLITH_NATIVE_GUARD_PAGES", "== \"1\" 原生分配守护页" },
            new string[] { "WAYLITH_NO_ASSET_PREFETCH", "== \"1\" 关资产预取" },
            new string[] { "WAYLITH_LOAD_ENCODE", "加载期纹理编码模式" },
            new string[] { "WAYLITH_STAGE_UPLOADS_IN_RAM", "!= \"1\" 时 StageUploadsInVram = HostVisibleVram" },
            new string[] { "WAYLITH_PROFILER_FENCED_TSC", "== \"1\" 围栏 TSC" },
            new string[] { "WAYLITH_PROFILER_NO_TSC", "== \"1\" 禁 TSC" },
            new string[] { "WAYLITH_VIDEO_BACKEND", "视频解码后端强制" },
            new string[] { "WAYLITH_DISABLE_WAYLAND_GAME_CONTENT", "Wayland 专用" },
            new string[] { "WAYLITH_SLANG_COMPILER", "字符串表里有此名,读取点未定位" }
        };

        /// <summary>22 条启动期 configProperties 的文档清单(找不到 runtimeconfig.json 时的兜底;RimWorld 1.7.4977 实测值)。</summary>
        internal static readonly string[] DocumentedSwitchKeys = new string[]
        {
            "WaylithClr.AssemblyNameRedirects",
            "WaylithClr.LaxAssemblyBinding",
            "WaylithClr.SkipAccessChecks",
            "WaylithClr.LaxDynamicMethodAttributes",
            "WaylithClr.AllowInitOnlyStaticWrites",
            "WaylithClr.LaxCallvirt",
            "WaylithClr.LaxAssemblyArchitecture",
            "WaylithClr.LaxTypeRefResolution",
            "WaylithClr.LaxMemberSignatures",
            "WaylithClr.DynamicMethodOwnerIsDeclaringType",
            "WaylithClr.CatchableAccessViolations",
            "WaylithClr.JitNoInlinePatchableIntoDynamicMethods",
            "WaylithClr.JitPatchableAssemblies",
            "WaylithClr.LaxInterfaceDispatch",
            "WaylithClr.IsolatedLoadsResolveByName",
            "WaylithClr.LaxPrivateFieldNames",
            "WaylithClr.MonoStringHashCode",
            "WaylithClr.MonoFloatFormatPrecision",
            "WaylithClr.LaxInheritedFieldRefs",
            "WaylithClr.MissingAssemblyTypeLoadException",
            "System.Reflection.Metadata.MetadataUpdater.IsSupported",
            "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization"
        };

        internal static readonly string[][] SwitchNotes = new string[][]
        {
            new string[] { "WaylithClr.AssemblyNameRedirects", "程序集名重定向垫片(UnityEngine*→UnityStubs 等;见本指令 layer 的垫片段)" },
            new string[] { "WaylithClr.LaxAssemblyBinding", "松散程序集绑定:版本/公钥不匹配也允许绑上" },
            new string[] { "WaylithClr.SkipAccessChecks", "跳过访问检查:反射访问私有成员不设防" },
            new string[] { "WaylithClr.LaxDynamicMethodAttributes", "动态方法属性宽松匹配" },
            new string[] { "WaylithClr.AllowInitOnlyStaticWrites", "允许写 initonly(static readonly)字段" },
            new string[] { "WaylithClr.LaxCallvirt", "宽松 callvirt(虚/接口调用容错)" },
            new string[] { "WaylithClr.LaxAssemblyArchitecture", "忽略程序集架构(AnyCPU/x64)不匹配" },
            new string[] { "WaylithClr.LaxTypeRefResolution", "类型引用解析失败可静默降级 —— 别把「没抛异常」当解析成功" },
            new string[] { "WaylithClr.LaxMemberSignatures", "成员签名宽松匹配(返回值/参数有差异也认)" },
            new string[] { "WaylithClr.DynamicMethodOwnerIsDeclaringType", "动态方法的 owner 视为声明类型" },
            new string[] { "WaylithClr.CatchableAccessViolations", "AccessViolation 可被托管 catch(否则直接崩进程)" },
            new string[] { "WaylithClr.JitNoInlinePatchableIntoDynamicMethods", "配套开关:可补丁方法不被内联进动态方法(Harmony/ReJIT 通道)" },
            new string[] { "WaylithClr.JitPatchableAssemblies", "可热补丁程序集白名单(实测 = Assembly-CSharp:游戏逻辑可补,引擎程序集不在名单)" },
            new string[] { "WaylithClr.LaxInterfaceDispatch", "宽松接口派发" },
            new string[] { "WaylithClr.IsolatedLoadsResolveByName", "隔离加载按名字解析" },
            new string[] { "WaylithClr.LaxPrivateFieldNames", "私有字段名宽松(编译器生成名)" },
            new string[] { "WaylithClr.MonoStringHashCode", "Mono 口径的 string.GetHashCode(跨运行时哈希一致)" },
            new string[] { "WaylithClr.MonoFloatFormatPrecision", "Mono 口径的浮点格式化精度" },
            new string[] { "WaylithClr.LaxInheritedFieldRefs", "继承来的字段引用宽松" },
            new string[] { "WaylithClr.MissingAssemblyTypeLoadException", "缺程序集时按 TypeLoadException 响亮失败(而不是静默降级)" },
            new string[] { "System.Reflection.Metadata.MetadataUpdater.IsSupported", "元数据热重载/EnC —— 与 WaylithClr 的 ReJIT(方法体替换)是两回事" },
            new string[] { "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", "BinaryFormatter 反序列化开关(RimWorld 1.7 实测为 true —— 没关)" }
        };
    }
}
