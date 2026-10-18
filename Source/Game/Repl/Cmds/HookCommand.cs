// RoslynProbe —— /hook：Harmony 补丁的全进程枚举 / 单方法查询 / 按 owner 卸。
//
// 规格来源：TEXT38\TOOLSTACK-TO-SLASH-1.7.md 的 /hook 行 + §3 全文（Harmony 热补丁）。
//
// 子命令（rest = "/hook" 之后的整串，已 Trim；内核半与 provider 半共用同一个 TryParse，杜绝语法漂移）：
//   ""              用法（不碰游戏侧）
//   "list [n]"      全进程被补目标 + owner + 各类型补丁数（n = hook 条数上限，默认 200，最大 10000）
//   "check <目标>"  谁挂了、几层、优先级；该方法是否被 MethodBodyUpdater(ReJIT) 动过
//   "off <owner>"   按 owner 精确卸（UnpatchAll）+ 明示“补丁卸了，但动态程序集不可卸载”
//
// 目标语法（check）：
//   [<程序集名（支持 * ? 通配）>!]<命名空间.类型>[<::|:> 或最后一个 '.' <成员>][(<参数类型短名>,...)]
//   例：Verse.DebugWindowsOpener::DrawButtons    RimWorld.Pawn.Kill
//       System.String.Trim                     System.String::Trim(System.Char)
//       Assembly-CSharp!RimWorld.Pawn
//   类型级目标（不带成员）= 只列该类型下**已被补过**的方法（不是它全部方法，避免刷屏）。
//   构造函数写成 <类型>::ctor / <类型>::.ctor（静态构造 cctor）。
//
// Data（小驼峰、稳定；工具消费方依赖）：
//   list  ：{ok, count(被补目标数), patchCount(补丁条目总数), hookCount, returnedHookCount, truncated,
//            limit, ownerCount, harmonyVersion,
//            hooks:[{target, owner, patchCount, priority, priorities, prefixes, postfixes, transpilers,
//                    finalizers, innerPrefixes, innerPostfixes, innerFinalizers, patchMethods}],
//            owners:[{owner, patchCount, targetCount}], types:[{type, patchCount, targetCount}]}
//   check ：{ok, target, typeOnly, matchCount, patched, layers, rejitTouchedAny, spec:{assembly, type,
//            member, parameters}, notes, methods:[{method, type, assembly, metadataToken, patched, layers,
//            prefixes, postfixes, transpilers, finalizers, innerPrefixes, innerPostfixes, innerFinalizers,
//            patches:[{owner, kind, priority, index, patchMethod}], conflict,
//            rejit:{available, supported, metadataUpdateVersion, updated, updatedMethodTokenCount,
//                   module, typeAssembly, reason, ...}}]}
//   off   ：{ok, owner, unpatchAll(是否真的调了 UnpatchAll), removed, remaining, patchCountBefore,
//            patchCountAfter, affectedTargets, sharedOwner, warnings, note, harmonyVersion}
//   失败  ：{ok:false, error, …}（解析失败附 target/owner；目标解析不到附 candidates）
//
// ---- 版本面（S11-7 之后：**编译面已经就是运行面 = 3.0.0**）----
//   * 编译面 = Source/RoslynProbe.csproj:20 与 tests/SlashHarness/SlashHarness.csproj:61 引用的
//     workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll = **3.0.0.0**（§3 那份真身；
//     外部核实：AssemblyVersion/ProductVersion 3.0.0.0-rc.3+3c0e32cd…，size 2532864，SHA256 C8EF7856…F77CB0）。
//   * 仓库外的历史事实（**不再参与编译，别再由它推真身** —— 舰队规则 §10.1 第一类版本号陷阱）：
//     Mods\2009463077\Current\Assemblies\0Harmony.dll = 2.4.1.0（size 2456064，SHA256 353DAAFE…）。
//     S11-7（commit 9cffbba）之前的老 HintPath 指的就是这份影子副本。
//   * 两个版本**整体不是同一个 API 面**（仅本机上下文就差 methodCount 10281 vs 10288，patch 流程还多一处
//     7 参 ctor 调用等；差得不多，但“两版同一”这个前提本身不成立）。这里只说**本文件依赖的那些成员**：
//       - Harmony.GetAllPatchedMethods() / Harmony.GetPatchInfo(MethodBase)：两版都公共静态、签名一致；
//       - Patch.{owner, priority, index, PatchMethod}：两版一致；
//       - Patches 的集合：**2.4.1 = 6 个 / 3.0.0 = 7 个**（3.0.0 多一个 **InnerFinalizers**）。
//     ⇒ 本文依赖的成员不会 MissingMethod/MissingField；唯一会漏事实的是第 7 个集合，故用反射补它
//       （ReadOptionalCollection + CollectOptional）：真身 3.0.0 下该集合存在 ⇒ 直接计数；万一 HintPath 又
//       被指回 2.4.1，该成员不存在 → 跳过、不抛，绝不会因此把方法误报成“未打补丁”。
//
// ---- 实做时核到的硬事实（T-S11 与调用方都依赖；逐条有据）----
// 1) UnpatchAll(string harmonyID = null) 在**两个版本上都**把 null 当“全进程所有 owner”：
//      2.4.1：bool IDCheck(Patch p) { if (harmonyID != null) return p.owner == harmonyID; return true; }
//             （实例自己的 ID 字段在 UnpatchAll 里根本没被读）
//      3.0.0：if (patchInfo.Owners.Count > 0) Unpatch(item, HarmonyPatchType.All, harmonyID ?? "*")
//             （null → 只看“有没有 owner”，再按 "*" 通配 owner 卸掉全部）
//    ⇒ new Harmony(owner).UnpatchAll()（不传参）会卸**全进程所有 owner** 的补丁。本指令一律调
//      UnpatchAll(owner)：简报字面写 UnpatchAll()，但“按 owner 精确卸”才是它的意图，取实现该意图的调用。
// 2) 枚举面（两版本一致）：Harmony.GetAllPatchedMethods()（静态、IEnumerable<MethodBase>）、
//    Harmony.GetPatchInfo(MethodBase)（静态、返回 Patches、未补返回 null）、
//    Patches.{Prefixes,Postfixes,Transpilers,Finalizers,InnerPrefixes,InnerPostfixes}（ReadOnlyCollection<Patch>；
//    3.0.0 另加 InnerFinalizers，见上）、Patch.{owner, priority, index, PatchMethod}。
//    Patch.PatchMethod 的 getter 会按 moduleGUID 反查，反查失败会抛 ⇒ 逐条兜住（取不到写“<取不到: …>”，不让整条指令倒）。
//    §3 的 owner 作用域坑（new Harmony(owner).GetPatchedMethods() 返回 0）因此不需要复验：本文件从不调它。
// 3) ReJIT 判定没有“这个方法被 ReJIT 过吗”的公开 API。引擎自己的做法（Waylith 源码
//    ProfilerInstrumentation.ReconcileMetadataUpdates）是：
//        int[] tokens = MethodBodyUpdater.GetUpdatedMethodTokens(module);
//        foreach (token) module.ResolveMethod(token)   → 被热更新/ReJIT 换过方法体的方法
//    本指令照抄：取目标方法的 Module，MetadataToken 出现在返回的 token 集合里即“动过”。
//    WaylithClr.* 定义在 Waylith 运行时的 System.Private.CoreLib 里（普通 CoreCLR 下取不到）
//    ⇒ 全反射 + 取不到就写 reason（绝不抛）。
// 4) 线程契约（README §3.4）：/hook 只碰 0Harmony 的静态表、反射与 MethodBase，**没有任何 Unity/Verse 状态**
//    ⇒ 三个通道一律 inline，不 marshal。理由：GUI 壳本来就在游戏线程调，若这里 Enqueue 再等 Tick 会自死锁；
//    Harmony 的表读写与 unpatch 都是运行时级操作，不依赖游戏线程。
// 5) Bootstrap 用单一 owner "roslynprobe.toolbar" + PatchAll 装调试工具栏按钮补丁（DebugToolbarPatch）。
//    本指令不拆分、不改 Bootstrap（共享文件），但 /hook off 命中这个 owner 时**响亮警告**会连带卸掉工具栏按钮；
//    警告同时进 Data[sharedOwner]/Data[warnings]。是否拆 owner 由控制者定（见任务报告）。
//
// 姿势 B（README §4）：内核半 Handle 解析语法 + 给用法/语法错误（不碰游戏侧）；
// 合法请求原样交给 ctx.CallHook(rest)。provider 本体 Provide 在本文件（契约 v1.1 的方法名），Bootstrap 注入
//     ctx.Hook = HookCommand.Provide;
// 本文件全程不 typeof 游戏类型（目标按字符串反射解析）⇒ 校验工程可编、无游戏环境也能单测。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RoslynProbe
{
    /// <summary>
    /// /hook —— Harmony 补丁的全进程枚举（list）/ 单方法查询（check，含 ReJIT 判定）/ 按 owner 卸（off）。
    /// </summary>
    public static class HookCommand
    {
        /// <summary>指令 verb 与 SlashResult.Kind（小写无空格）。</summary>
        public const string Kind = "hook";

        /// <summary>list 默认返回的 hook 条数上限（全量仍在 Data[hookCount]，只是不打全部行）。</summary>
        private const int DefaultListLimit = 200;

        /// <summary>list 上限的硬顶（Data 是给 agent 消费的，别让它变成几 MB 的 JSON）。</summary>
        private const int MaxListLimit = 10000;

        /// <summary>check 命中的方法数上限（重载爆炸时截断并写 truncated 到 notes）。</summary>
        private const int MaxMethodMatches = 50;

        /// <summary>
        /// RoslynProbe 工具栏按钮的 owner（Bootstrap <c>new Harmony("roslynprobe.toolbar")</c> + PatchAll）。
        /// /hook off 命中它 = 连带卸掉调试工具栏按钮补丁，故给响亮警告。
        /// 与 Bootstrap 里的字面量必须一致（Bootstrap.cs 的 "roslynprobe.toolbar"）。
        /// </summary>
        internal const string SharedOwner = "roslynprobe.toolbar";

        private const string UsageText =
            "list [n]|check <目标>|off <owner> —— Harmony 补丁：全进程枚举 / 单方法查询（含 ReJIT 判定）/ 按 owner 精确卸";

        /// <summary>RegisterAll 反射调用（public static void Register()：无参、返回 void）。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = Kind,
                Usage = UsageText,
                Group = "调试",
                Kind = Kind,
                Dangerous = true,          // off 有副作用（卸补丁）
                Handler = Handle
            });
        }

        // ==================== 内核半（零 Verse、零 Harmony） ====================

        /// <summary>
        /// 指令 handler：只做语法解析（TryParse）与用法 / 语法错误（都不需要游戏侧），
        /// 合法请求原样转给 ctx.CallHook(rest)（未注册 → 内核自动回“能力未注册”行，不抛）。
        /// </summary>
        private static SlashResult Handle(string rest, SlashContext ctx)
        {
            Request request;
            SlashResult early;
            if (!TryParse(rest, out request, out early)) return early;
            return ctx.CallHook(rest);
        }

        /// <summary>
        /// 语法解析（内核半与 provider 半共用）。返回 false 时 error 是「用法页」或「语法错误」结果。
        /// </summary>
        internal static bool TryParse(string rest, out Request request, out SlashResult error)
        {
            request = null;
            error = null;
            string text = (rest ?? "").Trim();
            if (text.Length == 0)
            {
                error = Usage("缺少参数：本指令是 /hook list [n]、/hook check <目标> 或 /hook off <owner>。");
                return false;
            }

            string head = text;
            string tail = "";
            int sp = IndexOfWhitespace(text);
            if (sp >= 0)
            {
                head = text.Substring(0, sp);
                tail = text.Substring(sp + 1).Trim();
            }

            if (string.Equals(head, "list", StringComparison.OrdinalIgnoreCase))
            {
                int limit = DefaultListLimit;
                if (tail.Length > 0)
                {
                    int parsed;
                    if (!int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed <= 0)
                    {
                        error = SlashResults.Error(Kind,
                            "list 的参数只能是正整数上限（收到 “" + tail + "”）；例：/hook list 50。");
                        return false;
                    }
                    limit = Math.Min(parsed, MaxListLimit);
                }
                request = new Request { Mode = "list", Limit = limit };
                return true;
            }

            if (string.Equals(head, "check", StringComparison.OrdinalIgnoreCase))
            {
                if (tail.Length == 0)
                {
                    error = Usage("check 缺目标：需要 「/hook check <目标>」，目标形如 Verse.DebugWindowsOpener::DrawButtons。");
                    return false;
                }
                request = new Request { Mode = "check", Target = tail };
                return true;
            }

            if (string.Equals(head, "off", StringComparison.OrdinalIgnoreCase))
            {
                if (tail.Length == 0)
                {
                    error = Usage("off 缺 owner：需要 「/hook off <owner>」，owner 是单个词（如 roslynprobe.topic）。");
                    return false;
                }
                if (IndexOfWhitespace(tail) >= 0)
                {
                    error = SlashResults.Error(Kind,
                        "owner 不能含空白（收到 “" + tail + "”）；owner 是单个词，例：/hook off roslynprobe.leak。");
                    return false;
                }
                request = new Request { Mode = "off", Owner = tail };
                return true;
            }

            error = SlashResults.Error(Kind,
                "未知子命令 “" + head + "”；本指令是 /hook list [n]、/hook check <目标> 或 /hook off <owner>。");
            return false;
        }

        private static SlashResult Usage(string reason)
        {
            return SlashResults.Usage(Kind,
                reason,
                "用法: /hook list [n]        —— 全进程被补目标 + owner + 各类型补丁数（n 默认 200）",
                "      /hook check <目标>    —— 谁挂了、几层、优先级；是否被 MethodBodyUpdater(ReJIT) 动过",
                "      /hook off <owner>     —— 按 owner 精确卸（UnpatchAll）；补丁卸了，但动态程序集不可卸载",
                "目标: [<程序集>!]<类型>[::<成员>][(<参数短名>,...)]，例 Verse.DebugWindowsOpener::DrawButtons",
                "      Assembly-CSharp!RimWorld.Pawn  —— 类型级目标只列该类型下已被补过的方法");
        }

        // ==================== 游戏侧半（姿势 B provider） ====================

        /// <summary>
        /// 姿势 B provider：Bootstrap 注册 <c>ctx.Hook = HookCommand.Provide</c>（契约 v1.1 方法名）。
        /// 0Harmony 不可用时响亮报错（About.xml 已声明依赖 brrainz.harmony），绝不抛。
        /// </summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            try
            {
                Request request;
                SlashResult early;
                if (!TryParse(rest, out request, out early)) return early;

                if (request.Mode == "list") return DoList(request);
                if (request.Mode == "check") return DoCheck(request);
                return DoOff(request);
            }
            catch (Exception ex)
            {
                return SlashResults.Error(Kind, "/hook 内部异常: " + Describe(ex));
            }
        }

        private static SlashResult DoList(Request request)
        {
            string version;
            string error;
            if (!TryBridge<string>(delegate { return HarmonyBridge.Version(); }, out version, out error))
                return HarmonyMissing(error, null);

            List<PatchEntry> entries;
            if (!TryBridge<List<PatchEntry>>(delegate { return HarmonyBridge.Snapshot(); }, out entries, out error))
                return HarmonyMissing(error, null);

            return BuildList(entries, request.Limit, version);
        }

        private static SlashResult DoCheck(Request request)
        {
            TargetSpec spec;
            SlashResult parseError;
            if (!TryParseTarget(request.Target, out spec, out parseError)) return parseError;

            ResolvedTarget resolved;
            SlashResult resolveError;
            if (!ResolveTarget(spec, out resolved, out resolveError)) return resolveError;

            string version;
            string error;
            if (!TryBridge<string>(delegate { return HarmonyBridge.Version(); }, out version, out error))
                return HarmonyMissing(error, Data("target", request.Target));

            List<PatchEntry> snapshot;
            if (!TryBridge<List<PatchEntry>>(delegate { return HarmonyBridge.Snapshot(); }, out snapshot, out error))
                return HarmonyMissing(error, Data("target", request.Target));

            var checks = new List<MethodCheck>();
            if (resolved.TypeOnly)
            {
                // 类型级目标：只列该类型下已被补过的方法（每个方法一条）。
                var byMethod = new List<MethodBase>();
                var byMethodPatches = new List<List<PatchEntry>>();
                foreach (PatchEntry e in snapshot)
                {
                    if (e.Method == null) continue;
                    if (!string.Equals(e.TypeName, resolved.Type.FullName, StringComparison.Ordinal)) continue;
                    int at = IndexOfMethod(byMethod, e.Method);
                    if (at < 0)
                    {
                        byMethod.Add(e.Method);
                        byMethodPatches.Add(new List<PatchEntry>());
                        at = byMethod.Count - 1;
                    }
                    byMethodPatches[at].Add(e);
                }
                for (int i = 0; i < byMethod.Count; i++)
                    checks.Add(MakeCheck(byMethod[i], byMethodPatches[i], null));
                if (byMethod.Count == 0)
                    resolved.Notes.Add("类型级目标只列“已被补过”的方法；该类型下当前没有补丁。要查单个方法的 ReJIT 状态请写 <类型>::<方法>。");
            }
            else
            {
                foreach (MethodBase method in resolved.Methods)
                {
                    PatchesView view;
                    string perError;
                    bool ok = TryBridge<PatchesView>(delegate { return HarmonyBridge.GetPatches(method); },
                        out view, out perError);
                    checks.Add(MakeCheck(method, ok && view != null ? view.Patches : null,
                        ok ? null : perError));
                }
            }

            return BuildCheck(request.Target, spec, resolved, checks, version);
        }

        private static SlashResult DoOff(Request request)
        {
            string version;
            string error;
            if (!TryBridge<string>(delegate { return HarmonyBridge.Version(); }, out version, out error))
                return HarmonyMissing(error, Data("owner", request.Owner));

            List<PatchEntry> before;
            if (!TryBridge<List<PatchEntry>>(delegate { return HarmonyBridge.Snapshot(); }, out before, out error))
                return HarmonyMissing(error, Data("owner", request.Owner));

            var owned = new List<PatchEntry>();
            var affected = new List<string>();
            foreach (PatchEntry e in before)
            {
                if (!string.Equals(e.Owner, request.Owner, StringComparison.Ordinal)) continue;
                owned.Add(e);
                if (!affected.Contains(e.Target)) affected.Add(e.Target);
            }
            affected.Sort(StringComparer.Ordinal);

            if (owned.Count == 0)
            {
                string suggestion = CaseInsensitiveOwnerHint(before, request.Owner);
                var r0 = new SlashResult { Kind = Kind };
                r0.Lines.Add("/hook off " + request.Owner + " —— 没有 owner=“" + request.Owner
                    + "” 的补丁；未做任何改动（UnpatchAll 未执行）。");
                if (suggestion != null)
                    r0.Lines.Add("  注意: owner 比较是区分大小写的（Harmony: patch.owner == harmonyID）；你是想写 “"
                        + suggestion + "” 吗？");
                r0.Lines.Add("  可用 /hook list 看当前全部 owner。");
                r0.Data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["owner"] = request.Owner,
                    ["unpatchAll"] = false,
                    ["removed"] = 0,
                    ["remaining"] = 0,
                    ["patchCountBefore"] = before.Count,
                    ["patchCountAfter"] = before.Count,
                    ["affectedTargets"] = affected,
                    ["sharedOwner"] = string.Equals(request.Owner, SharedOwner, StringComparison.OrdinalIgnoreCase),
                    ["warnings"] = new List<string>(),
                    ["harmonyVersion"] = version,
                    ["caseInsensitiveOwner"] = suggestion,
                    ["note"] = "没有该 owner 的补丁，故未调用 UnpatchAll —— 无副作用。"
                };
                return r0;
            }

            string unpatchError = TryBridgeAction(delegate { HarmonyBridge.UnpatchOwner(request.Owner); });

            List<PatchEntry> after;
            string reError;
            bool recounted = TryBridge<List<PatchEntry>>(delegate { return HarmonyBridge.Snapshot(); },
                out after, out reError);
            if (!recounted) after = null;

            int remaining = 0;
            var afterOwned = new List<PatchEntry>();
            if (after != null)
            {
                foreach (PatchEntry e in after)
                    if (string.Equals(e.Owner, request.Owner, StringComparison.Ordinal)) afterOwned.Add(e);
                remaining = afterOwned.Count;
            }
            int removed = owned.Count - remaining;

            var warnings = new List<string>();
            bool shared = string.Equals(request.Owner, SharedOwner, StringComparison.OrdinalIgnoreCase);
            if (shared)
            {
                warnings.Add("owner “" + SharedOwner + "” 是 RoslynProbe 自己的 owner（Bootstrap new Harmony(SharedOwner) + "
                    + "PatchAll(typeof(Bootstrap).Assembly)）：这一卸会连带卸下调试工具栏按钮补丁（DebugToolbarPatch）。"
                    + "要保留工具栏按钮，请给 AI 自己的补丁用独立 owner（§3 约定 roslynprobe.<topic>）。");
            }
            if (unpatchError != null)
                warnings.Add("UnpatchAll 抛出: " + unpatchError + "（已按 before/after 重新计数，removed 是实测差值）。");
            if (after != null && remaining > 0)
                warnings.Add("仍有 " + remaining + " 条本 owner 的补丁没卸掉（可能在无方法体的方法上：UnpatchAll 对 Transpilers 无条件卸、"
                    + "对 Prefix/Postfix/Finalizer 只在 original.HasMethodBody() 时卸）。");
            if (after != null && after.Count < before.Count - removed)
                warnings.Add("除本 owner 外还少了 " + ((before.Count - removed) - after.Count)
                    + " 条补丁 —— 非预期（本指令只调用 UnpatchAll(owner)，不做全局卸），请用 /hook list 复核。");

            const string note = "补丁卸了，但动态程序集不可卸载：Harmony 的补丁体（含补丁方法引用的 MethodInfo）留在 0Harmony 的全局表里，"
                + "而 Roslyn scripting 的动态程序集在默认 ALC，Unload 需要零非 collectible 强引用（本项目 AI 代码直接操作游戏对象 ⇒ 死路）。"
                + "换代码请用 MethodBodyUpdater(ReJIT) 或 unpatch → repatch（TOOLSTACK §4.1）。";

            var r = new SlashResult { Kind = Kind };
            r.Lines.Add("/hook off " + request.Owner + " —— 已按 owner 卸下 " + removed + " 条补丁"
                + (after == null ? "（重计数失败，removed 按下限 0 计）" : "（剩余本 owner 补丁 " + remaining + " 条）"));
            if (affected.Count > 0)
                r.Lines.Add("  受影响目标(" + affected.Count + "): " + string.Join("、", affected.GetRange(0, Math.Min(affected.Count, 10)))
                    + (affected.Count > 10 ? " …" : ""));
            foreach (string w in warnings) r.Lines.Add("  ⚠ " + w);
            r.Lines.Add("  注意: " + note);

            r.Data = new Dictionary<string, object>
            {
                ["ok"] = unpatchError == null,
                ["owner"] = request.Owner,
                ["unpatchAll"] = true,
                ["removed"] = removed,
                ["remaining"] = remaining,
                ["patchCountBefore"] = before.Count,
                ["patchCountAfter"] = after == null ? (object)null : after.Count,
                ["affectedTargets"] = affected,
                ["affectedPatches"] = owned.Count,
                ["sharedOwner"] = shared,
                ["warnings"] = warnings,
                ["harmonyVersion"] = version,
                ["unpatchError"] = unpatchError,
                ["note"] = note
            };
            if (unpatchError != null) r.Data["error"] = "UnpatchAll 抛出: " + unpatchError;
            return r;
        }

        private static SlashResult HarmonyMissing(string error, Dictionary<string, object> extra)
        {
            return Fail("0Harmony 不可用：" + error
                + " —— /hook 需要 Harmony 模组（About.xml 已声明依赖 brrainz.harmony）。", extra);
        }

        // ==================== list 的纯逻辑（可在无 0Harmony 环境下用合成数据单测） ====================

        /// <summary>
        /// 由补丁条目快照构造 /hook list 的结果（Lines + Data 同源）。
        /// 每条 hook = 一个 (target, owner) 对；patchCount 是该对的补丁条目总数（含 inner 两类）。
        /// </summary>
        internal static SlashResult BuildList(List<PatchEntry> entries, int limit, string harmonyVersion)
        {
            if (entries == null) entries = new List<PatchEntry>();
            entries.Sort(ComparePatchEntries);

            var targetOrder = new List<string>();
            var byTarget = new Dictionary<string, List<PatchEntry>>(StringComparer.Ordinal);
            foreach (PatchEntry e in entries)
            {
                string target = e.Target ?? "?";
                List<PatchEntry> bucket;
                if (!byTarget.TryGetValue(target, out bucket))
                {
                    bucket = new List<PatchEntry>();
                    byTarget[target] = bucket;
                    targetOrder.Add(target);
                }
                bucket.Add(e);
            }
            targetOrder.Sort(StringComparer.Ordinal);

            var hooks = new List<Dictionary<string, object>>();
            foreach (string target in targetOrder)
            {
                var byOwner = new Dictionary<string, List<PatchEntry>>(StringComparer.Ordinal);
                var ownerOrder = new List<string>();
                foreach (PatchEntry e in byTarget[target])
                {
                    string owner = e.Owner ?? "(null)";
                    List<PatchEntry> bucket;
                    if (!byOwner.TryGetValue(owner, out bucket))
                    {
                        bucket = new List<PatchEntry>();
                        byOwner[owner] = bucket;
                        ownerOrder.Add(owner);
                    }
                    bucket.Add(e);
                }
                ownerOrder.Sort(StringComparer.Ordinal);

                foreach (string owner in ownerOrder)
                {
                    List<PatchEntry> bucket = byOwner[owner];
                    int prefixes = 0, postfixes = 0, transpilers = 0, finalizers = 0, innerPrefixes = 0, innerPostfixes = 0;
                    int innerFinalizers = 0;
                    int minPriority = int.MaxValue;
                    var priorities = new List<int>();
                    var patchMethods = new List<string>();
                    foreach (PatchEntry e in bucket)
                    {
                        CountKind(e.PatchKind, ref prefixes, ref postfixes, ref transpilers, ref finalizers,
                            ref innerPrefixes, ref innerPostfixes, ref innerFinalizers);
                        if (e.Priority < minPriority) minPriority = e.Priority;
                        if (!priorities.Contains(e.Priority)) priorities.Add(e.Priority);
                        string pm = e.PatchMethod ?? "(取不到)";
                        if (!patchMethods.Contains(pm)) patchMethods.Add(pm);
                    }
                    priorities.Sort();
                    patchMethods.Sort(StringComparer.Ordinal);
                    hooks.Add(new Dictionary<string, object>
                    {
                        ["target"] = target,
                        ["owner"] = owner,
                        ["patchCount"] = bucket.Count,
                        ["priority"] = minPriority == int.MaxValue ? 0 : minPriority,
                        ["priorities"] = priorities,
                        ["prefixes"] = prefixes,
                        ["postfixes"] = postfixes,
                        ["transpilers"] = transpilers,
                        ["finalizers"] = finalizers,
                        ["innerPrefixes"] = innerPrefixes,
                        ["innerPostfixes"] = innerPostfixes,
                        ["innerFinalizers"] = innerFinalizers,
                        ["patchMethods"] = patchMethods
                    });
                }
            }

            int totalHooks = hooks.Count;
            bool truncated = totalHooks > limit;
            List<Dictionary<string, object>> shown = truncated ? hooks.GetRange(0, limit) : hooks;

            var owners = BuildOwnerSummary(entries);
            var types = BuildTypeSummary(entries);

            var r = new SlashResult { Kind = Kind };
            r.Lines.Add("Harmony 补丁（全进程）: " + targetOrder.Count + " 个目标 / " + entries.Count + " 条补丁 / "
                + owners.Count + " 个 owner" + (string.IsNullOrEmpty(harmonyVersion) ? "" : "  (0Harmony " + harmonyVersion + ")"));
            if (entries.Count == 0)
                r.Lines.Add("  （全进程没有任何 Harmony 补丁；用 /eval 的 using HarmonyLib; 挂补丁后回来看）");
            foreach (Dictionary<string, object> h in shown)
            {
                string extra = "";
                int inner = (int)h["innerPrefixes"] + (int)h["innerPostfixes"] + (int)h["innerFinalizers"];
                if (inner > 0) extra = " inner" + inner;
                r.Lines.Add("  " + h["target"] + "  [" + h["owner"] + "] ×" + h["patchCount"]
                    + "  priority=" + h["priority"]
                    + "  (prefix " + h["prefixes"] + " / postfix " + h["postfixes"] + " / transpiler "
                    + h["transpilers"] + " / finalizer " + h["finalizers"] + extra + ")");
            }
            if (truncated)
                r.Lines.Add("  …按 (target, owner) 排序只显示前 " + limit + " 条 hook（共 " + totalHooks
                    + " 条，目标 " + targetOrder.Count + " 个）；/hook list <n> 调上限。");
            if (owners.Count > 0)
                r.Lines.Add("  owner 汇总: " + JoinSummaries(owners, "owner"));
            if (types.Count > 0)
                r.Lines.Add("  类型汇总: " + JoinSummaries(types, "type"));

            r.Data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["count"] = targetOrder.Count,
                ["patchCount"] = entries.Count,
                ["hookCount"] = totalHooks,
                ["returnedHookCount"] = shown.Count,
                ["truncated"] = truncated,
                ["limit"] = limit,
                ["ownerCount"] = owners.Count,
                ["harmonyVersion"] = harmonyVersion,
                ["hooks"] = shown,
                ["owners"] = owners,
                ["types"] = types
            };
            return r;
        }

        private static List<Dictionary<string, object>> BuildOwnerSummary(List<PatchEntry> entries)
        {
            var patchCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var targets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (PatchEntry e in entries)
            {
                string owner = e.Owner ?? "(null)";
                int n;
                patchCount.TryGetValue(owner, out n);
                patchCount[owner] = n + 1;
                List<string> set;
                if (!targets.TryGetValue(owner, out set)) { set = new List<string>(); targets[owner] = set; }
                string target = e.Target ?? "?";
                if (!set.Contains(target)) set.Add(target);
            }
            return Summaries(patchCount, targets, "owner");
        }

        private static List<Dictionary<string, object>> BuildTypeSummary(List<PatchEntry> entries)
        {
            var patchCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var targets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (PatchEntry e in entries)
            {
                string type = e.TypeName ?? "(dynamic)";
                int n;
                patchCount.TryGetValue(type, out n);
                patchCount[type] = n + 1;
                List<string> set;
                if (!targets.TryGetValue(type, out set)) { set = new List<string>(); targets[type] = set; }
                string target = e.Target ?? "?";
                if (!set.Contains(target)) set.Add(target);
            }
            return Summaries(patchCount, targets, "type");
        }

        private static List<Dictionary<string, object>> Summaries(Dictionary<string, int> patchCount,
            Dictionary<string, List<string>> targets, string keyName)
        {
            var names = new List<string>();
            foreach (KeyValuePair<string, int> pair in patchCount) names.Add(pair.Key);
            names.Sort(delegate(string a, string b)
            {
                int c = patchCount[b].CompareTo(patchCount[a]);
                return c != 0 ? c : string.CompareOrdinal(a, b);
            });

            var list = new List<Dictionary<string, object>>();
            foreach (string name in names)
            {
                var d = new Dictionary<string, object>();
                d[keyName] = name;
                d["patchCount"] = patchCount[name];
                d["targetCount"] = targets[name].Count;
                list.Add(d);
            }
            return list;
        }

        private static string JoinSummaries(List<Dictionary<string, object>> summaries, string keyName)
        {
            var parts = new List<string>();
            int max = Math.Min(summaries.Count, 8);
            for (int i = 0; i < max; i++)
                parts.Add(summaries[i][keyName] + " ×" + summaries[i]["patchCount"]
                    + "(" + summaries[i]["targetCount"] + " 目标)");
            string text = string.Join("、", parts);
            if (summaries.Count > max) text += "、…共 " + summaries.Count + " 个";
            return text;
        }

        private static void CountKind(string kind, ref int prefixes, ref int postfixes, ref int transpilers,
            ref int finalizers, ref int innerPrefixes, ref int innerPostfixes, ref int innerFinalizers)
        {
            if (kind == "prefix") prefixes++;
            else if (kind == "postfix") postfixes++;
            else if (kind == "transpiler") transpilers++;
            else if (kind == "finalizer") finalizers++;
            else if (kind == "innerPrefix") innerPrefixes++;
            else if (kind == "innerPostfix") innerPostfixes++;
            else if (kind == "innerFinalizer") innerFinalizers++;
        }

        private static int ComparePatchEntries(PatchEntry a, PatchEntry b)
        {
            int c = string.CompareOrdinal(a.Target ?? "", b.Target ?? "");
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Owner ?? "", b.Owner ?? "");
            if (c != 0) return c;
            c = string.CompareOrdinal(a.PatchKind ?? "", b.PatchKind ?? "");
            if (c != 0) return c;
            c = a.Priority.CompareTo(b.Priority);
            if (c != 0) return c;
            c = a.Index.CompareTo(b.Index);
            if (c != 0) return c;
            return string.CompareOrdinal(a.PatchMethod ?? "", b.PatchMethod ?? "");
        }

        // ==================== check 的纯逻辑（渲染，可用合成数据单测） ====================

        internal static MethodCheck MakeCheck(MethodBase method, List<PatchEntry> patches, string patchError)
        {
            var check = new MethodCheck();
            check.Method = method;
            check.MethodText = DescribeMethod(method);
            check.TypeName = SafeTypeName(method);
            check.AssemblyName = SafeAssemblyName(method);
            check.MetadataToken = SafeToken(method);
            check.PatchInfoError = patchError;
            if (patches != null)
            {
                foreach (PatchEntry e in patches)
                {
                    if (e == null) continue;
                    check.Patches.Add(e);
                    CountKind(e.PatchKind, ref check.Prefixes, ref check.Postfixes, ref check.Transpilers,
                        ref check.Finalizers, ref check.InnerPrefixes, ref check.InnerPostfixes, ref check.InnerFinalizers);
                }
                check.Patches.Sort(ComparePatchEntries);
            }
            check.Rejit = ProbeRejit(method);
            return check;
        }

        internal static SlashResult BuildCheck(string rawTarget, TargetSpec spec, ResolvedTarget resolved,
            List<MethodCheck> checks, string harmonyVersion)
        {
            if (checks == null) checks = new List<MethodCheck>();
            bool typeOnly = resolved != null && resolved.TypeOnly;

            var r = new SlashResult { Kind = Kind };
            int totalLayers = 0;
            bool anyPatched = false;
            bool anyRejit = false;
            bool anyConflict = false;
            bool anyRejitUnknown = false;
            var methods = new List<Dictionary<string, object>>();

            foreach (MethodCheck check in checks)
            {
                if (check.Layers > 0) anyPatched = true;
                totalLayers += check.Layers;
                bool rejitUpdated = BoolOf(check.Rejit, "updated");
                bool rejitKnown = check.Rejit != null && check.Rejit["available"] is bool && (bool)check.Rejit["available"]
                    && check.Rejit["updated"] is bool;   // token 读不出时 updated=null ⇒ 同样算“不可判定”，不伪装成确定
                if (rejitUpdated) anyRejit = true;
                if (!rejitKnown) anyRejitUnknown = true;
                if (rejitUpdated && check.Layers > 0) anyConflict = true;

                r.Lines.Add("  " + check.MethodText + "  [" + (check.AssemblyName ?? "?")
                    + " token=0x" + check.MetadataToken.ToString("x8", CultureInfo.InvariantCulture) + "]");

                if (check.PatchInfoError != null)
                {
                    r.Lines.Add("    Harmony: 不可判定 —— " + check.PatchInfoError);
                }
                else if (check.Layers == 0)
                {
                    r.Lines.Add("    Harmony: 未打补丁");
                }
                else
                {
                    r.Lines.Add("    Harmony: " + check.Layers + " 层（prefix " + check.Prefixes + " / postfix "
                        + check.Postfixes + " / transpiler " + check.Transpilers + " / finalizer " + check.Finalizers
                        + (check.InnerPrefixes + check.InnerPostfixes + check.InnerFinalizers > 0
                            ? " / inner " + (check.InnerPrefixes + check.InnerPostfixes + check.InnerFinalizers) : "")
                        + "）");
                    foreach (PatchEntry e in check.Patches)
                        r.Lines.Add("      " + (e.PatchKind ?? "?") + "  owner=" + (e.Owner ?? "(null)")
                            + "  priority=" + e.Priority + "  index=" + e.Index
                            + "  patchMethod=" + (e.PatchMethod ?? "(取不到)"));
                }

                r.Lines.Add("    ReJIT: " + RejitLine(check.Rejit));
                if (rejitUpdated && check.Layers > 0)
                    r.Lines.Add("    ⚠ 同一方法上同时有 Harmony 补丁与 ReJIT 更新 —— §3「不可叠：叠加会破坏跳转链」"
                        + "（UESDdebuger/Source/UELoader/HotReloadDetour.cs:13）。");

                methods.Add(new Dictionary<string, object>
                {
                    ["method"] = check.MethodText,
                    ["type"] = check.TypeName,
                    ["assembly"] = check.AssemblyName,
                    ["metadataToken"] = check.MetadataToken,
                    ["patched"] = check.Layers > 0,
                    ["layers"] = check.Layers,
                    ["prefixes"] = check.Prefixes,
                    ["postfixes"] = check.Postfixes,
                    ["transpilers"] = check.Transpilers,
                    ["finalizers"] = check.Finalizers,
                    ["innerPrefixes"] = check.InnerPrefixes,
                    ["innerPostfixes"] = check.InnerPostfixes,
                    ["innerFinalizers"] = check.InnerFinalizers,
                    ["patchInfoError"] = check.PatchInfoError,
                    ["patches"] = PatchData(check.Patches),
                    ["conflict"] = rejitUpdated && check.Layers > 0,
                    ["rejit"] = check.Rejit
                });
            }

            r.Lines.Insert(0, "/hook check " + rawTarget + " —— " + (typeOnly ? "类型级目标" : "方法级目标")
                + "，命中 " + checks.Count + " 个方法" + (checks.Count == 0 ? "" : "")
                + (string.IsNullOrEmpty(harmonyVersion) ? "" : "  (0Harmony " + harmonyVersion + ")"));
            if (checks.Count == 0)
                r.Lines.Add("  （没有命中任何方法）");
            r.Lines.Add("  合计: " + totalLayers + " 层补丁；ReJIT 侧 " + (anyRejit ? "有方法被 MethodBodyUpdater 动过"
                : (anyRejitUnknown ? "不可判定（原因见上）" : "未发现被 MethodBodyUpdater 动过的方法")));
            if (resolved != null && resolved.Truncated)
                r.Lines.Add("  …命中超过上限，只显示前 " + checks.Count + " 个方法；请写更具体的 <类型>::<成员>。");
            if (resolved != null) foreach (string note in resolved.Notes) r.Lines.Add("  注: " + note);

            r.Data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["target"] = rawTarget,
                ["typeOnly"] = typeOnly,
                ["matchCount"] = checks.Count,
                ["patched"] = anyPatched,
                ["layers"] = totalLayers,
                ["rejitTouchedAny"] = anyRejit,
                ["rejitUnknownAny"] = anyRejitUnknown,
                ["conflictAny"] = anyConflict,
                ["harmonyVersion"] = harmonyVersion,
                ["spec"] = new Dictionary<string, object>
                {
                    ["assembly"] = spec == null ? null : spec.AssemblyPattern,
                    ["type"] = spec == null ? null : spec.TypeText,
                    ["member"] = spec == null ? null : spec.MemberText,
                    ["parameters"] = spec == null ? null : spec.Parameters
                },
                ["notes"] = resolved == null ? new List<string>() : resolved.Notes,
                ["methods"] = methods
            };
            return r;
        }

        private static List<Dictionary<string, object>> PatchData(List<PatchEntry> patches)
        {
            var list = new List<Dictionary<string, object>>();
            if (patches == null) return list;
            foreach (PatchEntry e in patches)
                list.Add(new Dictionary<string, object>
                {
                    ["owner"] = e.Owner,
                    ["kind"] = e.PatchKind,
                    ["priority"] = e.Priority,
                    ["index"] = e.Index,
                    ["patchMethod"] = e.PatchMethod
                });
            return list;
        }

        private static string RejitLine(Dictionary<string, object> rejit)
        {
            if (rejit == null) return "不可判定 —— 探测未执行。";
            bool available = rejit["available"] is bool && (bool)rejit["available"];
            string reason = rejit["reason"] as string;
            if (!available)
                return "不可判定 —— " + (string.IsNullOrEmpty(reason) ? "取不到 WaylithClr.MethodBodyUpdater。" : reason);

            bool? updated = rejit["updated"] as bool?;
            if (!updated.HasValue) return "不可判定 —— " + (string.IsNullOrEmpty(reason) ? "token 集合读不出。" : reason);

            var parts = new List<string>();
            parts.Add("module=" + (rejit["module"] as string ?? "?"));
            parts.Add("该模块已更新方法体 token=" + ShowValue(rejit["updatedMethodTokenCount"]));
            parts.Add("IsSupported=" + ShowValue(rejit["supported"]));
            parts.Add("MetadataUpdateVersion=" + ShowValue(rejit["metadataUpdateVersion"]));
            string text = updated.Value ? "被 MethodBodyUpdater 动过" : "未被 MethodBodyUpdater 动过";
            return text + "（" + string.Join(", ", parts) + "）" + (string.IsNullOrEmpty(reason) ? "" : "；注: " + reason);
        }

        private static bool BoolOf(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return false;
            object v;
            if (!dict.TryGetValue(key, out v)) return false;
            return v is bool && (bool)v;
        }

        // ==================== ReJIT 判定（纯反射；取不到写 reason） ====================

        /// <summary>
        /// 目标方法是否被 MethodBodyUpdater（ReJIT / 热更新）动过。全反射，永不抛。
        /// 判法照抄引擎自己（Waylith.Profiling.Instrumentation.ProfilerInstrumentation.ReconcileMetadataUpdates）：
        /// <c>GetUpdatedMethodTokens(module)</c> 返回被换过方法体的 metadata token，命中目标方法的 MetadataToken 即“动过”。
        /// </summary>
        internal static Dictionary<string, object> ProbeRejit(MethodBase method)
        {
            var d = new Dictionary<string, object>
            {
                ["available"] = false,
                ["supported"] = null,
                ["metadataUpdateVersion"] = null,
                ["updated"] = null,
                ["updatedMethodTokenCount"] = null,
                ["module"] = null,
                ["typeAssembly"] = null,
                ["reason"] = null
            };

            if (method == null)
            {
                d["reason"] = "目标方法为 null。";
                return d;
            }

            Type updater;
            string findReason;
            if (!TryFindMethodBodyUpdater(out updater, out findReason))
            {
                d["reason"] = findReason;
                return d;
            }
            d["typeAssembly"] = SafeAssemblyName(updater);

            object supported;
            string supportedError;
            if (TryReadStatic(updater, "IsSupported", out supported, out supportedError))
                d["supported"] = supported;
            else
                d["supportedError"] = supportedError;

            object version;
            string versionError;
            if (TryReadStatic(updater, "MetadataUpdateVersion", out version, out versionError))
                d["metadataUpdateVersion"] = version;
            else
                d["metadataUpdateVersionError"] = versionError;

            Module module = null;
            try { module = method.Module; }
            catch (Exception ex) { d["reason"] = "取不到目标方法的 Module: " + Describe(ex); return d; }
            if (module == null)
            {
                d["reason"] = "目标方法的 Module 为 null（动态方法？）—— 无法按模块查 ReJIT token。";
                return d;
            }
            d["module"] = SafeStr(delegate { return module.Name; });

            MethodInfo getter = FindStaticMethod(updater, "GetUpdatedMethodTokens",
                delegate(ParameterInfo p) { return p.ParameterType == typeof(Module); });
            if (getter == null)
            {
                d["reason"] = "WaylithClr.MethodBodyUpdater 上没有 GetUpdatedMethodTokens(Module)（签名变了？）—— 无法判定。";
                return d;
            }

            object raw;
            try { raw = getter.Invoke(null, new object[] { module }); }
            catch (Exception ex)
            {
                d["reason"] = "GetUpdatedMethodTokens 调用失败: " + Describe(ex);
                return d;
            }

            int[] tokens = ToIntArray(raw);
            if (tokens == null)
            {
                d["reason"] = "GetUpdatedMethodTokens 返回了非整数序列（" + (raw == null ? "null" : raw.GetType().Name)
                    + "）—— 无法判定。";
                return d;
            }

            d["available"] = true;
            d["updatedMethodTokenCount"] = tokens.Length;
            int token = SafeToken(method);
            if (token == 0)
            {
                // “读不出 token”必须与“确定不在集合里”区分开（同口径：不可判定 ⇒ updated 保持 null + reason）。
                d["reason"] = "目标方法的 MetadataToken 读不出来（0）—— 无法与 GetUpdatedMethodTokens 的 token 对齐，"
                    + "故不做判定（updated=null）。";
                return d;
            }
            d["updated"] = ContainsToken(tokens, token);
            if (tokens.Length == 0)
                d["reason"] = "该模块没有任何已更新方法体（MethodBodyUpdater.GetUpdatedMethodTokens 返回空）"
                    + " ⇒ 目标方法未被 ReJIT/热更新动过。";
            else if (d["supported"] is bool && !(bool)d["supported"])
                d["reason"] = "MethodBodyUpdater.IsSupported=false（本运行时未启用 ReJIT 面），但模块仍报告了 "
                    + tokens.Length + " 个已更新 token。";

            return d;
        }

        private static bool TryFindMethodBodyUpdater(out Type type, out string reason)
        {
            type = null;
            reason = null;
            const string fullName = "WaylithClr.MethodBodyUpdater";
            try { type = Type.GetType(fullName, false); }
            catch (Exception) { type = null; }
            if (type != null) return true;

            Assembly[] assemblies = null;
            try { assemblies = AppDomain.CurrentDomain.GetAssemblies(); }
            catch (Exception) { assemblies = null; }
            if (assemblies != null)
            {
                foreach (Assembly a in assemblies)
                {
                    if (a == null) continue;
                    if (!MayContainWaylithClr(a)) continue;
                    try { type = a.GetType(fullName, false); }
                    catch (Exception) { type = null; }
                    if (type != null) return true;
                }
            }

            reason = "本进程 AppDomain 里没有 WaylithClr.MethodBodyUpdater（WaylithClr 的 ReJIT 面定义在 Waylith 运行时的 "
                + "System.Private.CoreLib 里，普通 CoreCLR 下取不到）⇒ 无法判定是否被 ReJIT 动过；完整反射面见 /env waylithclr。";
            return false;
        }

        /// <summary>
        /// 只扫“可能带 WaylithClr”的程序集：名字含 Waylith，或核心库 / 本程序集。
        /// 本程序集在游戏进程里叫 RoslynProbe，在测试面里叫 SlashHarness（tests/SlashHarness；
        /// 本工程原名 SlashCheck，搬入 tests/ 时改名 SlashHarness）。
        /// </summary>
        private static bool MayContainWaylithClr(Assembly a)
        {
            string n = SafeStr(delegate { return a.GetName().Name; });
            if (string.IsNullOrEmpty(n)) return false;
            if (n.IndexOf("Waylith", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return string.Equals(n, "System.Private.CoreLib", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n, "System.Runtime", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n, "RoslynProbe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n, "SlashHarness", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadStatic(Type type, string name, out object value, out string error)
        {
            value = null;
            error = null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                FieldInfo field = type.GetField(name, flags);
                if (field != null) { value = field.GetValue(null); return true; }
                PropertyInfo prop = type.GetProperty(name, flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                {
                    value = prop.GetValue(null, null);
                    return true;
                }
                error = "没有静态字段/属性 " + name;
                return false;
            }
            catch (Exception ex)
            {
                error = "读 " + name + " 失败: " + Describe(ex);
                return false;
            }
        }

        private static MethodInfo FindStaticMethod(Type type, string name, Predicate<ParameterInfo> lastParameter)
        {
            try
            {
                MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                foreach (MethodInfo m in methods)
                {
                    if (!string.Equals(m.Name, name, StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    if (lastParameter != null && !lastParameter(ps[ps.Length - 1])) continue;
                    return m;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static int[] ToIntArray(object raw)
        {
            int[] array = raw as int[];
            if (array != null) return array;
            IEnumerable enumerable = raw as IEnumerable;
            if (enumerable == null) return null;
            var list = new List<int>();
            foreach (object item in enumerable)
            {
                if (item == null) return null;
                if (!(item is int)) return null;
                list.Add((int)item);
            }
            return list.ToArray();
        }

        private static bool ContainsToken(int[] tokens, int token)
        {
            if (tokens == null || token == 0) return false;
            for (int i = 0; i < tokens.Length; i++) if (tokens[i] == token) return true;
            return false;
        }

        // ==================== 目标解析（字符串 → 类型/方法，纯反射） ====================

        internal static bool TryParseTarget(string text, out TargetSpec spec, out SlashResult error)
        {
            spec = null;
            error = null;
            string raw = (text ?? "").Trim();
            if (raw.Length == 0)
            {
                error = Fail("check 的目标为空。", null);
                return false;
            }

            string body = raw;
            string assembly = null;
            int bang = body.IndexOf('!');
            if (bang >= 0)
            {
                assembly = body.Substring(0, bang).Trim();
                body = body.Substring(bang + 1).Trim();
                if (assembly.Length == 0 || body.Length == 0)
                {
                    error = Fail("目标 “" + raw + "” 的程序集前缀为空：形如 Assembly-CSharp!RimWorld.Pawn。", Data("target", raw));
                    return false;
                }
            }

            var parameters = new List<string>();
            int open = body.IndexOf('(');
            if (open >= 0)
            {
                if (!body.EndsWith(")", StringComparison.Ordinal))
                {
                    error = Fail("目标 “" + raw + "” 的参数表没有闭合的 ')'。", Data("target", raw));
                    return false;
                }
                string inner = body.Substring(open + 1, body.Length - open - 2).Trim();
                body = body.Substring(0, open).Trim();
                if (inner.Length > 0)
                {
                    string[] parts = inner.Split(',');
                    foreach (string p in parts)
                    {
                        string t = p.Trim();
                        if (t.Length > 0) parameters.Add(t);
                    }
                }
            }

            string typeText = body;
            string memberText = null;
            int sep = body.IndexOf("::", StringComparison.Ordinal);
            if (sep >= 0)
            {
                typeText = body.Substring(0, sep).Trim();
                memberText = body.Substring(sep + 2).Trim();
            }
            if (typeText.Length == 0)
            {
                error = Fail("目标 “" + raw + "” 的类型部分为空。", Data("target", raw));
                return false;
            }
            if (sep >= 0 && (memberText == null || memberText.Length == 0))
            {
                error = Fail("目标 “" + raw + "” 的成员部分为空（:: 后面要写成员名，如 Verse.Pawn::Kill）。",
                    Data("target", raw));
                return false;
            }

            spec = new TargetSpec();
            spec.Raw = raw;
            spec.AssemblyPattern = assembly;
            spec.TypeText = typeText;
            spec.MemberText = memberText;
            spec.Parameters = parameters;
            return true;
        }

        internal static bool ResolveTarget(TargetSpec spec, out ResolvedTarget resolved, out SlashResult error)
        {
            resolved = null;
            error = null;

            List<Type> types = FindTypes(spec.TypeText, spec.AssemblyPattern);
            if (types.Count == 0 && spec.MemberText == null)
            {
                // 没有显式成员分隔符：把最后一个 '.' 之后当成员名再试一次（Verse.Pawn.Kill 这种最常见写法）。
                int dot = spec.TypeText.LastIndexOf('.');
                while (dot > 0)
                {
                    string prefix = spec.TypeText.Substring(0, dot);
                    string member = spec.TypeText.Substring(dot + 1);
                    List<Type> hit = FindTypes(prefix, spec.AssemblyPattern);
                    if (hit.Count > 0)
                    {
                        if (hit.Count > 1)
                        {
                            error = AmbiguousType(spec, hit);
                            return false;
                        }
                        var spec2 = new TargetSpec();
                        spec2.Raw = spec.Raw;
                        spec2.AssemblyPattern = spec.AssemblyPattern;
                        spec2.TypeText = prefix;
                        spec2.MemberText = member;
                        spec2.Parameters = spec.Parameters;
                        return ResolveTarget(spec2, out resolved, out error);
                    }
                    dot = spec.TypeText.LastIndexOf('.', dot - 1);
                }
            }

            if (types.Count == 0)
            {
                error = Fail("未找到类型 “" + spec.TypeText + "”" + AssemblySuffix(spec.AssemblyPattern)
                    + "（反射已加载程序集；类型名要写全名，如 Verse.DebugWindowsOpener）。", Data("target", spec.Raw));
                return false;
            }

            if (spec.MemberText == null)
            {
                if (types.Count > 1)
                {
                    error = AmbiguousType(spec, types);
                    return false;
                }
                resolved = new ResolvedTarget();
                resolved.Spec = spec;
                resolved.Type = types[0];
                resolved.Assembly = SafeAssembly(types[0]);
                resolved.TypeOnly = true;
                return true;
            }

            resolved = new ResolvedTarget();
            resolved.Spec = spec;
            resolved.Type = types[0];
            resolved.Assembly = SafeAssembly(types[0]);
            resolved.TypeOnly = false;

            var matches = new List<MethodBase>();
            var caseInsensitive = new List<MethodBase>();
            var available = new List<string>();
            foreach (Type t in types)
            {
                foreach (MethodBase m in AllMethods(t))
                {
                    string label = DescribeMethod(m);
                    if (!available.Contains(label)) available.Add(label);
                    if (!MemberMatches(m, t, spec.MemberText)) continue;
                    if (ParametersMatch(m, spec.Parameters)) matches.Add(m);
                    else caseInsensitive.Add(m);
                }
            }

            if (matches.Count == 0 && caseInsensitive.Count > 0)
                matches = caseInsensitive;

            if (matches.Count == 0)
            {
                available.Sort(StringComparer.Ordinal);
                error = Fail("类型 “" + types[0].FullName + "” 里没有成员 “" + spec.MemberText + "”"
                    + (spec.Parameters.Count > 0 ? "(" + string.Join(", ", spec.Parameters) + ")" : "")
                    + "；构造函数写 ctor/cctor，重载可用 (参数短名,...) 消歧。", Data("candidates", Cap(available, 20)));
                return false;
            }

            matches.Sort(CompareMethods);
            if (matches.Count > MaxMethodMatches)
            {
                matches = matches.GetRange(0, MaxMethodMatches);
                resolved.Truncated = true;
            }
            resolved.Methods = matches;
            return true;
        }

        private static List<Type> FindTypes(string typeText, string assemblyPattern)
        {
            var found = new List<Type>();
            Assembly[] assemblies = null;
            try { assemblies = AppDomain.CurrentDomain.GetAssemblies(); }
            catch (Exception) { assemblies = null; }
            if (assemblies == null) return found;

            var candidates = new List<Assembly>();
            foreach (Assembly a in assemblies)
            {
                if (a == null || a.IsDynamic) continue;
                string name = SafeStr(delegate { return a.GetName().Name; });
                if (assemblyPattern != null && !WildcardMatch(assemblyPattern, name)) continue;
                candidates.Add(a);
            }

            var exact = new List<Type>();
            var ci = new List<Type>();
            var simple = new List<Type>();
            foreach (Assembly a in candidates)
            {
                Type[] types = null;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch (Exception) { types = null; }
                if (types == null) continue;
                foreach (Type t in types)
                {
                    if (t == null) continue;
                    string full = null;
                    try { full = t.FullName; } catch (Exception) { full = null; }
                    if (full == null) continue;
                    if (string.Equals(full, typeText, StringComparison.Ordinal)) { AddType(exact, t); continue; }
                    if (string.Equals(full, typeText, StringComparison.OrdinalIgnoreCase)) { AddType(ci, t); continue; }
                    int dot = typeText.LastIndexOf('.');
                    string simpleName = dot >= 0 ? typeText.Substring(dot + 1) : typeText;
                    if (string.Equals(t.Name, simpleName, StringComparison.Ordinal)) AddType(simple, t);
                }
            }

            if (exact.Count > 0) found.AddRange(exact);
            else if (ci.Count > 0) found.AddRange(ci);
            else found.AddRange(simple);
            return found;
        }

        private static void AddType(List<Type> list, Type t)
        {
            if (!list.Contains(t)) list.Add(t);
        }

        private static SlashResult AmbiguousType(TargetSpec spec, List<Type> types)
        {
            var names = new List<string>();
            foreach (Type t in types)
            {
                Assembly a = SafeAssembly(t);
                names.Add(t.FullName + (a == null ? "" : "  [" + SafeStr(delegate { return a.GetName().Name; }) + "]"));
            }
            names.Sort(StringComparer.Ordinal);
            return Fail("类型名 “" + spec.TypeText + "” 在多个程序集里都命中（" + types.Count + " 个）："
                + string.Join("、", Cap(names, 8)) + "；请用 <程序集名>!<类型> 消歧（程序集名支持 * ? 通配）。",
                Data("candidates", Cap(names, 20)));
        }

        private static List<MethodBase> AllMethods(Type type)
        {
            var list = new List<MethodBase>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            try
            {
                foreach (MethodInfo m in type.GetMethods(flags))
                    if (m != null && !m.IsSpecialName) list.Add(m);
                foreach (ConstructorInfo c in type.GetConstructors(flags)) if (c != null) list.Add(c);
            }
            catch (Exception) { }
            return list;
        }

        private static bool MemberMatches(MethodBase m, Type owner, string member)
        {
            if (m is ConstructorInfo)
            {
                string mt = member;
                if (mt.StartsWith("::", StringComparison.Ordinal)) mt = mt.Substring(2);
                if (string.Equals(mt, owner.Name, StringComparison.OrdinalIgnoreCase)) mt = "ctor";
                if (m.IsStatic)
                    return string.Equals(mt, "cctor", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(mt, ".cctor", StringComparison.OrdinalIgnoreCase);
                return string.Equals(mt, "ctor", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mt, ".ctor", StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(m.Name, member, StringComparison.Ordinal)
                || string.Equals(m.Name, member, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ParametersMatch(MethodBase m, List<string> parameters)
        {
            if (parameters == null || parameters.Count == 0) return true;
            ParameterInfo[] ps;
            try { ps = m.GetParameters(); }
            catch (Exception) { return false; }
            if (ps.Length != parameters.Count) return false;
            for (int i = 0; i < ps.Length; i++)
            {
                string given = parameters[i].Trim();
                Type actual = ps[i].ParameterType;
                string actualFull = actual.FullName ?? actual.Name;
                if (!string.Equals(given, actual.Name, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(given, actualFull, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static int CompareMethods(MethodBase a, MethodBase b)
        {
            int c = string.CompareOrdinal(SafeTypeName(a), SafeTypeName(b));
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Name, b.Name);
            if (c != 0) return c;
            c = ParameterCount(a).CompareTo(ParameterCount(b));
            if (c != 0) return c;
            c = string.CompareOrdinal(DescribeMethod(a), DescribeMethod(b));
            if (c != 0) return c;
            return SafeToken(a).CompareTo(SafeToken(b));
        }

        private static int IndexOfMethod(List<MethodBase> list, MethodBase m)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == m) return i;
            return -1;
        }

        // ==================== Harmony 桥（所有 HarmonyLib 类型引用都关在这里） ====================
        //
        // 为什么单开一层：若 0Harmony 载不进来（缺 DLL / 非游戏进程），对 HarmonyLib 类型的引用会让
        // **方法级** JIT 失败并抛 FileNotFoundException。把引用关在下述方法里，外层调用点（不含 Harmony 类型）
        // 的 try/catch 就能把它收敛成一行错误 —— 而不是让 /hook 崩掉或抛到指令层。

        private static class HarmonyBridge
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static string Version()
            {
                return typeof(HarmonyLib.Harmony).Assembly.GetName().Version.ToString();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static List<PatchEntry> Snapshot()
            {
                var list = new List<PatchEntry>();
                foreach (MethodBase method in HarmonyLib.Harmony.GetAllPatchedMethods())
                {
                    if (method == null) continue;
                    HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(method);
                    Collect(list, method, info);
                }
                return list;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static PatchesView GetPatches(MethodBase method)
            {
                if (method == null) return null;
                HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(method);
                if (info == null) return null;
                var view = new PatchesView();
                Collect(view.Patches, method, info);
                view.Prefixes = info.Prefixes.Count;
                view.Postfixes = info.Postfixes.Count;
                view.Transpilers = info.Transpilers.Count;
                view.Finalizers = info.Finalizers.Count;
                view.InnerPrefixes = info.InnerPrefixes.Count;
                view.InnerPostfixes = info.InnerPostfixes.Count;
                // 3.0.0 才有 InnerFinalizers（2.4.1 没有这个成员 ⇒ 编译期引用会 CS0117，故走反射；缺就 0）。
                view.InnerFinalizers = CountOptional(info, "InnerFinalizers");
                return view;
            }

            /// <summary>
            /// 按 owner 精确卸。**必须传 owner**：UnpatchAll(string harmonyID = null) 在 harmonyID == null 时
            /// 两个版本都按“全进程所有 owner”卸（2.4.1 IDCheck 恒真；3.0.0 用 "*" 通配 owner，见文件头 1）。
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static void UnpatchOwner(string owner)
            {
                new HarmonyLib.Harmony(owner).UnpatchAll(owner);
            }

            private static void Collect(List<PatchEntry> into, MethodBase method, HarmonyLib.Patches info)
            {
                if (info == null) return;
                AddKind(into, method, info.Prefixes, "prefix");
                AddKind(into, method, info.Postfixes, "postfix");
                AddKind(into, method, info.Transpilers, "transpiler");
                AddKind(into, method, info.Finalizers, "finalizer");
                AddKind(into, method, info.InnerPrefixes, "innerPrefix");
                AddKind(into, method, info.InnerPostfixes, "innerPostfix");
                CollectOptional(into, method, info);
            }

            private static void AddKind(List<PatchEntry> into, MethodBase method,
                IEnumerable<HarmonyLib.Patch> patches, string kind)
            {
                if (patches == null) return;
                foreach (HarmonyLib.Patch patch in patches)
                {
                    if (patch == null) continue;
                    into.Add(Make(method, patch, kind));
                }
            }

            /// <summary>
            /// 只在运行期真身（3.0.0）才存在的集合（编译面 2.4.1 没有这些成员 ⇒ 不能编译期引用，走反射）。
            /// 名字写死在这里；缺就跳过（不抛）—— 于是同一份代码在 2.4.1 / 3.0.0 上都正确。
            /// </summary>
            private static readonly string[] OptionalCollections = new string[] { "InnerFinalizers" };

            /// <summary>补第 7 类（InnerFinalizers）：只挂它（没有 prefix/postfix）的方法否则会从 list 漏掉、被 check 误报“未打补丁”。</summary>
            private static void CollectOptional(List<PatchEntry> into, MethodBase method, HarmonyLib.Patches info)
            {
                foreach (string name in OptionalCollections)
                {
                    string error;
                    List<object> items = ReadOptionalCollection(info, name, out error);
                    if (items == null) continue;               // 该版本没有这个集合 → 跳过
                    foreach (object item in items)
                    {
                        HarmonyLib.Patch patch = item as HarmonyLib.Patch;
                        if (patch != null) into.Add(Make(method, patch, KindOfOptional(name)));
                    }
                }
            }

            private static int CountOptional(HarmonyLib.Patches info, string name)
            {
                string error;
                List<object> items = ReadOptionalCollection(info, name, out error);
                return items == null ? 0 : items.Count;
            }

            private static string KindOfOptional(string collectionName)
            {
                if (collectionName == "InnerFinalizers") return "innerFinalizer";
                string n = collectionName.Length > 0 ? collectionName.Substring(0, 1).ToLowerInvariant() + collectionName.Substring(1) : collectionName;
                return n ?? "unknown";
            }

            private static PatchEntry Make(MethodBase method, HarmonyLib.Patch patch, string kind)
            {
                return new PatchEntry
                {
                    Method = method,
                    Target = DescribeMethod(method),
                    TypeName = SafeTypeName(method),
                    AssemblyName = SafeAssemblyName(method),
                    MetadataToken = SafeToken(method),
                    Owner = patch.owner,
                    PatchKind = kind,
                    Priority = patch.priority,
                    Index = patch.index,
                    PatchMethod = PatchMethodText(patch)
                };
            }

            private static string PatchMethodText(HarmonyLib.Patch patch)
            {
                try
                {
                    MethodInfo mi = patch.PatchMethod;
                    return mi == null ? null : DescribeMethod(mi);
                }
                catch (Exception ex)
                {
                    return "<取不到: " + Describe(ex) + ">";
                }
            }
        }

        // ==================== 小工具 ====================

        /// <summary>
        /// 反射读一个“运行期才有”的集合字段/属性（如 3.0.0 的 <c>Patches.InnerFinalizers</c>）。
        /// 该版本没有这个成员 → 返回 null（不是错误）；读取抛 → 返回 null 并写 error。**绝不抛**。
        /// 单独抽成 BCL-only 方法，好让“没有该成员就跳过”这条守卫能单测（合成对象即可）。
        /// </summary>
        internal static List<object> ReadOptionalCollection(object owner, string name, out string error)
        {
            error = null;
            if (owner == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                Type type = owner.GetType();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                object value = null;
                FieldInfo field = type.GetField(name, flags);
                if (field != null) value = field.GetValue(owner);
                else
                {
                    PropertyInfo prop = type.GetProperty(name, flags);
                    if (prop != null && prop.GetIndexParameters().Length == 0) value = prop.GetValue(owner, null);
                }
                if (value == null) return null;                   // 该版本没有这个成员

                IEnumerable sequence = value as IEnumerable;
                if (sequence == null)
                {
                    error = name + " 不是可枚举集合（" + value.GetType().Name + "）";
                    return null;
                }
                var items = new List<object>();
                foreach (object item in sequence) items.Add(item);
                return items;
            }
            catch (Exception ex)
            {
                error = "读可选集合 " + name + " 失败: " + Describe(ex);
                return null;
            }
        }

        internal static string DescribeMethod(MethodBase m)
        {
            if (m == null) return "?";
            string type = SafeTypeName(m) ?? "<dynamic>";
            var ps = new List<string>();
            try
            {
                foreach (ParameterInfo p in m.GetParameters()) ps.Add(p.ParameterType.Name);
            }
            catch (Exception) { }
            string args = string.Join(", ", ps);
            ConstructorInfo ctor = m as ConstructorInfo;
            if (ctor != null)
                return (m.IsStatic ? "static " : "") + type + "(" + args + ")";
            return type + "." + m.Name + "(" + args + ")";
        }

        private static int ParameterCount(MethodBase m)
        {
            try { return m.GetParameters().Length; }
            catch (Exception) { return 0; }
        }

        private static string SafeTypeName(MethodBase m)
        {
            if (m == null) return null;
            try { return m.DeclaringType == null ? null : m.DeclaringType.FullName; }
            catch (Exception) { return null; }
        }

        private static string SafeAssemblyName(MethodBase m)
        {
            if (m == null) return null;
            return SafeAssemblyName(m.DeclaringType);
        }

        private static string SafeAssemblyName(Type t)
        {
            if (t == null) return null;
            Assembly a = SafeAssembly(t);
            return a == null ? null : SafeStr(delegate { return a.GetName().Name; });
        }

        private static Assembly SafeAssembly(Type t)
        {
            if (t == null) return null;
            try { return t.Assembly; }
            catch (Exception) { return null; }
        }

        private static int SafeToken(MethodBase m)
        {
            if (m == null) return 0;
            try { return m.MetadataToken; }
            catch (Exception) { return 0; }
        }

        private static string SafeStr(Func<string> f)
        {
            try { return f(); }
            catch (Exception) { return null; }
        }

        private static bool TryBridge<T>(Func<T> call, out T value, out string error)
        {
            value = default(T);
            error = null;
            try { value = call(); return true; }
            catch (Exception ex) { error = Describe(ex); return false; }
        }

        private static string TryBridgeAction(Action call)
        {
            try { call(); return null; }
            catch (Exception ex) { return Describe(ex); }
        }

        private static int IndexOfWhitespace(string text)
        {
            if (text == null) return -1;
            for (int i = 0; i < text.Length; i++) if (char.IsWhiteSpace(text[i])) return i;
            return -1;
        }

        /// <summary>极简通配（* 任意串、? 单字符），用于程序集名前缀。</summary>
        internal static bool WildcardMatch(string pattern, string value)
        {
            if (pattern == null) return true;
            if (value == null) value = "";
            int p = 0, v = 0, star = -1, mark = 0;
            while (v < value.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[v])) { p++; v++; }
                else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = v; }
                else if (star >= 0) { p = star + 1; v = ++mark; }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        private static string AssemblySuffix(string pattern)
        {
            return string.IsNullOrEmpty(pattern) ? "" : "（程序集过滤 " + pattern + "）";
        }

        private static List<string> Cap(List<string> list, int max)
        {
            if (list == null) return new List<string>();
            if (list.Count <= max) return list;
            var capped = list.GetRange(0, max);
            capped.Add("…还有 " + (list.Count - max) + " 条");
            return capped;
        }

        private static string CaseInsensitiveOwnerHint(List<PatchEntry> entries, string owner)
        {
            var names = new List<string>();
            foreach (PatchEntry e in entries)
            {
                if (e.Owner == null) continue;
                if (!string.Equals(e.Owner, owner, StringComparison.OrdinalIgnoreCase)) continue;
                if (!names.Contains(e.Owner)) names.Add(e.Owner);
            }
            names.Sort(StringComparer.Ordinal);
            return names.Count == 0 ? null : names[0];
        }

        private static SlashResult Fail(string message, Dictionary<string, object> extra)
        {
            SlashResult r = SlashResults.Error(Kind, message);
            if (extra != null)
            {
                foreach (KeyValuePair<string, object> pair in extra)
                {
                    if (pair.Key == "ok" || pair.Key == "error") continue;   // 不覆盖 SlashResults.Error 的形状
                    r.Data[pair.Key] = pair.Value;
                }
            }
            return r;
        }

        private static Dictionary<string, object> Data(string key, object value)
        {
            return new Dictionary<string, object> { [key] = value };
        }

        private static string Describe(Exception ex)
        {
            Exception inner = (ex is TargetInvocationException && ex.InnerException != null) ? ex.InnerException : ex;
            return inner.GetType().Name + ": " + inner.Message;
        }

        private static string ShowValue(object value)
        {
            if (value == null) return "null";
            if (value is bool) return ((bool)value) ? "true" : "false";
            IFormattable f = value as IFormattable;
            return f != null ? f.ToString(null, CultureInfo.InvariantCulture) : value.ToString();
        }

        // ==================== 内部类型 ====================

        /// <summary>解析后的请求（内核半与 provider 半共用）。</summary>
        internal sealed class Request
        {
            public string Mode;
            public int Limit = DefaultListLimit;
            public string Target;
            public string Owner;
        }

        /// <summary>check 的目标（字符串解析结果，尚未解析成 Type/MethodBase）。</summary>
        public sealed class TargetSpec
        {
            public string Raw;
            public string AssemblyPattern;
            public string TypeText;
            public string MemberText;
            public List<string> Parameters = new List<string>();
        }

        /// <summary>check 的目标解析结果。</summary>
        public sealed class ResolvedTarget
        {
            public TargetSpec Spec;
            public Type Type;
            public Assembly Assembly;
            public bool TypeOnly;
            public bool Truncated;
            public List<MethodBase> Methods = new List<MethodBase>();
            public List<string> Notes = new List<string>();
        }

        /// <summary>一条补丁（一个 (目标, owner, kind) 三元组）。</summary>
        public sealed class PatchEntry
        {
            public MethodBase Method;
            public string Target;
            public string TypeName;
            public string AssemblyName;
            public int MetadataToken;
            public string Owner;
            public string PatchKind;
            public int Priority;
            public int Index;
            public string PatchMethod;
        }

        /// <summary>一个方法上的全部补丁。</summary>
        public sealed class PatchesView
        {
            public List<PatchEntry> Patches = new List<PatchEntry>();
            public int Prefixes;
            public int Postfixes;
            public int Transpilers;
            public int Finalizers;
            public int InnerPrefixes;
            public int InnerPostfixes;
            public int InnerFinalizers;
            public int Layers
            {
                get
                {
                    return Prefixes + Postfixes + Transpilers + Finalizers
                        + InnerPrefixes + InnerPostfixes + InnerFinalizers;
                }
            }
        }

        /// <summary>check 里一个方法的完整视图（补丁层 + ReJIT 判定）。</summary>
        public sealed class MethodCheck
        {
            public MethodBase Method;
            public string MethodText;
            public string TypeName;
            public string AssemblyName;
            public int MetadataToken;
            public List<PatchEntry> Patches = new List<PatchEntry>();
            public int Prefixes;
            public int Postfixes;
            public int Transpilers;
            public int Finalizers;
            public int InnerPrefixes;
            public int InnerPostfixes;
            public int InnerFinalizers;
            public string PatchInfoError;
            public Dictionary<string, object> Rejit;
            public int Layers
            {
                get
                {
                    return Prefixes + Postfixes + Transpilers + Finalizers
                        + InnerPrefixes + InnerPostfixes + InnerFinalizers;
                }
            }
        }
    }
}
