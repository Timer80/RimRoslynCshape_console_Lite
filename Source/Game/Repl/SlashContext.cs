// RoslynProbe —— 姿势 B 回调包：内核零 Verse 依赖，游戏侧能力由 Bootstrap 注入委托。
//
// 为什么有这一层（docs/slash-command-authoring.md §4 姿势 B，TOOLSTACK §2.2 前置 2）：
// /dev /debug-action /hook /prof 这类指令要碰 Verse / DefDatabase / Harmony，而内核必须保持
// 零 Verse 依赖。做法是：Bootstrap 在启动时把"能力"以一个委托注入本包，指令 handler 只调委托；
// provider 本体写在各自指令文件（Cmds/*.cs，与内核同程序集，可以 using Verse）。
//
// 冻结声明：SlashRegistry.cs 与本文件是给 13 个并行写手消费的共享 API，T-S0 交付后冻结。
// 写手需要新委托/新字段 → 报告控制者，不要自行修改。
//
// 未注册语义：每个能力还有一个 CallXxx(args) 访问器。provider 为 null 时**不抛异常**，
// 返回标准"能力未注册"行（SlashResults.CapabilityMissing）。调用点统一写
//     return ctx.CallDev(rest);
// 于是"忘了注册"在三个通道都表现为一行响亮的自解释错误，而不是 NullReferenceException。
//
// 线程契约：本包自身无状态、无锁。GUI 壳在游戏线程调用；stdio/管道壳在读线程调用 ——
// provider 里任何碰 ConsoleLog / Unity / Verse 的动作必须自己 executor.Enqueue marshal
// （坑见 docs/slash-command-authoring.md §6）。

using System;
using System.Collections.Generic;
using RoslynProbe.Repl;

namespace RoslynProbe
{
    /// <summary>
    /// 一次 Slash 派发的上下文：壳层引用（Host/Executor/Channel/IsGameThread）+ 8 个可空能力委托。
    /// 由壳层/Bootstrap 构造一次长期持有（也可每行新建，本类无状态）。
    /// </summary>
    public sealed class SlashContext
    {
        /// <summary>调用壳的通道名："gui" / "stdio" / "pipe"；未接线为 null。可选（/status 自报通道用）。</summary>
        public string Channel;

        /// <summary>REPL 门面（/usings、/status 直接读它；可为 null）。</summary>
        public ReplHost Host;

        /// <summary>主线程执行队列（需要 marshal 的 provider 里面用；可为 null）。</summary>
        public Executor Executor;

        /// <summary>
        /// **调用本次 Slash 的线程是否游戏线程**；壳层必须填。拿不准就填 <c>false</c>
        /// —— 宁可多一次 marshal，也不要裸读 Unity/Verse 对象。
        /// <para>壳层填法：<c>gui</c> 壳 → ctx.IsGameThread = true；<c>stdio</c> / 命名管道壳 →
        /// 由壳在游戏线程内判定（壳层本就引用 Assembly-CSharp，可调 <c>UnityData.IsInMainThread</c>；<c>UnityData</c> 来自 Assembly-CSharp、不是编译期桩层 UnityStubs）
        /// 或直接 false（会话线程）。</para>
        /// <para>provider 据此决定内联还是 marshal：
        /// <c>if (ctx.IsGameThread) Inline(); else ctx.Executor.Enqueue(...);</c></para>
        /// <para>契约 v1.1（2026-10-15 增补）：曾有四个写手各造启发式
        /// （Channel=="gui" 否则 UnityData.IsInMainThread / RouteFor(onGameThread, hasExecutor) / 只用 Channel），
        /// 由本普通 bool 事实位统一收口。零 Verse/Unity 依赖（内核必须能脱离游戏编译/测试）。</para>
        /// </summary>
        public bool IsGameThread;

        /// <summary>委托签名：<c>(rest, ctx) =&gt; SlashResult</c>。rest 已 Trim；ctx 必非 null。</summary>
        // ---- 姿势 B 能力（Bootstrap 注入；未注入 = null） ----

        /// <summary>/status 的只读自检 provider。</summary>
        public Func<string, SlashContext, SlashResult> Status;

        /// <summary>/log 的日志读取 provider（LogSnapshot 侧）。</summary>
        public Func<string, SlashContext, SlashResult> Log;

        /// <summary>/dev 的调试设置类 provider（字段列/置位/清位/翻转）。</summary>
        public Func<string, SlashContext, SlashResult> Dev;

        /// <summary>/debug-action 的调试动作树 provider。</summary>
        public Func<string, SlashContext, SlashResult> DebugAction;

        /// <summary>/hook 的 Harmony provider（list / check / off）。</summary>
        public Func<string, SlashContext, SlashResult> Hook;

        /// <summary>/prof 的剖析只读 provider。</summary>
        public Func<string, SlashContext, SlashResult> Prof;

        /// <summary>/usings add 的 using 追加 provider（路径与 HTTP /using 同源）。</summary>
        public Func<string, SlashContext, SlashResult> UsingAdd;

        /// <summary>/ui open 的窗口打开 provider。</summary>
        public Func<string, SlashContext, SlashResult> UiOpen;

        // ---- 调用入口（provider 为 null 时返回"能力未注册"行，绝不抛） ----

        public SlashResult CallStatus(string args) { return Invoke(Status, "status", args); }
        public SlashResult CallLog(string args) { return Invoke(Log, "log", args); }
        public SlashResult CallDev(string args) { return Invoke(Dev, "dev", args); }
        public SlashResult CallDebugAction(string args) { return Invoke(DebugAction, "debug-action", args); }
        public SlashResult CallHook(string args) { return Invoke(Hook, "hook", args); }
        public SlashResult CallProf(string args) { return Invoke(Prof, "prof", args); }
        public SlashResult CallUsingAdd(string args) { return Invoke(UsingAdd, "usings-add", args); }
        public SlashResult CallUiOpen(string args) { return Invoke(UiOpen, "ui-open", args); }

        /// <summary>
        /// 统一调用口：null → 能力未注册行；provider 抛异常 → 错误行（指令层永不抛）；
        /// provider 返回 null → 错误行。
        /// </summary>
        public SlashResult Invoke(Func<string, SlashContext, SlashResult> capability, string capabilityName, string args)
        {
            if (capability == null) return SlashResults.CapabilityMissing(capabilityName);
            SlashResult r;
            try
            {
                r = capability(args ?? "", this);
            }
            catch (Exception ex)
            {
                r = SlashResults.Error("capability-error",
                    "能力 " + capabilityName + " 抛出异常: " + ex.GetType().Name + ": " + ex.Message);
                return r;
            }
            if (r == null)
                return SlashResults.Error("capability-error", "能力 " + capabilityName + " 返回 null");
            return r;
        }

        /// <summary>已注入的能力名（供 /status 或测试显示；顺序固定）。</summary>
        public List<string> RegisteredCapabilities()
        {
            var list = new List<string>();
            if (Status != null) list.Add("status");
            if (Log != null) list.Add("log");
            if (Dev != null) list.Add("dev");
            if (DebugAction != null) list.Add("debug-action");
            if (Hook != null) list.Add("hook");
            if (Prof != null) list.Add("prof");
            if (UsingAdd != null) list.Add("usings-add");
            if (UiOpen != null) list.Add("ui-open");
            return list;
        }
    }
}
