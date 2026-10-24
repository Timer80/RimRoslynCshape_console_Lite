// RoslynProbe —— legacy 5 条(/help /usings /usings remove /reset /quit)迁表后的最小回归(S11-2)。
//
// 为什么需要它:legacy 的 5 条原先在 SlashCommands.cs 的 switch 里,主仓 xunit 工程编不了 Source/Game
// (需 Verse),所以旧行为此前**没有任何自动化测试**。S11-2 迁表后至少钉住:
//   ① 5 条都登记进表且 kind/usage/危险位形状正确;
//   ② /help 列出**全部**指令(表驱动同源);
//   ③ /usings add X 经 legacy 入口仍走到 usings-add(真 ReplHost + 真 UsingAddCommand.Provide);
//   ④ /usings remove 经 legacy 入口与 usings-remove 表项(真 ReplHost;纯内存,无编译器依赖);
//   ⑤ /reset、/quit 的 Kind/Lines/Data 逐字不变(quit 的 Kind 是壳层行为开关);
//   ⑥ 兼容壳把 channel/IsGameThread/Host/Executor 事实位原样送进 ctx。
//
// 约定同 tests/SlashHarness/SlashRegistryTests.cs:不用 xunit(harness 工程零 PackageReference),
// public static int Run() 返回失败数,由 SlashTestHarness.RunAll() 反射发现。用例必须幂等(§8.4)。

using System;
using System.Collections.Generic;
using RoslynProbe;
using RoslynProbe.Repl;

namespace RoslynProbe.Tests
{
    public static class LegacyCommandsTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";
        private static int checks;

        /// <summary>注入的 UiOpen provider 收到的 rest（F1 的 /ui&lt;空白&gt;open 用例复用）。</summary>
        private static string uiSeenRest = "UNSET";

