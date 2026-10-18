// RoslynProbe —— Slash 指令表驱动内核 + 结果帮助类。
//
// 零 Verse/Unity/Roslyn 依赖：本文件只依赖 BCL 与 Source/Core(ReplResult 侧)的 SlashResult。
// 三个壳（GUI / stdio v1 / 命名管道 v2）共享这一份内核 —— 壳只负责 IsCommand 分流、
// TryDispatch、ConsoleLog.SystemNote 落账与 quit 特殊处理（与现有 SlashCommands 完全同构）。
//
// 冻结声明：本文件与 SlashContext.cs 是给 13 个并行写手消费的共享 API，T-S0 交付后冻结。
// 写手需要新字段/新委托 → 报告控制者，不要自行修改本文件。
//
// ---- 与现有 SlashCommands.cs 的行为等价性（集成 T-S11 逐字迁移的依据）----
//   * 判定：trimmed 非空且首字符 '/' 才算指令（IsCommand 同款）。
//   * 解析：去掉 '/' 后 Trim；verb = 首个空格前的部分（Trim），rest = 其后全部（Trim）。
//   * 查表：verb 一律 ToLowerInvariant（大小写不敏感）。
//   * 未命中：响亮拒绝 —— Kind="unknown"、Lines/Data 与现有 Unknown(verb) 逐字一致，
//     返回 true（是指令就必定吞下，绝不放行给 C# 编译器）。
//   * 命中但 Handler 为 null：返回 NotImplemented 行（stub 写手可以只登记不实现）。
//   * Handler 抛出：捕获并转成结果行（指令层永不抛，异常都是结果）。

using System;
using System.Collections.Generic;
using System.Reflection;

namespace RoslynProbe
{
    /// <summary>
    /// 一条 Slash 指令的登记项。字段全部公开：
    /// <list type="bullet">
    /// <item><description><c>Verb</c>：不含 '/'，全小写，多词用 '-'（如 "debug-action"）。Register 会 Trim + 小写化。</description></item>
    /// <item><description><c>Usage</c>：help 行里 "参数与说明" 的展示串，形如 <c>[keepUsings] —— 清空会话</c>；
    /// 分隔符是 "——"（U+2014 ×2）。无参数就写 <c>—— 说明</c>，无说明就只写参数。HelpLines 渲染成 <c>"  /verb " + Usage</c>。</description></item>
    /// <item><description><c>Group</c>：help 分组名（可空）。只影响排序与 HelpData 的 "group" 键，不额外插分组标题。</description></item>
    /// <item><description><c>Kind</c>：SlashResult.Kind（小写无空格）。空则默认取 Verb。壳层据此做特殊处理（如 "quit"）。</description></item>
    /// <item><description><c>Dangerous</c>：是否有副作用（/quit、/reset、/hook off、/dev 置位…）。只进 HelpData 的 "dangerous" 键，不改 Lines。</description></item>
    /// <item><description><c>Handler</c>：<c>(rest, ctx) =&gt; SlashResult</c>。rest 已 Trim。为 null 表示"已登记未实现"。</description></item>
    /// </list>
    /// </summary>
    public sealed class SlashEntry
    {
        /// <summary>不含 '/'，全小写，多词用 '-'。Register 会 Trim 并 ToLowerInvariant。</summary>
        public string Verb;

        /// <summary>help 展示串：<c>[参数] —— 说明</c>（分隔符 "——"）。</summary>
        public string Usage;

        /// <summary>help 分组名（可空）。</summary>
        public string Group;

        /// <summary>SlashResult.Kind（小写无空格）。空则默认取 Verb。</summary>
        public string Kind;

        /// <summary>是否有副作用。只进 HelpData。</summary>
        public bool Dangerous;

        /// <summary>指令实现：rest 已 Trim、ctx 必非 null。返回 null 会被内核换成错误行。</summary>
        public Func<string, SlashContext, SlashResult> Handler;

        public SlashEntry() { }

