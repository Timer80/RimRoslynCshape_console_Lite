// RoslynProbe —— Slash 指令统一层:兼容壳 + legacy 5 条的表驱动实现（集成 T-S11/S11-2）。
//
// 历史：本文件曾是唯一内核（switch 分派）。S11-2 把 /help /usings /usings remove /reset /quit
// 迁到表驱动内核（SlashRegistry），本文件降级为**兼容壳**：
//   * IsCommand / TryDispatch 保留为内核兼容入口（内部转发到 SlashRegistry）。两个壳（GUI /
//     命名管道 v2）**经 SlashJson.TryDispatch 进这里**（壳层的 --json 剥离/渲染在那里，见 SlashJson.cs）；
//   * legacy 5 条的注册与 handler 仍住在本文件（RegisterAll 反射调用这里的 public static Register()）。
//   * 还登记**两段式别名**（/usings add|remove、/ui open）：薄 handler 只做「子动词分流 + 反查表项 +
//     委托」，自己不实现功能 —— 两段式给人用，连字符式（usings-add / usings-remove / ui-open）是表内
//     verb（机器面）。等价关系与写法约定见 docs/slash-command-authoring.md §3.7。
// 边界：本文件零 Unity/Verse 依赖；碰 ConsoleLog 的动作一律经 Executor 队列 marshal（与旧实现一致）。
//
// 铁律（与旧实现同）：'/' 开头的行永远不进 Roslyn 编译器；未知指令响亮拒绝，绝不静默。

using System;
using System.Collections.Generic;
using RoslynProbe.Repl;

namespace RoslynProbe
{
    /// <summary>
    /// Slash 指令兼容壳。TryDispatch 返回 false 仅当输入不是指令（不以 "/" 开头）；
    /// 是指令则必定返回 true —— 包括未知指令（响亮报错），绝不放行给 C# 编译器。
    /// </summary>
    public static class SlashCommands
    {
        // ---- 通道名（壳层填进 SlashContext.Channel；字面量集中在此，避免三处拼错）----

        /// <summary>游戏内 GUI 控制台（ConsoleWindow.Submit，OnGUI ⇒ 游戏线程）。</summary>
        public const string ChannelGui = "gui";
        /// <summary>命名管道 v2（PipeConsole，会话读线程）。</summary>
        public const string ChannelPipe = "pipe";

        /// <summary>
        /// 是否 Slash 指令（trim 后首字符 '/'）—— 判定与 SlashRegistry.IsCommand 同实现、与通道无关。
        /// <paramref name="channel"/> 只为让分流与随后的 TryDispatch 共用同一个通道常量，不参与判定。
        /// </summary>
        public static bool IsCommand(string input, string channel = null)
        {
            return SlashRegistry.IsCommand(input);
        }

        /// <summary>
        /// 内核兼容入口：内部转发到表驱动内核（SlashRegistry.TryDispatch）。本方法**不认 --json**
        /// （旗标是壳层语义：两个壳经 SlashJson.TryDispatch 调这里，剥离/渲染都发生在那一层）。
        /// <paramref name="channel"/> 进 ctx.Channel（gui/pipe）；
        /// <paramref name="isGameThread"/> 是壳层填的线程事实位（契约 v1.1 §3.4.1，拿不准填 false）。
        /// 姿势 B 的 8 个能力委托由 SlashCapabilities 从 Bootstrap 注入的模板逐字复制。
        /// </summary>
        public static bool TryDispatch(string input, ReplHost host, Executor executor, out SlashResult result,
                                       string channel = null, bool isGameThread = false)
        {
            SlashContext ctx = SlashCapabilities.NewContext(host, executor, channel, isGameThread);
            return SlashRegistry.TryDispatch(input, ctx, out result);
        }

        // ================= legacy 5 条 + 两段式别名的登记（RegisterAll 反射调用）=================

        /// <summary>表内登记的 usings-remove verb（与 UsingAddCommand.Verb 同族的独立 verb，不改名 usings）。</summary>
        public const string UsingRemoveVerb = "usings-remove";

        /// <summary>两段式别名 verb：/ui open 与表内 verb ui-open 等价（见 docs/slash-command-authoring.md §3.7）。</summary>
        public const string UiAliasVerb = "ui";

        /// <summary>两段式 /ui 的用法行（空子动词与非 open 子动词共用）。</summary>
        private const string UiUsageLine = "用法: /ui open（等价于 /ui-open；打开游戏内 GUI 控制台窗口）";

