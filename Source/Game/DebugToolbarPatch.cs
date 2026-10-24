using HarmonyLib;
using UnityEngine;
using Verse;

namespace RoslynProbe
{
    /// <summary>
    /// Appends a RoslynProbe toggle to the vanilla debug toolbar (the row of icon buttons that
    /// DrawButtons in Verse.DebugWindowsOpener renders when Prefs.DevMode is on).
    /// 
    /// Why a Postfix on DrawButtons: it is the delegate the toolbar window runs each frame, and it
    /// ends with widgetRowFinalX = widgetRow.FinalX, which sizes and centres the whole row. A
    /// Postfix therefore runs after every vanilla button has been laid out and after FinalX has been
    /// captured, so we can keep drawing on the SAME WidgetRow (continuing its x cursor past the last
    /// vanilla icon) and then push the enlarged width back into widgetRowFinalX so the row still
    /// centres correctly with our icon included.
    /// 
    /// DrawButtons, widgetRow and widgetRowFinalX are all private, hence AccessTools everywhere.
    /// </summary>
    [HarmonyPatch(typeof(Verse.DebugWindowsOpener), "DrawButtons")]
    public static class DebugToolbarPatch
    {
        private static void Postfix(object __instance)
        {
            try
            {
                // Loaded on the game's update thread (this is a GUI callback). No Texture2D field on
                // this type on purpose: Verse's StaticConstructorOnStartupUtility audits for assets
                // loaded off the main thread, and icons are fetched per frame via ContentFinder instead.
                var row = Traverse.Create(__instance).Field<WidgetRow>("widgetRow").Value;
                if (row == null) return;

                bool open = ConsoleLayer.IsOpen;
                // 不做静态纹理缓存：play data 重载（如切语言）走 LoadedModManager.ClearDestroy() 会销毁
                // 旧纹理，缓存会持有僵尸引用；ContentFinder.Get 是字典查找，每帧直取开销可忽略。
                Texture2D icon = Load(open ? "RoslynProbe/Cshape_console_Enabled" : "RoslynProbe/Cshape_console_Disabled");
                if (icon == null)
                {
                    // 瞬态 vs 配置错误：LanguageDatabase.SelectLanguage → PlayDataLoader.ClearAllPlayData →
                    // LoadedModManager.ClearDestroy() 再重建，重载窗口期 ContentFinder 必然查不到纹理
                    // （切语言时曾因此刷过一次本报错）。先静默容忍 IconFailGraceSeconds（本帧不画按钮），
                    // 持续仍失败才是真正的 Textures/LoadFolders 路径配置错误，才响亮报错一次。
                    if (iconFailSince < 0f) iconFailSince = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup - iconFailSince < IconFailGraceSeconds) return;
                    Verse.Log.ErrorOnce(
                        "[Roslyn] 工具栏图标加载失败（" + (open ? "Enabled" : "Disabled") + "）。" +
                        "请确认 Textures\\RoslynProbe\\Cshape_console_*.png 存在，且 LoadFolders.xml 已挂载该目录。",
                        "RoslynProbe.ToolbarIconMissing".GetHashCode());
                    return;
                }
                iconFailSince = -1f; // 恢复正常：清掉瞬态计时

                bool clicked = row.ButtonIcon(
                    icon,
                    ConsoleText.T("RoslynProbe.Toolbar.Tooltip", "Toggle the RoslynProbe console.\n\nA live C# REPL for this session."),
                    GenUI.MouseoverColor);

                if (clicked)
                {
                    if (open) ConsoleLayer.Close();
                    else ConsoleLayer.Open();
                }

                // Grow the row so the toolbar window still centres around the widened content.
                Traverse.Create(__instance).Field("widgetRowFinalX").SetValue(row.FinalX);
            }
            catch (System.Exception ex)
            {
                // Never let a toolbar patch break the vanilla UI.
                Verse.Log.ErrorOnce("[Roslyn] 调试工具栏 patch 失败: " + ex, "RoslynProbe.DebugToolbarPatch".GetHashCode());
            }
        }

        /// <summary>图标首次查不到的时刻（-1 = 当前正常）；只用于区分 play data 重载瞬态与真配置错误。</summary>
        private static float iconFailSince = -1f;

        /// <summary>重载窗口期容忍时长：play data 重载在长事件里跑完，通常远短于此。</summary>
        private const float IconFailGraceSeconds = 5f;

        private static Texture2D Load(string path)
        {
            // ContentFinder<Texture2D>.Get(reportFailure: false) returns null instead of erroring.
            return ContentFinder<Texture2D>.Get(path, false);
        }
    }
}