        public SlashEntry(
            string verb,
            string usage,
            Func<string, SlashContext, SlashResult> handler,
            string kind = null,
            string group = null,
            bool dangerous = false)
        {
            Verb = verb;
            Usage = usage;
            Handler = handler;
            Kind = kind;
            Group = group;
            Dangerous = dangerous;
        }
    }

    /// <summary>
    /// 表驱动 Slash 内核：静态表 + 查表分派 + help 同源生成。
    /// 单条指令一个文件（<c>Cmds/*.cs</c>），文件内 <c>public static void Register()</c> 由
    /// <see cref="RegisterAll"/> 反射统一调用 —— 写手不需要改任何共享文件。
    /// </summary>
    public static class SlashRegistry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, SlashEntry> Table =
            new Dictionary<string, SlashEntry>(StringComparer.Ordinal);
        private static bool allRegistered;

        /// <summary>已登记条数（快照，测试与 /status 用）。</summary>
        public static int Count { get { lock (Gate) { return Table.Count; } } }

        /// <summary>
        /// 登记一条指令。重名（按小写 verb 比较）立刻抛 —— 响亮，绝不让两条指令抢同一个 verb。
        /// verb 为空/含空白/含 '/'、或 Kind 含空白，同样立刻抛。
        /// </summary>
        public static void Register(SlashEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");

            string verb = (entry.Verb ?? "").Trim().ToLowerInvariant();
            if (verb.Length == 0)
                throw new ArgumentException("SlashEntry.Verb 不能为空", "entry");
            if (verb.IndexOf('/') >= 0)
                throw new ArgumentException("SlashEntry.Verb 不能含 '/': \"" + entry.Verb + "\"", "entry");
            for (int i = 0; i < verb.Length; i++)
                if (char.IsWhiteSpace(verb[i]))
                    throw new ArgumentException("SlashEntry.Verb 不能含空白: \"" + entry.Verb + "\"", "entry");

            string kind = string.IsNullOrEmpty(entry.Kind) ? verb : entry.Kind.Trim();
            if (kind.Length == 0) kind = verb;
            for (int i = 0; i < kind.Length; i++)
                if (char.IsWhiteSpace(kind[i]))
                    throw new ArgumentException("SlashEntry.Kind 不能含空白（约定小写无空格）: \"" + entry.Kind + "\"", "entry");

            entry.Verb = verb;
            entry.Kind = kind;
            entry.Usage = entry.Usage ?? "";
            entry.Group = entry.Group ?? "";

            lock (Gate)
            {
                SlashEntry existing;
                if (Table.TryGetValue(verb, out existing))
                    throw new InvalidOperationException(
                        "Slash 指令重名: /" + verb + " 已由 " + existing.GetType().FullName + " 登记（Kind=" + existing.Kind + "）");
                Table.Add(verb, entry);
            }
        }

        /// <summary>
        /// **测试隔离专用**：清空整张表并复位 RegisterAll 幂等标记。运行时不需要（也绝不该调）。
        /// 各测试类在自己的用例开头调它，避免「重名抛」把同进程其它用例互相打断。
        /// </summary>
        public static void ResetForTests()
        {
            lock (Gate)
            {
                Table.Clear();
                allRegistered = false;
            }
        }

        /// <summary>按 verb（大小写不敏感）查表。</summary>
        public static bool TryGet(string verb, out SlashEntry entry)
        {
            entry = null;
            if (verb == null) return false;
            lock (Gate) { return Table.TryGetValue(verb.Trim().ToLowerInvariant(), out entry); }
        }

        /// <summary>全表快照，按 (Group, Verb) 升序稳定排序 —— HelpLines / HelpData 的唯一数据源。</summary>
        public static List<SlashEntry> Snapshot()
        {
            List<SlashEntry> list;
            lock (Gate) { list = new List<SlashEntry>(Table.Values); }
            list.Sort(CompareEntries);
            return list;
        }

        private static int CompareEntries(SlashEntry a, SlashEntry b)
        {
            int g = string.CompareOrdinal(a.Group ?? "", b.Group ?? "");
            if (g != 0) return g;
            return string.CompareOrdinal(a.Verb ?? "", b.Verb ?? "");
        }