        /// <summary>RegisterAll 反射调用：无参、返回 void。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = "help",
                Usage = "—— 列出全部指令",
                Group = "会话",
                Kind = "help",
                Handler = HelpHandler
            });

            SlashRegistry.Register(new SlashEntry
            {
                Verb = "usings",
                Usage = "—— 列出 usings 与 directives 两张表（标注 Default / 别名·静态）",
                Group = "会话",
                Kind = "usings",
                Handler = UsingsHandler
            });

            SlashRegistry.Register(new SlashEntry
            {
                Verb = UsingRemoveVerb,
                Usage = "<条目> [...] —— 删除条目（DefaultUsings 拒删并点名；两段式别名:/usings remove <条目> [...]）",
                Group = "会话",
                Kind = "usings-remove",
                Dangerous = true,
                Handler = UsingsRemoveHandler
            });

            // 两段式别名：/ui open ≡ /ui-open（口径与 /usings add 那条一致 —— 薄别名只做分流+反查+委托，
            // 自己不实现功能）。真机实测（S11-8-1）：用户工具清单里写的是 /ui open，而表里只有 /ui-open，
            // 于是 /ui open 被响亮拒绝成「未知指令: /ui」—— 补上这个别名。
            SlashRegistry.Register(new SlashEntry
            {
                Verb = UiAliasVerb,
                Usage = "open —— 打开游戏内 GUI 控制台窗口（幂等：已开着时也算 opened=true）",
                Group = "界面",
                Kind = UiCommand.Kind,     // 纯别名：只产出 ui-open 一种结果
                Dangerous = true,          // 与 ui-open 同：开窗是用户可见副作用
                Handler = UiAliasHandler
            });

            SlashRegistry.Register(new SlashEntry
            {
                Verb = "reset",
                Usage = "[keepUsings] —— 清空 submission 链；默认 usings 回 DefaultUsings，keepUsings=保留",
                Group = "会话",
                Kind = "reset",
                Dangerous = true,
                Handler = ResetHandler
            });

            SlashRegistry.Register(new SlashEntry
            {
                Verb = "quit",
                Usage = "—— 正常退出游戏",
                Group = "会话",
                Kind = "quit",
                Dangerous = true,
                Handler = QuitHandler
            });
        }

        // ================= handler（legacy 5 条的 Lines/Data 与旧 switch 逐字一致；别名只分流+委托）=================

        /// <summary>/help —— 表驱动 help（Lines 与 Data 同源：列出的就是当前登记的全部指令）。</summary>
        private static SlashResult HelpHandler(string rest, SlashContext ctx)
        {
            return SlashResults.Help();
        }

        /// <summary>
        /// /usings —— 无子动词列出两张表；add/remove 子动词**反查表项并委托**：
        /// add → usings-add（该指令的解析器传 skipFirstWord="add"，故这里传整串 "add " + itemsText）；
        /// remove → usings-remove（传整串，表项自己剥 "remove" 前缀）。
        /// 分隔符 = **任意空白**（<see cref="SlashRegistry.IndexOfWhitespace"/>，F1）；
        /// 不认识的子动词 ⇒ **用法行**，不再静默降级成列表（那会让 /usings add&lt;TAB&gt;X 得到
        /// 「usings 表(N 条)」—— 另一条指令的输出，与「我只想看列表」无法区分）。
        /// </summary>
        private static SlashResult UsingsHandler(string rest, SlashContext ctx)
        {
            string sub = (rest ?? "").Trim();
            if (sub.Length > 0)
            {
                string subVerb = sub, itemsText = "";
                int sp = SlashRegistry.IndexOfWhitespace(sub);
                if (sp >= 0)
                {
                    subVerb = sub.Substring(0, sp).Trim();
                    itemsText = sub.Substring(sp + 1).Trim();
                }

                if (subVerb.Equals("add", StringComparison.OrdinalIgnoreCase))
                {
                    SlashEntry entry;
                    if (SlashRegistry.TryGet(UsingAddCommand.Verb, out entry) && entry.Handler != null)
                        return entry.Handler("add " + itemsText, ctx);   // W10 集成建议的逐字形状
                }
                else if (subVerb.Equals("remove", StringComparison.OrdinalIgnoreCase))
                {
                    SlashEntry entry;
                    if (SlashRegistry.TryGet(UsingRemoveVerb, out entry) && entry.Handler != null)
                        return entry.Handler(sub, ctx);                  // 传整串（含 "remove"）
                    return RemoveUsings(itemsText, ctx);                 // 表被隔离（测试）时的兜底：同一内核
                }
                else
                {
                    return UsingsUsage(subVerb);
                }
            }
            return UsingsList(ctx);
        }

        /// <summary>两段式 /usings 的用法行（非空 badSubVerb 时先点名未知子命令）。</summary>
        private static SlashResult UsingsUsage(string badSubVerb)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(badSubVerb))
                lines.Add("未知子命令: " + badSubVerb + " —— /usings 只接受 add / remove（不带子命令时列出两张表）");
            lines.Add("/usings              列出 usings 与 directives 两张表");
            lines.Add("/usings add <条目> [...]     新增 using（等价于 /usings-add）");
            lines.Add("/usings remove <条目> [...]  删除条目（等价于 /usings-remove）");
            return SlashResults.Usage("usings", lines.ToArray());
        }

        /// <summary>/usings remove 的用法行（空参数与引号错误两条路共用；逐字与旧实现一致）。</summary>
        private const string RemoveUsageLine = "用法: /usings remove <条目> [...]（例: /usings remove System.IO）";

        /// <summary>
        /// /ui open —— 两段式别名：只做「子动词分流 + 反查表项 + 委托」，自己不实现开窗。
        /// 剥掉 "open" 后把**剩余参数原样**交给 ui-open 表项 ⇒ <c>/ui open &lt;args&gt;</c> 与
        /// <c>/ui-open &lt;args&gt;</c> 完全同形；结果 Kind 也是 ui-open（无壳层特判）。
        /// 空子动词或非 open 子动词 ⇒ 用法行（响亮、不静默、也不当成别的东西）。
        /// </summary>
        private static SlashResult UiAliasHandler(string rest, SlashContext ctx)
        {
            string sub = (rest ?? "").Trim();
            string subVerb = sub, subRest = "";
            int sp = SlashRegistry.IndexOfWhitespace(sub);   // 任意空白（F1）
            if (sp >= 0)
            {
                subVerb = sub.Substring(0, sp).Trim();
                subRest = sub.Substring(sp + 1).Trim();
            }

            if (subVerb.Length == 0 || !subVerb.Equals("open", StringComparison.OrdinalIgnoreCase))
                return UiUsage(sub.Length == 0 ? null : subVerb);

            SlashEntry entry;
            if (SlashRegistry.TryGet(UiCommand.Verb, out entry) && entry.Handler != null)
                return entry.Handler(subRest, ctx);      // 剩余参数原样（动词已剥离）
            return ctx.CallUiOpen(subRest);              // 表被隔离（测试）时的兜底：同一 provider
        }

        /// <summary>两段式 /ui 的用法行（非空 badSubVerb 时先点名未知子命令）。</summary>
        private static SlashResult UiUsage(string badSubVerb)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(badSubVerb))
                lines.Add("未知子命令: " + badSubVerb + " —— /ui 只接受 open");
            lines.Add(UiUsageLine);
            return SlashResults.Usage(UiCommand.Kind, lines.ToArray());
        }

        /// <summary>usings-remove 表项的 handler：rest 可带 "remove" 前缀（/usings remove X 走这里）。</summary>
        private static SlashResult UsingsRemoveHandler(string rest, SlashContext ctx)
        {
            return RemoveUsings(StripRemoveVerb(rest), ctx);
        }

        /// <summary>剥掉可选的 "remove" 前缀（大小写不敏感）；没有则原样返回。分隔符 = 任意空白（F1）。</summary>
        private static string StripRemoveVerb(string rest)
        {
            string s = (rest ?? "").Trim();
            if (s.Length == 0) return "";
            int sp = SlashRegistry.IndexOfWhitespace(s);
            string head = sp >= 0 ? s.Substring(0, sp) : s;
            if (!head.Equals("remove", StringComparison.OrdinalIgnoreCase)) return s;
            return sp >= 0 ? s.Substring(sp + 1).Trim() : "";
        }

        /// <summary>
        /// /usings remove &lt;条目&gt; [...] —— 删除内核。切分复用 W10 已 public 化的 ParseItems
        /// （引号分段；未加引号的空白切分与旧实现逐字等价）。
        /// **传 skipFirstWord=null**：remove 没有需要跳过的动词，"add" 就是普通条目
        /// （首词跳过是 usings-add 自己的规则，不在这里生效 —— 绝不静默吃掉一个 token）。
        /// 三条非成功路都有机器可读 Data（消费者可能只看 Data）：空参数 = {ok:false, usage:true, error}；
        /// 引号错误 = 同前且 error 为引号错误原文；Host 未接线 = {ok:false, error}（SlashResults.Error）。
        /// </summary>
        private static SlashResult RemoveUsings(string itemsText, SlashContext ctx)
        {
            var r = new SlashResult { Kind = "usings-remove" };
            string text = (itemsText ?? "").Trim();
            if (text.Length == 0)
            {
                // S11-4 复审边角 1：空参路也必须给机器可读的 Data —— 启用 --json 时壳层只渲染 Data，
                // 给裸 null 消费者就**看不到原因**（全指令扫描里此前唯一 Data==null 的一条）。
                // Lines 逐字不变（仍只有那一行用法）；ok/usage 与 /usings add 的用法页同口径，
                // error 带原因（Data 的消费者看不到 Lines，必须能从 Data 知道缺什么）。
                SlashResult usage = SlashResults.Usage("usings-remove", RemoveUsageLine);
                usage.Data["error"] = "缺少条目：/usings remove 至少要一个条目（例: /usings remove System.IO）";
                return usage;
            }

            string parseError;
            // skipFirstWord=null：remove 的条目就是条目（对比 UsingAddCommand 自己传 "add"）。
            List<string> items = UsingAddCommand.ParseItems(text, out parseError, null);
            if (!string.IsNullOrEmpty(parseError))
            {
                SlashResult usage = SlashResults.Usage("usings-remove", RemoveUsageLine, "错误: " + parseError);
                usage.Data["error"] = parseError;
                return usage;
            }

            SlashContext c = ctx ?? new SlashContext();
            if (c.Host == null)
                return SlashResults.Error("usings-remove",
                    "会话未接线：ctx.Host 为 null（ReplHost 未注入）—— 无法删除 using。");

            UsingRemoveReport rep = c.Host.RemoveUsings(items);
            foreach (string item in rep.Removed) r.Lines.Add("已移除: using " + item + ";");
            foreach (string item in rep.Denied) r.Lines.Add("拒绝删除（DefaultUsings 不可删）: " + item);
            foreach (string item in rep.NotFound) r.Lines.Add("未找到: " + item);
            if (rep.Removed.Count == 0 && rep.Denied.Count == 0 && rep.NotFound.Count == 0)
                r.Lines.Add("（无可删除条目）");
            r.Data = new Dictionary<string, object>
            {
                ["removed"] = rep.Removed,
                ["denied"] = rep.Denied,
                ["notFound"] = rep.NotFound
            };
            return r;
        }

        /// <summary>/usings（无子动词）—— 列出 usings 与 directives 两张表。</summary>
        private static SlashResult UsingsList(SlashContext ctx)
        {
            SlashContext c = ctx ?? new SlashContext();
            if (c.Host == null)
                return SlashResults.Error("usings",
                    "会话未接线：ctx.Host 为 null（ReplHost 未注入）—— 无法列出 usings。");

            UsingsSnapshot snap = c.Host.SnapshotUsings();
            var list = new SlashResult { Kind = "usings" };
            list.Lines.Add("usings 表(" + snap.Usings.Count + " 条):");
            foreach (string u in snap.Usings)
                list.Lines.Add("  using " + u + ";" + (snap.Defaults.Contains(u) ? "  （Default）" : ""));
            list.Lines.Add("directives 表(" + snap.Directives.Count + " 条):");
            foreach (string d in snap.Directives)
                list.Lines.Add("  using " + d + ";  （别名/静态）");
            list.Data = new Dictionary<string, object> { ["usings"] = snap.Usings, ["directives"] = snap.Directives };
            return list;
        }

        /// <summary>/reset [keepUsings] —— 与旧实现同路径：Executor 收口未完成请求 → 引擎重置 → ConsoleLog 经队列落账。</summary>
        private static SlashResult ResetHandler(string rest, SlashContext ctx)
        {
            bool keepUsings = (rest ?? "").Trim().Equals("keepUsings", StringComparison.OrdinalIgnoreCase);
            SlashContext c = ctx ?? new SlashContext();
            if (c.Host == null)
                return SlashResults.Error("reset",
                    "会话未接线：ctx.Host 为 null（ReplHost 未注入）—— 无法重置。");

            Executor executor = c.Executor;
            if (executor != null) executor.Reset();
            c.Host.Reset(keepUsings);
            if (executor != null)
                executor.Enqueue(() => { ConsoleLog.AbandonPending("reset"); return ReplResult.Success("reset"); }, 5000);

            var r = new SlashResult { Kind = "reset" };
            r.Lines.Add("会话已重置" + (keepUsings ? "（usings 保留）" : "（usings 回 DefaultUsings）"));
            r.Data = new Dictionary<string, object> { ["ok"] = true, ["keepUsings"] = keepUsings };
            return r;
        }

        /// <summary>/quit —— 壳层据 Kind="quit" 经 Executor 调 Root.Shutdown。</summary>
        private static SlashResult QuitHandler(string rest, SlashContext ctx)
        {
            var r = new SlashResult { Kind = "quit" };
            r.Data = new Dictionary<string, object> { ["ok"] = true, ["quit"] = true };
            return r;
        }
    }
}
