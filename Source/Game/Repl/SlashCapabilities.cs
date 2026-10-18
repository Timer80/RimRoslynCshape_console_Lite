// RoslynProbe —— 姿势 B 能力的接线寄存点（集成 T-S11/S11-3）。
//
// 为什么单独一个文件：SlashContext 的 8 个能力委托必须由 Bootstrap 在**游戏线程**注入
// （[StaticConstructorOnStartup] —— /debug-action 的 mainThreadId 与 /threads 的注册期线程捕获都依赖它），
// 而每个通道的每次派发都要一个带**本次事实位**（Channel / IsGameThread / Host / Executor）的上下文。
// 故：Bootstrap 注入一个**模板**（只有 8 个委托）→ 本类为每次派发复制出真上下文。
// 这样模板永不携带通道事实位，通道之间也不会互相看见对方的 Host/Executor（无共享可变状态）。
//
// 本文件零 Unity/Verse 依赖（只碰 SlashContext / ReplHost / Executor 这些已有的零 Verse 类型）。

using System;
using System.Collections.Generic;
using RoslynProbe.Repl;

namespace RoslynProbe
{
    /// <summary>姿势 B 能力的寄存点：Bootstrap 注入模板，壳层经 NewContext 取每次派发的上下文。</summary>
    public static class SlashCapabilities
    {
        private static SlashContext template;

        /// <summary>
        /// Bootstrap 在游戏线程调用：寄存已注入 8 个 provider 的模板上下文（其 Channel/Host/Executor 不参与复制）。
        /// </summary>
        public static void Install(SlashContext capabilities)
        {
            template = capabilities;
        }

        /// <summary>已注入的能力名（未 Install 时为空表；供 /status 或测试显示）。</summary>
        public static List<string> Registered()
        {
            SlashContext t = template;
            return t == null ? new List<string>() : t.RegisteredCapabilities();
        }

        /// <summary>
        /// 为一次派发构造上下文：事实位由壳层给（channel/isGameThread/host/executor），
        /// 8 个能力委托从模板**逐字复制**（模板未装 ⇒ 全 null ⇒ 各 CallXxx 回 missing-capability 行）。
        /// </summary>
        public static SlashContext NewContext(ReplHost host, Executor executor, string channel, bool isGameThread)
        {
            var c = new SlashContext
            {
                Host = host,
                Executor = executor,
                Channel = channel,
                IsGameThread = isGameThread
            };

            SlashContext t = template;
            if (t != null)
            {
                c.Status = t.Status;
                c.Log = t.Log;
                c.Dev = t.Dev;
                c.DebugAction = t.DebugAction;
                c.Hook = t.Hook;
                c.Prof = t.Prof;
                c.UsingAdd = t.UsingAdd;
                c.UiOpen = t.UiOpen;
            }
            return c;
        }

        /// <summary>测试隔离专用：清掉模板（运行时不需要）。</summary>
        public static void ResetForTests()
        {
            template = null;
        }
    }
}