        public static int Run()
        {
            Failures.Clear();
            checks = 0;
            currentCase = "(none)";

            Case("legacy 5 条都已登记进表（verb/Kind/Usage/Dangerous 形状）", RegisterCases);
            Case("/help 列出全部指令（表驱动；Data 与 Lines 同源）", HelpCases);
            Case("/usings add 经 legacy 入口仍生效（真 ReplHost + 真 UsingAddCommand.Provide）", UsingsAddViaLegacyCase);
            Case("/usings remove 经 legacy 入口与 usings-remove 表项（真 ReplHost）", UsingsRemoveViaLegacyCase);
            Case("/usings 列表经 legacy 入口（真 ReplHost）", UsingsListCase);
            Case("两段式 /ui open ≡ /ui-open（薄别名：分流 + 反查 + 委托；非 open 给用法行）", UiAliasCases);
            Case("/reset 仍走老路径（Kind/Lines/Data 逐字）", ResetCases);
            Case("/quit 仍是那条路径（Kind=quit + Data）", QuitCases);
            Case("--json 边角：空参 remove 不再给裸 null；quit 在 GUI 也落 JSON 行", JsonEdgeCases);
            Case("F1 分隔空白：TAB/全角/NBSP 都能分流（两段式别名不再静默降级）", WhitespaceCases);
            Case("兼容壳把 channel/IsGameThread/Host/Executor 事实位送进 ctx", ChannelFactCases);
            Case("能力未注入时姿势 B 指令回 missing-capability（壳层不抛）", MissingCapabilityCase);

            // 收尾:还原真实表与模板,避免污染同进程其它测试(§8.4)。
            SlashCapabilities.ResetForTests();
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();

            Console.WriteLine("LegacyCommandsTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- 用例 ----------------

        private static void RegisterCases()
        {
            ResetRealTable();

            string[] verbs = { "help", "usings", "usings-remove", "reset", "quit" };
            foreach (string v in verbs)
            {
                SlashEntry e;
                Check(SlashRegistry.TryGet(v, out e), "legacy verb 应已登记: /" + v);
                if (e == null) continue;
                Check(e.Handler != null, "/" + v + " 的 Handler 不应为 null（迁表后必须可派发）");
                Check(e.Group == "会话", "/" + v + " 的 Group 应为 会话，实际 " + Show(e.Group));
            }

            SlashEntry h, u, ur, r, q;
            SlashRegistry.TryGet("help", out h);
            SlashRegistry.TryGet("usings", out u);
            SlashRegistry.TryGet("usings-remove", out ur);
            SlashRegistry.TryGet("reset", out r);
            SlashRegistry.TryGet("quit", out q);

            Check(h != null && h.Kind == "help", "help 的 Kind 应为 help");
            Check(u != null && u.Kind == "usings", "usings 的 Kind 应为 usings");
            Check(ur != null && ur.Kind == "usings-remove", "usings-remove 的 Kind 应为 usings-remove");
            Check(r != null && r.Kind == "reset", "reset 的 Kind 应为 reset");
            Check(q != null && q.Kind == "quit", "quit 的 Kind 应为 quit（壳层行为开关）");

            Check(h != null && h.Usage == "—— 列出全部指令", "help 的 Usage 应逐字，实际 " + Show(h == null ? null : h.Usage));
            Check(u != null && u.Usage == "—— 列出 usings 与 directives 两张表（标注 Default / 别名·静态）",
                "usings 的 Usage 应逐字，实际 " + Show(u == null ? null : u.Usage));
            Check(r != null && r.Usage == "[keepUsings] —— 清空 submission 链；默认 usings 回 DefaultUsings，keepUsings=保留",
                "reset 的 Usage 应逐字，实际 " + Show(r == null ? null : r.Usage));
            Check(q != null && q.Usage == "—— 正常退出游戏", "quit 的 Usage 应逐字，实际 " + Show(q == null ? null : q.Usage));

            string args, note;
            SlashRegistry.SplitUsage(ur == null ? "" : ur.Usage, out args, out note);
            // F5：参数段只放参数规格；两段式别名在说明段里点名（渲染行不再是 "/usings-remove remove …"）。
            Check(args == "<条目> [...]", "usings-remove 的 Usage 参数段，实际 " + Show(args));
            Check(args.IndexOf('/') < 0, "usings-remove 的 Usage 参数段不得含 '/'，实际 " + Show(args));
            Check(note == "删除条目（DefaultUsings 拒删并点名；两段式别名:/usings remove <条目> [...]）",
                "usings-remove 的 Usage 说明段，实际 " + Show(note));

            Check(h != null && !h.Dangerous, "help 只读，不应标 Dangerous");
            Check(u != null && !u.Dangerous, "usings 列出只读，不应标 Dangerous");
            Check(ur != null && ur.Dangerous && r != null && r.Dangerous && q != null && q.Dangerous,
                "usings-remove/reset/quit 有副作用，必须标 Dangerous");
        }

        private static void HelpCases()
        {
            ResetRealTable();

            SlashResult r;
            Check(SlashRegistry.TryDispatch("/help", new SlashContext { Channel = "test" }, out r), "/help 应被吞下");
            Check(r != null && r.Kind == "help", "help 的 Kind 应为 help");

            List<SlashEntry> snap = SlashRegistry.Snapshot();
            Check(r != null && r.Lines.Count == snap.Count + 2,
                "help 行数应为 表条数+2(首尾)，实际 " + (r == null ? -1 : r.Lines.Count) + " vs " + snap.Count);

            // §8.5:先断言扫到了全部条目(锚点存在),而不是在空集上断言恒真。
            int seen = 0;
            foreach (SlashEntry e in snap)
            {
                string args, note;
                SlashRegistry.SplitUsage(e.Usage, out args, out note);
                string label = "/" + e.Verb + (args.Length > 0 ? " " + args : "");
                bool found = false;
                if (r != null)
                {
                    foreach (string line in r.Lines)
                    {
                        if (!line.StartsWith("  " + label, StringComparison.Ordinal)) continue;
                        int at = 2 + label.Length;
                        if (line.Length == at || line[at] == ' ') { found = true; break; }
                    }
                }
                Check(found, "help 里应有 \"" + label + "\" 这一行（缺 = 指令没被列出）");
                if (found) seen++;
            }
            Check(seen == snap.Count, "help 应列出全部 " + snap.Count + " 条指令，实际 " + seen);
            Check(snap.Count > 0, "表非空（前置：RegisterAll 真的登记了东西）");

            Check(r != null && r.Data != null && (int)r.Data["count"] == snap.Count, "help 的 Data.count 应等于表条数");
            List<string> verbs = r == null ? null : r.Data["verbs"] as List<string>;
            Check(verbs != null && verbs.Count == snap.Count, "help 的 Data.verbs 条数应等于表条数");

            string[] legacy = { "/help", "/usings", "/usings-remove", "/reset", "/quit" };
            foreach (string v in legacy)
                Check(verbs != null && verbs.Contains(v), "help 的 verbs 应含 " + v + "（legacy 5 条仍在表上）");

            // F5：args 只放**参数规格**（不含 verb、不含前导 '/'）；也不得出现 "/verb verb …" 这种行。
            List<Dictionary<string, object>> cmds = r == null ? null : r.Data["commands"] as List<Dictionary<string, object>>;
            Check(cmds != null && cmds.Count == snap.Count, "help 的 Data.commands 条数应等于表条数");
            int argsChecked = 0;
            if (cmds != null)
                foreach (Dictionary<string, object> c in cmds)
                {
                    string verb = (string)c["verb"];                 // "/xxx"
                    string ap = (string)c["args"] ?? "";
                    Check(ap.IndexOf('/') < 0, verb + " 的 args 不得含 '/'，实际 " + Show(ap));
                    string bare = verb.Length > 0 ? verb.Substring(1) : verb;
                    Check(!ap.StartsWith(bare, StringComparison.Ordinal),
                        verb + " 的 args 不得以 verb 开头（/verb verb … 形态），实际 " + Show(ap));
                    argsChecked++;
                }
            Check(argsChecked == snap.Count && snap.Count > 0,
                "args 断言必须真的扫过全部条目（§8.5 锚点），实际 " + argsChecked + "/" + snap.Count);
            foreach (SlashEntry e in snap)
            {
                string badPrefix = "  /" + e.Verb + " " + e.Verb;
                bool has = false;
                if (r != null)
                    foreach (string line in r.Lines)
                        if (line.StartsWith(badPrefix, StringComparison.Ordinal)) { has = true; break; }
                Check(!has, "help 不得出现 /verb verb 形态的行: " + Show(badPrefix));
            }
        }

        private static void UsingsAddViaLegacyCase()
        {
            ResetRealTable();
            var host = new ReplHost();
            SlashCapabilities.Install(RealCapabilities());

            // 真路径(真 ReplHost + 真 UsingAddCommand.Provide):System 已在默认 usings 表里,
            // 故确定性地产出 added=0 / accepted>=1(不触发编译器校验,与本机程序集面无关)。
            SlashResult r;
            bool hit = SlashCommands.TryDispatch("/usings add System", host, null, out r,
                SlashCommands.ChannelPipe, false);
            Check(hit, "/usings add 应被吞下");
            Check(r != null && r.Kind == "usings-add",
                "应经 usings-add 表项产出 Kind=usings-add，实际 " + (r == null ? "<null>" : r.Kind));
            Check(r != null && r.Data != null && r.Data.ContainsKey("accepted") && (int)r.Data["accepted"] >= 1,
                "已存在的 System 应被接受(accepted>=1)");
            Check(r != null && r.Data != null && (int)r.Data["added"] == 0,
                "System 已在表里，added 应为 0（值来自真 host.AddUsings）");
            Check(r != null && r.Lines.Count > 0 && r.Lines[0].StartsWith("已追加 ", StringComparison.Ordinal),
                "首行应是追加汇总，实际 " + Show(r == null || r.Lines.Count == 0 ? null : r.Lines[0]));

            // 反向锚点:legacy 入口必须把整串 "add X" 交给 usings-add 的 provider(不自己剥前缀)——
            // 这正是 W10 集成建议的形状,也是 usings-add 解析时传 skipFirstWord="add" 的前提
            // (该跳过是本指令自己的动词规则,不是通用解析器的语义)。
            string seenRest = null;
            SlashCapabilities.Install(new SlashContext
            {
                UsingAdd = delegate (string a, SlashContext c) { seenRest = a; return SlashResults.Ok("usings-add"); }
            });
            SlashResult r2;
            Check(SlashCommands.TryDispatch("/usings add System.IO", host, null, out r2,
                SlashCommands.ChannelPipe, false), "/usings add <ns> 应被吞下");
            Check(seenRest == "add System.IO", "legacy /usings add 应传 \"add \" + itemsText，实际 " + Show(seenRest));
        }

        private static void UsingsRemoveViaLegacyCase()
        {
            ResetRealTable();
            var host = new ReplHost();

            // DefaultUsings 拒删并点名（逐字与旧实现一致）。
            SlashResult r;
            Check(SlashCommands.TryDispatch("/usings remove System", host, null, out r,
                SlashCommands.ChannelPipe, false), "/usings remove 应被吞下");
            Check(r != null && r.Kind == "usings-remove", "Kind 应为 usings-remove");
            Check(r != null && r.Lines.Count == 1 && r.Lines[0] == "拒绝删除（DefaultUsings 不可删）: System",
                "DefaultUsings 拒删行应逐字，实际 " + Show(r == null || r.Lines.Count == 0 ? null : r.Lines[0]));
            Check(r != null && r.Data != null
                && ((List<string>)r.Data["denied"]).Count == 1 && ((List<string>)r.Data["denied"])[0] == "System",
                "Data.denied 应点名 System");
            Check(r != null && r.Data != null
                && ((List<string>)r.Data["removed"]).Count == 0 && ((List<string>)r.Data["notFound"]).Count == 0,
                "removed/notFound 应为空");

            // /usings-remove 表项同一条内核（表项自己剥可选的 "remove" 前缀）。
            SlashResult r2;
            Check(SlashCommands.TryDispatch("/usings-remove System", host, null, out r2,
                SlashCommands.ChannelPipe, false), "/usings-remove 表项应被吞下");
            Check(r2 != null && r2.Kind == "usings-remove" && r2.Lines.Count == 1
                && r2.Lines[0] == "拒绝删除（DefaultUsings 不可删）: System",
                "usings-remove 表项应与 /usings remove 同路产出同一行");

            // 空参数：Lines 旧用法行逐字；Data 必须有机器可读的原因（S11-4 复审边角 1）。
            SlashResult r3;
            SlashCommands.TryDispatch("/usings remove", host, null, out r3, SlashCommands.ChannelPipe, false);
            Check(r3 != null && r3.Kind == "usings-remove" && r3.Lines.Count == 1
                && r3.Lines[0] == "用法: /usings remove <条目> [...]（例: /usings remove System.IO）",
                "空 remove 的用法行应逐字，实际 " + Show(r3 == null || r3.Lines.Count == 0 ? null : r3.Lines[0]));
            Check(r3 != null && r3.Data != null, "空 remove 的 Data 不得为 null（只看 Data 的消费者会看不到原因）");
            Check(r3 != null && r3.Data != null && (bool)r3.Data["ok"] == false && (bool)r3.Data["usage"] == true,
                "空 remove 的 Data 应为 ok:false/usage:true");
            Check(r3 != null && r3.Data != null && r3.Data["error"] != null
                && ((string)r3.Data["error"]).IndexOf("缺少条目", StringComparison.Ordinal) >= 0,
                "空 remove 的 Data.error 应带原因，实际 "
                + Show(r3 == null || r3.Data == null ? null : r3.Data["error"] as string));

            // 未找到行逐字。
            SlashResult r4;
            SlashCommands.TryDispatch("/usings remove NoSuch.NamespaceZzz", host, null, out r4,
                SlashCommands.ChannelPipe, false);
            Check(r4 != null && r4.Lines.Count == 1 && r4.Lines[0] == "未找到: NoSuch.NamespaceZzz",
                "未找到行应逐字，实际 " + Show(r4 == null || r4.Lines.Count == 0 ? null : r4.Lines[0]));

            // skipFirstWord 的修复（复审裁定 2）：remove 没有动词可跳，"add" 是普通条目 ⇒
            // 必须报「未找到: add」，绝不能被解析器静默吃掉一个 token。
            SlashResult r5;
            SlashCommands.TryDispatch("/usings remove add", host, null, out r5, SlashCommands.ChannelPipe, false);
            Check(r5 != null && r5.Lines.Count == 1 && r5.Lines[0] == "未找到: add",
                "remove 不得静默跳掉首词 add（应为 未找到: add），实际 "
                + Show(r5 == null || r5.Lines.Count == 0 ? null : r5.Lines[0]));
            Check(r5 != null && r5.Data != null && ((List<string>)r5.Data["notFound"]).Count == 1
                && ((List<string>)r5.Data["notFound"])[0] == "add",
                "Data.notFound 应点名 add（证明 token 没被吃掉）");

            // /usings remove add System.IO：两个条目都应参与（add 不再被当动词）。
            SlashResult r6;
            SlashCommands.TryDispatch("/usings remove add System.IO", host, null, out r6,
                SlashCommands.ChannelPipe, false);
            Check(r6 != null && r6.Data != null
                && ((List<string>)r6.Data["notFound"]).Count == 2
                && ((List<string>)r6.Data["notFound"])[0] == "add"
                && ((List<string>)r6.Data["notFound"])[1] == "System.IO",
                "两个条目都应参与（add 与 System.IO 都不在表里 ⇒ notFound 两条），实际 "
                + (r6 == null || r6.Data == null ? "<null>" : ((List<string>)r6.Data["notFound"]).Count.ToString()));
        }

        private static void UsingsListCase()
        {
            ResetRealTable();
            var host = new ReplHost();

            SlashResult r;
            Check(SlashCommands.TryDispatch("/usings", host, null, out r, SlashCommands.ChannelPipe, false),
                "/usings 应被吞下");
            Check(r != null && r.Kind == "usings", "Kind 应为 usings");
            Check(r != null && r.Lines.Count >= 2 && r.Lines[0].StartsWith("usings 表(", StringComparison.Ordinal),
                "首行应是 usings 表(N 条):，实际 " + Show(r == null || r.Lines.Count == 0 ? null : r.Lines[0]));
            Check(r != null && r.Data != null && r.Data["usings"] is List<string> && r.Data["directives"] is List<string>,
                "Data 应含 usings / directives 两个 List<string>");

            List<string> usings = r == null ? null : r.Data["usings"] as List<string>;
            Check(usings != null && usings.Contains("System"), "默认 usings 表应含 System（前置：真 host 真表）");

            // §8.5 锚点:默认条目的 （Default） 标注必须真的出现。
            bool defaultTagged = false;
            if (r != null)
                foreach (string line in r.Lines)
                    if (line == "  using System;  （Default）") { defaultTagged = true; break; }
            Check(defaultTagged, "System 那行应带 （Default） 标注");
        }

        /// <summary>
        /// S11-8-1 真机边角 A：工具清单里写的是两段式 <c>/ui open</c>，而表里只有 <c>/ui-open</c> ⇒
        /// 曾被响亮拒绝成「未知指令: /ui」。口径与 /usings add 那条一致：薄别名只做「子动词分流 +
        /// 反查表项 + 委托」，自己不实现功能；非 open 的子动词给用法行（不静默、也不当成别的东西）。
        /// </summary>
        private static void UiAliasCases()
        {
            ResetRealTable();

            // ---- 登记形状 ----
            SlashEntry e;
            Check(SlashRegistry.TryGet(SlashCommands.UiAliasVerb, out e), "两段式别名 verb /ui 应已登记");
            Check(e != null && e.Handler != null, "/ui 的 Handler 不应为 null");
            Check(e != null && e.Kind == UiCommand.Kind,
                "/ui 的结果 Kind 应与表内 verb 一致（" + UiCommand.Kind + "），实际 " + (e == null ? "<null>" : e.Kind));
            Check(e != null && e.Group == "界面", "/ui 的 Group 应为 界面");
            Check(e != null && e.Dangerous, "/ui open 会开窗，Dangerous 应为 true");
            string args, note;
            SlashRegistry.SplitUsage(e == null ? "" : e.Usage, out args, out note);
            Check(args == "open" && note == "打开游戏内 GUI 控制台窗口（幂等：已开着时也算 opened=true）",
                "/ui 的 Usage 应是「open —— …」，实际 " + Show(e == null ? null : e.Usage));

            var host = new ReplHost();

            // ---- 委托：剥掉 open，剩余参数原样交给 ui-open 表项（Kind 也是 ui-open）----
            string seenRest = "UNSET";
            SlashCapabilities.Install(new SlashContext
            {
                UiOpen = delegate (string a, SlashContext c) { seenRest = a; return SlashResults.Ok("ui-open"); }
            });

            SlashResult r;
            Check(SlashCommands.TryDispatch("/ui open", host, null, out r, SlashCommands.ChannelGui, true),
                "/ui open 应被吞下");
            Check(r != null && r.Kind == "ui-open", "结果 Kind 应为 ui-open，实际 " + (r == null ? "<null>" : r.Kind));
            Check(seenRest == "", "剥掉 open 后剩余参数为空串（/ui open ≡ /ui-open），实际 " + Show(seenRest));

            SlashResult r2;
            Check(SlashCommands.TryDispatch("/ui open extra", host, null, out r2, SlashCommands.ChannelGui, true),
                "/ui open extra 应被吞下");
            Check(seenRest == "extra", "剩余参数应原样交给 ui-open，实际 " + Show(seenRest));

            SlashResult r3;
            SlashCommands.TryDispatch("/ui OPEN x", host, null, out r3, SlashCommands.ChannelGui, true);
            Check(seenRest == "x", "/ui OPEN 的子动词判定应大小写不敏感，实际 " + Show(seenRest));

            // ---- 非 open 子动词：用法行（响亮、不静默、也不交给 provider）----
            seenRest = "UNSET";
            SlashResult bad;
            Check(SlashCommands.TryDispatch("/ui close", host, null, out bad, SlashCommands.ChannelGui, true),
                "/ui close 仍应被吞下（是指令，不放行给编译器）");
            Check(bad != null && bad.Kind == "ui-open", "未知子命令的 Kind 仍应是 ui-open（属这条指令）");
            Check(seenRest == "UNSET", "非 open 的子动词**不得**被交给 provider，实际 " + Show(seenRest));
            Check(bad != null && bad.Lines.Count == 2
                && bad.Lines[0].IndexOf("未知子命令: close", StringComparison.Ordinal) >= 0
                && bad.Lines[1].StartsWith("用法: /ui open", StringComparison.Ordinal),
                "应给「未知子命令 + 用法行」两行，实际 "
                + Show(bad == null || bad.Lines.Count == 0 ? null : bad.Lines[0]));
            Check(bad != null && bad.Data != null && (bool)bad.Data["ok"] == false && (bool)bad.Data["usage"] == true,
                "用法行应给机器可读 Data（ok:false/usage:true）");

            // ---- 空子动词：同样给用法行（不静默）----
            SlashResult bare;
            Check(SlashCommands.TryDispatch("/ui", host, null, out bare, SlashCommands.ChannelGui, true),
                "裸 /ui 应被吞下");
            Check(bare != null && bare.Lines.Count == 1 && bare.Lines[0].StartsWith("用法: /ui open", StringComparison.Ordinal),
                "裸 /ui 应只给一行用法，实际 "
                + Show(bare == null || bare.Lines.Count == 0 ? null : bare.Lines[0]));

            // ---- 表内 verb 两条路都不回归 ----
            string seen2 = "UNSET";
            SlashCapabilities.Install(new SlashContext
            {
                UiOpen = delegate (string a, SlashContext c) { seen2 = a; return SlashResults.Ok("ui-open"); }
            });
            SlashResult r4;
            Check(SlashCommands.TryDispatch("/ui-open", host, null, out r4, SlashCommands.ChannelGui, true),
                "/ui-open 仍应可用（不回归）");
            Check(seen2 == "", "/ui-open 仍把空 rest 交给 provider，实际 " + Show(seen2));
            SlashCommands.TryDispatch("/ui-open open", host, null, out r4, SlashCommands.ChannelGui, true);
            Check(seen2 == "open", "/ui-open open 仍原样把 \"open\" 交给 provider，实际 " + Show(seen2));

            // ---- /ui 也在 help 里（表驱动同源；两段式形态给人看）----
            SlashResult help;
            SlashRegistry.TryDispatch("/help", new SlashContext(), out help);
            bool listed = false;
            if (help != null)
                foreach (string line in help.Lines)
                    if (line.StartsWith("  /ui open", StringComparison.Ordinal)) { listed = true; break; }
            Check(listed, "help 里应有「  /ui open」这一行");
        }

        private static void ResetCases()
        {
            ResetRealTable();
            var host = new ReplHost();

            SlashResult r;
            Check(SlashCommands.TryDispatch("/reset", host, null, out r, SlashCommands.ChannelGui, true),
                "/reset 应被吞下");
            Check(r != null && r.Kind == "reset", "Kind 应为 reset");
            Check(r != null && r.Lines.Count == 1 && r.Lines[0] == "会话已重置（usings 回 DefaultUsings）",
                "默认 reset 行应逐字，实际 " + Show(r == null || r.Lines.Count == 0 ? null : r.Lines[0]));
            Check(r != null && r.Data != null && (bool)r.Data["ok"] == true && (bool)r.Data["keepUsings"] == false,
                "默认 reset 的 Data 应为 ok=true/keepUsings=false");

            SlashResult r2;
            SlashCommands.TryDispatch("/reset keepUsings", host, null, out r2, SlashCommands.ChannelGui, true);
            Check(r2 != null && r2.Kind == "reset" && r2.Lines.Count == 1 && r2.Lines[0] == "会话已重置（usings 保留）",
                "keepUsings 行应逐字，实际 " + Show(r2 == null || r2.Lines.Count == 0 ? null : r2.Lines[0]));
            Check(r2 != null && r2.Data != null && (bool)r2.Data["keepUsings"] == true, "keepUsings 位应为 true");

            SlashResult r3;
            SlashCommands.TryDispatch("/reset KEEPUSINGS", host, null, out r3, SlashCommands.ChannelGui, true);
            Check(r3 != null && (bool)r3.Data["keepUsings"] == true, "keepUsings 判定应大小写不敏感（与旧实现一致）");
        }

        private static void QuitCases()
        {
            ResetRealTable();

            SlashResult r;
            Check(SlashCommands.TryDispatch("/quit", new ReplHost(), null, out r, SlashCommands.ChannelGui, true),
                "/quit 应被吞下");
            Check(r != null && r.Kind == "quit", "Kind 必须是 quit（壳层据此经 Executor 调 Root.Shutdown）");
            Check(r != null && r.Lines.Count == 0, "quit 不应有行（壳层只认 Kind）");
            Check(r != null && r.Data != null && (bool)r.Data["ok"] == true && (bool)r.Data["quit"] == true,
                "quit 的 Data 应为 ok=true/quit=true");
        }

        /// <summary>
        /// S11-4 复审报的两条「机器可读出口不一致」边角：
        /// ① 空参 remove 经壳层 --json 不能给字面 null（消费者只看 Data，必须能看到原因）；
        /// ② quit 在 GUI 的落账序列必须含该指令自己的行（带 --json 时就是那行 JSON），bye 在最后 ——
        ///    与 stdio/pipe 由 SessionLine 追加 "&gt; bye" 的口径一致。
        /// 三者都经**壳层真实走的那条路**（SlashJson.TryDispatch / ConsoleWindow.QuitNoteLines）。
        /// </summary>
        private static void JsonEdgeCases()
        {
            ResetRealTable();
            var host = new ReplHost();

            // ---- ① 空参 remove + --json ----
            SlashResult rj;
            Check(SlashJson.TryDispatch("/usings remove --json", host, null,
                SlashCommands.ChannelPipe, false, out rj), "--json 空参 remove 应被吞下");
            Check(rj != null && rj.Kind == "usings-remove", "Kind 应为 usings-remove");
            Check(rj != null && rj.Data != null, "--json 空参 remove 的 Data 不得为 null（消费者只看 Data）");
            Check(rj != null && rj.Data != null && (bool)rj.Data["ok"] == false && (bool)rj.Data["usage"] == true,
                "--json 空参 remove 的 Data 应为 ok:false/usage:true");
            Check(rj != null && rj.Data != null && rj.Data["error"] != null
                && ((string)rj.Data["error"]).IndexOf("缺少条目", StringComparison.Ordinal) >= 0,
                "Data.error 应带原因（缺少条目）");
            Check(rj != null && rj.Lines.Count == 1 && rj.Lines[0] != "null"
                && rj.Lines[0].StartsWith("{", StringComparison.Ordinal),
                "--json 渲染行应是 JSON 对象而不是字面 null，实际 "
                + Show(rj == null || rj.Lines.Count == 0 ? null : rj.Lines[0]));
            Check(rj != null && rj.Lines.Count == 1
                && rj.Lines[0].IndexOf("缺少条目", StringComparison.Ordinal) >= 0,
                "--json 渲染行应含原因文本，实际 "
                + Show(rj == null || rj.Lines.Count == 0 ? null : rj.Lines[0]));

            // ---- ② quit + --json：GUI 的落账序列 ----
            SlashResult qj;
            Check(SlashJson.TryDispatch("/quit --json", host, null,
                SlashCommands.ChannelGui, true, out qj), "--json quit 应被吞下");
            Check(qj != null && qj.Kind == "quit", "Kind 应为 quit");
            Check(qj != null && qj.Data != null, "quit 的 Data 应非 null（--json 才有东西可渲染）");
            Check(qj != null && qj.Lines.Count == 1, "quit --json 应把 Lines 换成唯一一行 JSON，实际 "
                + (qj == null ? -1 : qj.Lines.Count));

            List<string> notes = ConsoleWindow.QuitNoteLines(qj);
            Check(notes.Count == 2, "quit 的落账序列应是 [指令行, bye]，实际 " + notes.Count + " 行");
            Check(notes.Count == 2 && notes[1] == "bye", "最后一行必须是 bye");
            Check(notes.Count == 2 && qj != null && notes[0] == qj.Lines[0],
                "第一行必须是该指令自己的行（旧实现把 --json 的 JSON 行丢了），实际 " + Show(notes.Count == 0 ? null : notes[0]));
            Check(notes.Count == 2 && notes[0].StartsWith("{", StringComparison.Ordinal),
                "该行应是 JSON 对象，实际 " + Show(notes.Count == 0 ? null : notes[0]));

            // ---- 不带 --json：quit 无行 ⇒ 落账序列就是旧的单行 bye（行为逐字不变）----
            SlashResult q;
            Check(SlashCommands.TryDispatch("/quit", host, null, out q, SlashCommands.ChannelGui, true),
                "/quit 应被吞下");
            List<string> plain = ConsoleWindow.QuitNoteLines(q);
            Check(plain.Count == 1 && plain[0] == "bye",
                "不带 --json 的 quit 落账仍只有一行 bye（逐字不变），实际 " + plain.Count + " 行");

            // ---- 参数 null 也不得抛（壳层可能拿到 null 结果）----
            List<string> none = ConsoleWindow.QuitNoteLines(null);
            Check(none.Count == 1 && none[0] == "bye", "QuitNoteLines(null) 应只给 bye，不抛");
        }

        /// <summary>
        /// F1：两段式别名的分隔符口径 = **任意空白**（TAB / 全角空格 U+3000 / NBSP U+00A0）。
        /// 旧口径只认 ASCII 空格 ⇒ <c>/usings add&lt;TAB&gt;X</c> 静默降级成 /usings 的**列表输出**
        /// （用户请求"加一条 using"，拿到的是另一条指令的结果），而 <c>/ui&lt;TAB&gt;open</c> 被响亮拒绝
        /// —— 同一类空白两种行为。三条路各跑三种空白。
        /// </summary>
        private static void WhitespaceCases()
        {
            ResetRealTable();
            var host = new ReplHost();
            SlashContext caps = RealCapabilities();                       // 真 UsingAdd/…，保证 add 路径真落盘
            caps.UiOpen = delegate (string a, SlashContext c) { uiSeenRest = a; return SlashResults.Ok("ui-open"); };
            SlashCapabilities.Install(caps);

            string[] blanks = { "\t", "\u3000", "\u00A0" };
            string[] names = { "TAB", "全角空格(U+3000)", "NBSP(U+00A0)" };

            for (int i = 0; i < blanks.Length; i++)
            {
                string b = blanks[i], n = names[i];

                // ① /usings add<空白>System ⇒ 必须走 add 路径（真 provider：System 已在表里 ⇒ accepted>=1）
                SlashResult r;
                Check(SlashCommands.TryDispatch("/usings add" + b + "System", host, null, out r,
                    SlashCommands.ChannelPipe, false), n + "：/usings add 应被吞下");
                Check(r != null && r.Kind == "usings-add",
                    n + "：/usings add 必须走 add 路径（不得静默降级成列表），实际 "
                    + (r == null ? "<null>" : r.Kind));
                Check(r != null && r.Data != null && r.Data.ContainsKey("accepted") && (int)r.Data["accepted"] >= 1,
                    n + "：add 路径应到达真 provider（accepted>=1），实际 "
                    + (r == null || r.Data == null ? "<null>"
                        : (r.Data.ContainsKey("accepted") ? r.Data["accepted"].ToString() : "无 accepted 键")));

                // ② /usings remove<空白>System ⇒ 必须走 remove 路径（DefaultUsings 拒删并点名）
                SlashResult r2;
                SlashCommands.TryDispatch("/usings remove" + b + "System", host, null, out r2,
                    SlashCommands.ChannelPipe, false);
                Check(r2 != null && r2.Kind == "usings-remove" && r2.Lines.Count == 1
                    && r2.Lines[0] == "拒绝删除（DefaultUsings 不可删）: System",
                    n + "：/usings remove 必须走 remove 路径，实际 "
                    + (r2 == null ? "<null>" : r2.Kind + " / "
                        + (r2.Lines.Count == 0 ? "<无行>" : r2.Lines[0])));

                // ③ /ui<空白>open ⇒ 必须走别名委托（旧行为是 kind=unknown 的响亮拒绝）
                uiSeenRest = "UNSET";
                SlashResult r3;
                Check(SlashCommands.TryDispatch("/ui" + b + "open", host, null, out r3,
                    SlashCommands.ChannelGui, true), n + "：/ui<空白>open 应被吞下");
                Check(r3 != null && r3.Kind == "ui-open",
                    n + "：/ui<空白>open 必须走别名，实际 " + (r3 == null ? "<null>" : r3.Kind));
                Check(uiSeenRest == "", n + "：别名应剥掉 open 后把空剩余交给 provider，实际 " + Show(uiSeenRest));
            }

            // 未知子动词：一律用法行（不再静默降级成列表 —— 那会与「我只想看列表」不可区分）
            SlashResult bad;
            SlashCommands.TryDispatch("/usings bogus", host, null, out bad, SlashCommands.ChannelPipe, false);
            Check(bad != null && bad.Data != null && (bool)bad.Data["usage"] == true,
                "未知子动词应给用法行（usage=true），实际 " + (bad == null ? "<null>" : Show(bad.Kind)));
            Check(bad != null && bad.Lines.Count > 0
                && bad.Lines[0].IndexOf("未知子命令: bogus", StringComparison.Ordinal) >= 0,
                "用法行应先点名未知子命令，实际 " + Show(bad == null || bad.Lines.Count == 0 ? null : bad.Lines[0]));
            bool listed = false;
            if (bad != null)
                foreach (string l in bad.Lines)
                    if (l.StartsWith("usings 表(", StringComparison.Ordinal)) { listed = true; break; }
            Check(!listed, "未知子动词不得给出列表输出（那会把误拼的 add 变成另一条指令的结果）");

            // 同一条指令的 ASCII 空格路径不回归
            SlashResult ok;
            SlashCommands.TryDispatch("/usings add System", host, null, out ok, SlashCommands.ChannelPipe, false);
            Check(ok != null && ok.Kind == "usings-add", "ASCII 空格路径不得回归，实际 "
                + (ok == null ? "<null>" : ok.Kind));
        }

        private static void ChannelFactCases()
        {
            SlashRegistry.ResetForTests();
            SlashCapabilities.ResetForTests();

            string seenChannel = null;
            bool seenThread = false;
            ReplHost seenHost = null;
            Executor seenExec = null;
            SlashRegistry.Register(new SlashEntry
            {
                Verb = "t-fact",
                Kind = "t-fact",
                Handler = delegate (string a, SlashContext c)
                {
                    seenChannel = c.Channel;
                    seenThread = c.IsGameThread;
                    seenHost = c.Host;
                    seenExec = c.Executor;
                    return SlashResults.Ok("t-fact");
                }
            });

            var host = new ReplHost();
            var exec = new Executor();
            SlashResult r;
            Check(SlashCommands.TryDispatch("/t-fact x", host, exec, out r, SlashCommands.ChannelPipe, true),
                "应被吞下");
            Check(seenChannel == SlashCommands.ChannelPipe,
                "ctx.Channel 应等于壳层给的 pipe，实际 " + Show(seenChannel));
            Check(seenThread, "ctx.IsGameThread 应等于壳层给的 true");
            Check(object.ReferenceEquals(seenHost, host) && object.ReferenceEquals(seenExec, exec),
                "ctx 应带上壳层给的 Host/Executor");
            Check(r != null && r.Kind == "t-fact", "结果应来自该 handler");

            // 默认值:不传通道/事实位时,通道为 null、线程为 false（拿不准就 false 的口径）。
            SlashResult r2;
            SlashCommands.TryDispatch("/t-fact", host, null, out r2);
            Check(seenChannel == null, "未给通道时应为 null，实际 " + Show(seenChannel));
            Check(!seenThread, "未给事实位时应为 false（宁可多一次 marshal）");

            // IsCommand 与通道无关（同一个判定）。
            Check(SlashCommands.IsCommand("  /x", SlashCommands.ChannelGui)
                && !SlashCommands.IsCommand("x", SlashCommands.ChannelGui), "IsCommand 判定不受通道影响");
        }

        private static void MissingCapabilityCase()
        {
            ResetRealTable();
            SlashCapabilities.ResetForTests();   // 等价于 Bootstrap 未跑:模板未注入

            SlashResult r;
            Check(SlashCommands.TryDispatch("/dev list", new ReplHost(), null, out r,
                SlashCommands.ChannelPipe, false), "姿势 B 指令仍应被吞下");
            Check(r != null && r.Kind == "missing-capability",
                "未注入能力时应回 missing-capability，实际 " + (r == null ? "<null>" : r.Kind));
            Check(r != null && r.Data != null && (string)r.Data["capability"] == "dev", "应点名 dev");
        }

        // ---------------- 助手 ----------------

        /// <summary>真实表：清空后按 RegisterAll 重新登记（含 legacy 5 条）。</summary>
        private static void ResetRealTable()
        {
            SlashCapabilities.ResetForTests();
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();
        }

        /// <summary>与 Bootstrap 注入逐字一致的能力模板（测试里替代 Bootstrap）。</summary>
        private static SlashContext RealCapabilities()
        {
            var c = new SlashContext();
            c.Status = StatusCommand.Provide;
            c.Log = LogCommand.Provide;
            c.Dev = DevCommand.Provide;
            c.DebugAction = DebugActionCommand.Provide;
            c.Hook = HookCommand.Provide;
            c.Prof = ProfCommand.Provide;
            c.UsingAdd = UsingAddCommand.Provide;
            c.UiOpen = UiCommand.Provide;
            return c;
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

        private static string Show(string s) { return s == null ? "<null>" : "\"" + s + "\""; }
    }
}