        /// <summary>是否 Slash 指令（trim 后首字符 '/'）——与现有 SlashCommands.IsCommand 逐字等价。</summary>
        public static bool IsCommand(string input)
        {
            string t = (input ?? "").TrimStart();
            return t.Length > 0 && t[0] == '/';
        }

        /// <summary>
        /// verb 与 rest 的**分隔符口径**：任意空白（<see cref="char.IsWhiteSpace(char)"/>）。
        /// 与壳层的 token 切分（<c>SlashJson.TryStrip</c>）和指令自己的参数解析
        /// （<c>UsingAddCommand.ParseItems</c>）同一口径 —— 全链路口径一致，才不会出现
        /// 「TAB/全角空格/NBSP 分隔时同一条指令走另一条路」这类静默错答（F1）。
        /// 返回 −1 = 没有分隔空白（整串都是 verb）。
        /// </summary>
        internal static int IndexOfWhitespace(string s)
        {
            if (s == null) return -1;
            for (int i = 0; i < s.Length; i++)
                if (char.IsWhiteSpace(s[i])) return i;
            return -1;
        }

        /// <summary>
        /// 分派一行输入。返回 false 仅当它**不是**指令（不以 '/' 开头）；
        /// 是指令则必定返回 true —— 包括未命中（Unknown 响亮拒绝），绝不放行给 C# 编译器。
        /// ctx 为 null 时自动降级为空上下文（能力全部未注册）。
        /// </summary>
        public static bool TryDispatch(string input, SlashContext ctx, out SlashResult result)
        {
            result = null;
            string trimmed = (input ?? "").Trim();
            if (trimmed.Length == 0 || trimmed[0] != '/') return false;

            string body = trimmed.Substring(1).Trim();
            string verb = body, rest = "";
            int sp = IndexOfWhitespace(body);
            if (sp >= 0)
            {
                verb = body.Substring(0, sp).Trim();
                rest = body.Substring(sp + 1).Trim();
            }

            SlashEntry entry;
            if (!TryGet(verb, out entry))
            {
                result = SlashResults.Unknown(verb);
                return true;
            }

            if (entry.Handler == null)
            {
                result = SlashResults.NotImplemented(entry.Kind, entry.Verb);
                return true;
            }

            SlashContext context = ctx ?? new SlashContext();
            try
            {
                result = entry.Handler(rest, context);
            }
            catch (Exception ex)
            {
                result = SlashResults.Error(entry.Kind,
                    "指令 /" + entry.Verb + " 抛出异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            if (result == null)
                result = SlashResults.Error(entry.Kind, "指令 /" + entry.Verb + " 的 handler 返回 null");
            return true;
        }

        /// <summary>
        /// help 的人读行。与 <see cref="HelpData"/> 同源：两者都只读 <see cref="Snapshot"/>，
        /// 顺序、条数、verb 集合必然一致（tests/SlashHarness/SlashRegistryTests.cs 断言这一点）。
        /// </summary>
        public static string[] HelpLines()
        {
            List<SlashEntry> entries = Snapshot();

            int col = 0;
            var lefts = new string[entries.Count];
            var notes = new string[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                string argsPart, note;
                SplitUsage(entries[i].Usage, out argsPart, out note);
                string label = "/" + entries[i].Verb + (argsPart.Length > 0 ? " " + argsPart : "");
                lefts[i] = label;
                notes[i] = note;
                if (label.Length > col) col = label.Length;
            }
            col += 2;

            var lines = new List<string>();
            lines.Add("可用指令:");
            for (int i = 0; i < entries.Count; i++)
            {
                string line = "  " + lefts[i].PadRight(col);
                if (notes[i].Length > 0) line += "—— " + notes[i];
                else line = line.TrimEnd();
                lines.Add(line);
            }
            lines.Add("未知指令一律报错，不会当作 C# 编译。");
            return lines.ToArray();
        }

        /// <summary>
        /// help 的机器可读数据。键名小驼峰且稳定（工具消费方依赖）：
        /// <c>count</c>(int)、<c>verbs</c>(List&lt;string&gt;，带 '/' 前缀)、
        /// <c>commands</c>(List&lt;Dictionary&lt;string,object&gt;&gt;，每项 verb/args/note/usage/group/kind/dangerous/implemented)。
        /// </summary>
        public static Dictionary<string, object> HelpData()
        {
            List<SlashEntry> entries = Snapshot();
            var verbs = new List<string>();
            var commands = new List<Dictionary<string, object>>();
            foreach (SlashEntry e in entries)
            {
                string argsPart, note;
                SplitUsage(e.Usage, out argsPart, out note);
                verbs.Add("/" + e.Verb);
                commands.Add(new Dictionary<string, object>
                {
                    ["verb"] = "/" + e.Verb,
                    ["args"] = argsPart,
                    ["note"] = note,
                    ["usage"] = e.Usage,
                    ["group"] = e.Group,
                    ["kind"] = e.Kind,
                    ["dangerous"] = e.Dangerous,
                    ["implemented"] = e.Handler != null
                });
            }
            return new Dictionary<string, object>
            {
                ["count"] = entries.Count,
                ["verbs"] = verbs,
                ["commands"] = commands
            };
        }

        /// <summary>把 Usage 拆成 "参数" 与 "说明" 两段（分隔符 "——"）。</summary>
        public static void SplitUsage(string usage, out string args, out string note)
        {
            string u = (usage ?? "").Trim();
            if (u.Length == 0) { args = ""; note = ""; return; }
            int i = u.IndexOf("——", StringComparison.Ordinal);
            if (i < 0) { args = u; note = ""; return; }
            if (i == 0) { args = ""; note = u.Substring(2).Trim(); return; }
            args = u.Substring(0, i).Trim();
            note = u.Substring(i + 2).Trim();
        }

        /// <summary>
        /// 反射发现并调用本程序集内所有 <c>public static void Register()</c>（无参、返回 void、非泛型类型），
        /// 按类型全名序执行。幂等：第二次调用是 no-op。
        /// 约定：每个指令文件是 <c>Cmds/&lt;Name&gt;Command.cs</c> + 一个 <c>public static void Register()</c>，
        /// 因此写手只碰自己的文件，永远不改共享登记表（T-S11 Bootstrap 只需调这一句）。
        /// </summary>
        public static void RegisterAll()
        {
            lock (Gate)
            {
                if (allRegistered) return;
                allRegistered = true;
            }

            Type[] types;
            try
            {
                types = typeof(SlashRegistry).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loaded = new List<Type>();
                foreach (Type t in ex.Types) if (t != null) loaded.Add(t);
                types = loaded.ToArray();
            }

            var methods = new List<MethodInfo>();
            foreach (Type t in types)
            {
                if (t == null || t == typeof(SlashRegistry)) continue;
                if (t.IsGenericTypeDefinition || t.ContainsGenericParameters) continue;
                MethodInfo m = t.GetMethod("Register", BindingFlags.Public | BindingFlags.Static,
                    null, Type.EmptyTypes, null);
                if (m == null || m.ReturnType != typeof(void)) continue;
                methods.Add(m);
            }
            methods.Sort((a, b) => string.CompareOrdinal(
                a.DeclaringType == null ? "" : a.DeclaringType.FullName,
                b.DeclaringType == null ? "" : b.DeclaringType.FullName));

            foreach (MethodInfo m in methods)
                m.Invoke(null, null);
        }

        /// <summary>T-S11 集成后主工程不再需要本方法；供 /status 之类显示登记状态。</summary>
        public static List<string> RegisteredVerbs()
        {
            List<SlashEntry> entries = Snapshot();
            var verbs = new List<string>(entries.Count);
            foreach (SlashEntry e in entries) verbs.Add("/" + e.Verb);
            return verbs;
        }
    }

    /// <summary>
    /// SlashResult 的便捷构造（Source/Game/Repl/SlashResult.cs 是共享文件、零依赖，不得修改，
    /// 故便捷构造集中放在这里）。约定的 Kind：unknown / missing-capability / capability-error / error。
    /// </summary>
    public static class SlashResults
    {
        /// <summary>成功结果：只有 Kind 与 Data["ok"]=true。</summary>
        public static SlashResult Ok(string kind)
        {
            var r = new SlashResult { Kind = kind ?? "ok" };
            r.Data = new Dictionary<string, object> { ["ok"] = true };
            return r;
        }

        /// <summary>失败结果：Data["ok"]=false、Data["error"]=message，并把 message 作为唯一行。</summary>
        public static SlashResult Error(string kind, string message)
        {
            string msg = string.IsNullOrEmpty(message) ? "未知错误" : message;
            var r = new SlashResult { Kind = string.IsNullOrEmpty(kind) ? "error" : kind };
            r.Lines.Add(msg);
            r.Data = new Dictionary<string, object> { ["ok"] = false, ["error"] = msg };
            return r;
        }

        /// <summary>
        /// 未知指令 —— 与现有 SlashCommands.Unknown(verb) 的 Kind/Lines/Data 逐字一致。
        /// 响亮拒绝：绝不把 '/' 开头的行交给 C# 编译器。
        /// </summary>
        public static SlashResult Unknown(string verb)
        {
            string v = verb ?? "";
            var r = new SlashResult { Kind = "unknown" };
            r.Lines.Add("未知指令: /" + v + " —— 拒绝执行（不会当作 C# 编译）。可用指令见 /help");
            r.Data = new Dictionary<string, object> { ["ok"] = false, ["unknown"] = v };
            return r;
        }

        /// <summary>
        /// stub 结果：Kind 用该指令自己的 Kind（壳层行为了然），Data["implemented"]=false。
        /// 写手先把指令登记进表、handler 留 null 或显式返回本结果，后续再补实现。
        /// </summary>
        public static SlashResult NotImplemented(string kind, string verb)
        {
            string v = verb ?? "";
            var r = new SlashResult { Kind = string.IsNullOrEmpty(kind) ? v : kind };
            r.Lines.Add("指令 /" + v + " 尚未实现（stub，Kind=" + r.Kind + "）。");
            r.Data = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["implemented"] = false,
                ["kind"] = r.Kind,
                ["verb"] = v
            };
            return r;
        }

        /// <summary>
        /// 能力未注册（姿势 B）：Bootstrap 没有注入该 provider。指令调用点遇到本行就返回它。
        /// </summary>
        public static SlashResult CapabilityMissing(string capability)
        {
            string cap = string.IsNullOrEmpty(capability) ? "?" : capability;
            var r = new SlashResult { Kind = "missing-capability" };
            r.Lines.Add("能力未注册: " + cap + " —— 游戏侧 provider 未由 Bootstrap 注入（姿势 B）；该指令暂不可用。");
            r.Data = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "capability not registered",
                ["capability"] = cap
            };
            return r;
        }

        /// <summary>无参指令的标准"用法"结果（Lines 给出用法，Kind 用该指令自己的 Kind）。</summary>
        public static SlashResult Usage(string kind, params string[] lines)
        {
            var r = new SlashResult { Kind = string.IsNullOrEmpty(kind) ? "usage" : kind };
            if (lines != null)
                foreach (string l in lines) if (!string.IsNullOrEmpty(l)) r.Lines.Add(l);
            r.Data = new Dictionary<string, object> { ["ok"] = false, ["usage"] = true };
            return r;
        }

        /// <summary>
        /// /help 的规范实现体：Lines 与 Data 同源（都来自 SlashRegistry 同一张表）。
        /// 集成时 HelpCommand 的 handler 就写 <c>return SlashResults.Help();</c>。
        /// </summary>
        public static SlashResult Help()
        {
            var r = new SlashResult { Kind = "help" };
            r.Lines.AddRange(SlashRegistry.HelpLines());
            r.Data = SlashRegistry.HelpData();
            return r;
        }
    }
}
