// RoslynProbe —— /usings add <条目> [...]：校验后把一批 using 追加进会话。
//
// 姿势 B 的合规样板见本目录的 LogCommand.cs。
//
// ---- verb 命名（本文件为什么登记 "usings-add" 而不是 "usings"）----
// "usings" 这个 verb 归**两段式别名项**（Source/Game/Repl/SlashCommands.cs 的 UsingsHandler），
// 且 SlashRegistry.Register 对重名**立刻抛** ⇒ 本指令登记独立 verb "usings-add"。
// 两段式别名项反查本表并委托：
//     SlashEntry e; if (SlashRegistry.TryGet("usings-add", out e)) result = e.Handler("add " + itemsText, ctx);
// 正因如此本指令解析时传 skipFirstWord="add"（大小写不敏感），反查时不必自己剥前缀。
// 两个形态等价：/usings add <条目> ≡ /usings-add add <条目>（表内 verb 是机器面名字）。
//
// ---- 姿势 B ----
// handler 只写 return ctx.CallUsingAdd(rest); —— 不写 if (ctx.UsingAdd == null)（同一个语义、更啰嗦）。
// provider 本体在本文件：public static SlashResult Provide(string rest, SlashContext ctx)。
// Bootstrap 注入 ctx.UsingAdd = UsingAddCommand.Provide；未注入时 CallUsingAdd 自己回
// 「能力未注册」行，指令层永不抛。
//
// ---- 线程 ----
// 本 provider **不** marshal：AddUsings 只碰 RoslynEngine 自己的 stateLock 与两张 string 表，
// 不碰 Unity/Verse；HTTP POST /using 本来就在 HTTP 后台线程上调它
// （HTTP POST /using 时代就是后台线程直调），故会话读线程直调是既有口径。
// RoslynEngine.AddUsings 的内部约定（锁内规范化/去重 + 锁外试编译 + 锁内落盘再查重）见其文件头注释。
//
// ---- 参数解析（B1：双引号分段）----
// 纯空白切分**会破坏**含空格的条目（别名 "SB = System.Text.StringBuilder"、静态导入 "static System.Math"）——
// 被拆散后逐条拒绝；而引擎的 AddUsings 本来就支持它们（走 directives 那条路）。故 ParseItems 支持双引号：
// **引号内的空白不是分隔符**。
//     /usings add "SB = System.Text.StringBuilder" "static System.Math"
// 边界（写死，不猜）：未闭合引号 → 用法错误；空引号（"" 或只含空白的引号）→ 用法错误（**不静默丢**）；
// 引号内最小转义：\" 表示引号、\\ 表示反斜杠，其它 \x 原样保留；不带引号的普通多条目行为**逐字不变**。
// /usings remove 的切分在 Source/Game/Repl/SlashCommands.cs（已改指过来共用本解析器）。
// 解析器做成 public 且**不含任何指令语义**：跳过首个词是**调用方自己的动词规则**，通过
// skipFirstWord 参数显式选择 —— 本指令传 "add"；usings remove 传 null（绝不静默吃掉一个 token）。
//
// ---- Data（键名小驼峰且稳定）----
//   { added:int, accepted:int, rejected:[{item, error}] }
//   added    = report.Added（本次**真正追加**的条数）；
//   accepted = report.Accepted（未被拒绝的条数，含已存在被去重跳过的）；
//   rejected = report.Rejected 逐条的 Item/Error（形态 {item, error}）。
// 成功路径**不放** ok 键：三个字段本身就有区分度（added/accepted 可为 0，rejected 可空），
// 而恒为 true 的能力位是谎报（项目级裁定 3）。
// **失败/用法路径分三种，键集不同** —— 只读 Data 的消费者（--json）按这三行取：
//   * 运行期失败（ctx.Host 未接线 / AddUsings 抛异常 / 返回 null）→ SlashResults.Error：{ ok:false, error:原文 }
//   * 参数错误（未闭合引号 / 空引号）→ SlashResults.Usage：{ ok:false, usage:true, error:原文 }
//     —— usage:true = "给了用法页"，error = **引号错误原文**（O-1：只读 Data 的消费者也能直接拿到原因，不必解析 Lines；
//        该键只在本指令这条路上加，**共享 SlashResults.Usage 的形状未动**）
//   * 空参数（rest 为空 / 只有 "add"）→ SlashResults.Usage：{ ok:false, usage:true }（**无** error 键：这不是错误）

