// RoslynProbe —— 强制 runInBackground：让窗口失焦不再停掉游戏主循环的 Update。
//
// == 为什么需要（Waylith.EngineApplication.RunLoop 反编译 + 实测，2026-10-26）==
// RunLoop 每帧开头判：
//     flag3 = !Window.IsFocused && !EngineWindow.RunInBackground && !Options.HideWindow
// flag3 为真时只 Glfw.WaitEventsTimeout(0.1) 然后 continue —— 跳过本帧剩下的全部，
// 其中就包括 "GameUpdate" 作用域里的 OnUpdate（即 Unity 的 Update）。
// BootstrapBehaviour.Update → Executor.Tick 因此不跑，管道里的求值永远排队，
// 客户端只能看到 15 秒超时。现场日志 "Focus regained after 2111.9 s idle" 之后
// 93 毫秒求值才执行，正是这条 continue 的直接证据；-NoWindow 因 Options.HideWindow
// 让 flag3 恒假，所以无窗实例一切正常 —— 反过来印证了这条路径。
//
// == 为什么不改原版设置 ==
// PrefsData.runInBackground 是玩家配置，改了会写进 Config/Prefs.xml 并持久化，
// 下次不带本 mod 启动仍是改过的值。本类只在**本次进程**把应用值钉在 true：
// 不写配置、不改玩家字段，普通玩家（零旗标启动）行为完全不变。
//
// == 拦截点（反编译核实）==
//   PrefsData.Apply() → Application.runInBackground = data.runInBackground
//     → UnityRuntime.Current.App.RunInBackground → EngineWindow.RunInBackground
// 因此拦 Application 的 setter 即可覆盖所有写入者（同 mute 拦 AudioListener.volume
// 的思路）；再钉住 Prefs.RunInBackground 的 getter，让读该属性的 UI / 其它代码看到「开」。

using System;
using HarmonyLib;
using Verse;

namespace RoslynProbe
{
    /// <summary>
    /// 本次进程是否把 runInBackground 强制为 true。只有带 -RoslynConsole 的 agent 通道
    /// （管道 v2 / stdio v1）才启用 —— 那正是「窗口在后台、agent 在跑命令」的场景。
    /// 零旗标启动（普通玩家）不启用，行为与原版一致。
    /// </summary>
    public static class RunInBackgroundForce
    {
        /// <summary>已启用（补丁的 Prefix/Postfix 只在这个开关为真时才改写值）。</summary>
        public static bool Active { get; private set; }

        /// <summary>启用并立即生效一次（Prefs.Apply 可能在本方法之前已经跑过）。</summary>
        public static void Enable()
        {
            Active = true;
            try
            {
                UnityEngine.Application.runInBackground = true;
                Log.Message("[Roslyn] 已强制 runInBackground=true（窗口失焦时 Update 不再停摆）");
            }
            catch (Exception ex)
            {
                Log.Error("[Roslyn] 强制 runInBackground 失败: " + ex);
            }
        }
    }

    /// <summary>Prefs.RunInBackground 的 getter 钉在 true（读该属性的 UI / 代码看到「开」）。</summary>
    [HarmonyPatch(typeof(Verse.Prefs), nameof(Verse.Prefs.RunInBackground), MethodType.Getter)]
    internal static class Patch_Prefs_RunInBackground_Getter
    {
        [HarmonyPostfix]
        private static void Postfix(ref bool __result)
        {
            if (RunInBackgroundForce.Active) __result = true;
        }
    }

    /// <summary>
    /// 所有写入 runInBackground 的总入口：把入参改成 true。
    /// 只改参数、不跳过原方法 —— 原 setter 还要负责把值送到 EngineWindow。
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.Application), nameof(UnityEngine.Application.runInBackground), MethodType.Setter)]
    internal static class Patch_Application_runInBackground_Setter
    {
        [HarmonyPrefix]
        private static void Prefix(ref bool value)
        {
            if (RunInBackgroundForce.Active) value = true;
        }
    }
}