using System;
using System.Collections.Generic;
using RoslynProbe.Repl;   // UsingAddReport / UsingRejection（姿势 B provider 的数据源）

namespace RoslynProbe
{
    /// <summary>/usings add —— 校验并追加一批 using（姿势 B：游戏侧 provider 在本文件内）。</summary>
    public static class UsingAddCommand
    {
        /// <summary>表内登记的 verb（/usings 已被 legacy SlashCommands 占用，见文件头）。</summary>
        public const string Verb = "usings-add";

        /// <summary>SlashResult.Kind（小写无空格）。</summary>
        public const string Kind = "usings-add";

        /// <summary>RegisterAll 反射调用（全程序集 public static void Register()，无参、返回 void）。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = Verb,
                // F5：Usage 的**参数段**只放参数规格（不带 verb、不带前导 '/'）—— help 渲染成
                // "  /usings-add <条目> [...]"；两段式别名在说明段里点名（人类读者照样看得到）。
                Usage = "<条目> [...] —— 新增 using（两段式别名:/usings add <条目> [...]）",
                Group = "会话",
                Kind = Kind,
                // 有副作用：改的是会话的 usings/directives 两表，影响之后的**每一次**编译（可经 /usings remove 撤销）。
                Dangerous = true,
                Handler = Handle
            });
        }

        /// <summary>handler：只把调用转给姿势 B provider（未注入时 CallUsingAdd 自己回 missing-capability 行）。</summary>
        private static SlashResult Handle(string rest, SlashContext ctx)
        {
            return ctx.CallUsingAdd(rest);
        }

        // ================= 姿势 B provider（游戏侧；集成时 Bootstrap 注入 ctx.UsingAdd = UsingAddCommand.Provide） =================

        /// <summary>
        /// /usings add 的 provider：解析 → 用法页 / 未接线 / 落盘 → 渲染。本方法绝不抛。
        /// </summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            string parseError;
            // skipFirstWord="add"：本指令自己的动词规则（集成阶段 "/usings add X" 会整串 "add X" 反查进来）。
            List<string> items = ParseItems(rest, out parseError, "add");
            if (!string.IsNullOrEmpty(parseError)) return UsageResult(parseError);
            if (items.Count == 0) return UsageResult(null);

            SlashContext c = ctx ?? new SlashContext();
            if (c.Host == null)
                return SlashResults.Error(Kind,
                    "会话未接线：ctx.Host 为 null（ReplHost 未注入）—— 无法追加 using。");

            UsingAddReport report;
            try
            {
                report = c.Host.AddUsings(items);
            }
            catch (Exception ex)
            {
                // AddUsings 内部已把"坏条目"收敛成 Rejected；走到这里是真的没预料到的故障，如实报告。
                return SlashResults.Error(Kind,
                    "追加 using 失败: " + ex.GetType().Name + ": " + ex.Message);
            }
            if (report == null)
                return SlashResults.Error(Kind, "追加 using 失败：ReplHost.AddUsings 返回 null（未预期的状态）。");

            return BuildResult(report, items);
        }

        // ================= 纯逻辑（可单测） =================

        /// <summary>
        /// 解析文本成条目列表（**通用解析器：不含任何指令语义**）。规则（写死，避免歧义）：
        /// <list type="bullet">
        /// <item><description>未加引号的空白（空格/制表符）是分隔符；连续空白不产生空条目；</description></item>
        /// <item><description>**双引号内的空白不是分隔符**（B1）⇒ 含空格的条目可表达；还原出的条目**不含外层引号**；</description></item>
        /// <item><description>引号内最小转义：<c>\"</c> 表示引号本身、<c>\\</c> 表示反斜杠；其它 <c>\x</c> 原样保留（不猜）；</description></item>
        /// <item><description>首个**未被引号包住**的词若等于 <paramref name="skipFirstWord"/>（大小写不敏感，
        /// 且该参数非 null）就跳过 —— 这是**调用方自己的动词规则**，不是解析器的通用语义：
        /// <c>usings-add</c> 传 <c>"add"</c>（于是集成阶段 "/usings" 的 handler 可以把 "add System.IO"
        /// 整串反查进本表，而不必自己剥前缀）；<c>usings remove</c> 传 <c>null</c>（条目就是条目，
        /// 绝不静默吃掉一个 token）；</description></item>
        /// <item><description>空列表（文本为空、或按上述规则跳完只剩空）由调用方转成用法页。</description></item>
        /// </list>
        /// 失败一律由 <paramref name="error"/> 带回（调用方给用法页），**绝不猜**：
        /// 未闭合引号；空引号（<c>""</c> 或引号内只含空白）。
        /// </summary>
        public static List<string> ParseItems(string rest, out string error, string skipFirstWord)
        {
            error = null;
            var items = new List<string>();
            string r = (rest ?? "").Trim();
            if (r.Length == 0) return items;

            int i = 0;
            bool first = true;
            while (i < r.Length)
            {
                while (i < r.Length && char.IsWhiteSpace(r[i])) i++;   // 跳过分隔空白
                if (i >= r.Length) break;

                string item;
                bool quoted = false;
                if (r[i] == '"')
                {
                    quoted = true;
                    int openAt = i;
                    i++;                                              // 跳过开引号
                    var sb = new System.Text.StringBuilder();
                    bool closed = false;
                    while (i < r.Length)
                    {
                        char ch = r[i];
                        if (ch == '\\' && i + 1 < r.Length && (r[i + 1] == '"' || r[i + 1] == '\\'))
                        {
                            sb.Append(r[i + 1]);                      // 最小转义：\" 与 \\
                            i += 2;
                            continue;
                        }
                        if (ch == '"') { i++; closed = true; break; }
                        sb.Append(ch);
                        i++;
                    }
                    if (!closed)
                    {
                        string rest2 = r.Substring(openAt);
                        if (rest2.Length > 60) rest2 = rest2.Substring(0, 60) + "...";
                        error = "引号未闭合：" + rest2 + "（引号内的空白不会被当作分隔符；请补上收尾引号）";
                        return new List<string>();
                    }
                    item = sb.ToString();
                    if (item.Trim().Length == 0)
                    {
                        error = "空引号 \"\" 不是一个有效条目（请写引号内的条目原文，或整条删掉）";
                        return new List<string>();
                    }
                }
                else
                {
                    int start = i;
                    while (i < r.Length && !char.IsWhiteSpace(r[i])) i++;
                    item = r.Substring(start, i - start);
                }

                bool skip = first && !quoted && skipFirstWord != null
                    && item.Equals(skipFirstWord, StringComparison.OrdinalIgnoreCase);
                first = false;
                if (!skip) items.Add(item);
            }
            return items;
        }

        /// <summary>
        /// 通用重载：**不跳过任何首词**（skipFirstWord == null）。
        /// 需要"跳过自己的动词"的调用方用带 <c>skipFirstWord</c> 的重载（本例：<c>usings-add</c> 传 "add"）。
        /// </summary>
        public static List<string> ParseItems(string rest, out string error)
        {
            return ParseItems(rest, out error, null);
        }

        /// <summary>
        /// 兼容旧调用点（单参重载）：不跳过任何首词；解析失败时返回空列表。
        /// 调用方若需要错误文案，用带 <c>out error</c> 的重载。
        /// </summary>
        public static List<string> ParseItems(string rest)
        {
            string ignored;
            return ParseItems(rest, out ignored, null);
        }

        /// <summary>Data["rejected"]：List&lt;Dictionary&gt;，键 item / error（与 HTTP POST /using 的 rejected[] 同形）。</summary>
        public static List<Dictionary<string, object>> RejectedData(UsingAddReport report)
        {
            var list = new List<Dictionary<string, object>>();
            if (report == null || report.Rejected == null) return list;
            foreach (UsingRejection rej in report.Rejected)
            {
                if (rej == null) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["item"] = rej.Item ?? "",
                    ["error"] = rej.Error ?? ""
                });
            }
            return list;
        }

        /// <summary>
        /// 由落盘报告渲染结果：Lines（汇总 + 逐条拒绝点名）+ Data（added / accepted / rejected）。
        /// requested 只用来在"全被规范化丢弃"时给一句可读解释（不进 Data，形状保持三键）。
        /// </summary>
        public static SlashResult BuildResult(UsingAddReport report, List<string> requested)
        {
            int added = report == null ? 0 : report.Added;
            int accepted = report == null ? 0 : report.Accepted;
            List<Dictionary<string, object>> rejected = RejectedData(report);

            var r = new SlashResult { Kind = Kind };
            string summary = "已追加 " + added + " 条 using（接受 " + accepted + " 条";
            if (rejected.Count > 0) summary += "，拒绝 " + rejected.Count + " 条";
            summary += "）。";
            r.Lines.Add(summary);

            if (accepted == 0 && rejected.Count == 0)
            {
                int n = requested == null ? 0 : requested.Count;
                r.Lines.Add("（提交的 " + n + " 个条目都被规范化后丢弃，无一落盘 —— 例：条目只含 \"using\" 关键字或空白）");
            }
            foreach (Dictionary<string, object> rej in rejected)
                r.Lines.Add("拒绝: " + rej["item"] + " —— " + rej["error"]);

            r.Data = new Dictionary<string, object>
            {
                ["added"] = added,
                ["accepted"] = accepted,
                ["rejected"] = rejected
            };
            return r;
        }

        /// <summary>用法页（无错误文案时的兼容重载）。</summary>
        public static SlashResult UsageResult()
        {
            return UsageResult(null);
        }

        /// <summary>
        /// 用法页（provider 收到空条目或参数错误时；error 非空则置顶一行“错误: …”）。
        /// Data：{ ok:false, usage:true }；且 <paramref name="error"/> 非空时**额外**带 <c>error</c> = 原文 ——
        /// 只读 Data 的消费者（--json）据此可直接拿到参数错误原因，不必解析 Lines（O-1）。
        /// 空参数（error 为空）**不带** error 键：那不是错误，只是"要看一下用法"。
        /// 该键只在本指令这条路上加，**共享 SlashResults.Usage 的形状未动**。
        /// </summary>
        public static SlashResult UsageResult(string error)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(error)) lines.Add("错误: " + error);
            lines.Add("用法: /usings add <条目> [...]");
            lines.Add("  例: /usings add System.IO");
            lines.Add("  例: /usings add \"SB = System.Text.StringBuilder\" \"static System.Math\"");
            lines.Add("<条目> —— 命名空间（System.IO）、别名（SB = System.Text.StringBuilder）或静态导入（static System.Math）");
            lines.Add("含空格的条目（别名 / 静态导入）**必须用双引号包住**：引号内的空白不是分隔符；");
            lines.Add("  引号必须成对（未闭合 → 用法错误）；空引号 \"\" 是错误；引号内 \\\" 表示引号、\\\\ 表示反斜杠");
            lines.Add("每条都用同一编译器试编译通过后才落盘；坏条目被拒并点名，不污染之后的编译（RoslynEngine.AddUsings 口径）");
            lines.Add("本表登记名 /usings-add；两段式别名 /usings add 与它等价（同一条实现，见 /help）");
            SlashResult r = SlashResults.Usage(Kind, lines.ToArray());
            // O-1：参数错误的原因**同时**进 Data —— 只读 Data 的消费者（落地的 --json）不能只看到
            // ok=false/usage=true 而无从知道哪里错。只在本指令自己的这条路上加键，共享形状未动。
            if (!string.IsNullOrEmpty(error)) r.Data["error"] = error;
            return r;
        }
    }
}
