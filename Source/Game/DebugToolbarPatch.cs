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
                // Loaded on the game's update thread (this is a GUI callback) and cached in a
                // boxed holder, because a Texture2D field on this type would trip Verse's
                // StaticConstructorOnStartupUtility "assets must be loaded in the main thread" warning.
                var row = Traverse.Create(__instance).Field<WidgetRow>("widgetRow").Value;
                if (row == null) return;

                bool open = ConsoleLayer.IsOpen;
                if (Icons.On == null) Icons.On = Load("RoslynProbe/Cshape_console_Enabled");
                if (Icons.Off == null) Icons.Off = Load("RoslynProbe/Cshape_console_Disabled");
                Texture2D icon = (Texture2D)(open ? Icons.On : Icons.Off);
                if (icon == null)
                {
                    // Loud, once: a silent return here means the toolbar button just never shows up
                    // and nobody can tell why. Almost always a LoadFolders/Textures path problem.
                    Verse.Log.ErrorOnce(
                        "[Roslyn] 工具栏图标加载失败（" + (open ? "Enabled" : "Disabled") + "）。" +
                        "请确认 Textures\\RoslynProbe\\Cshape_console_*.png 存在，且 LoadFolders.xml 已挂载该目录。",
                        "RoslynProbe.ToolbarIconMissing".GetHashCode());
                    return;
                }

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

        /// <summary>
        /// Holds the two toolbar icons. Kept behind object refs (not Texture2D fields) so the
        /// patch type itself carries no asset-typed field and stays clear of the startup audit.
        /// </summary>
        private static class Icons
        {
            internal static object On;
            internal static object Off;
        }

        private static Texture2D Load(string path)
        {
            // ContentFinder<Texture2D>.Get(reportFailure: false) returns null instead of erroring.
            return ContentFinder<Texture2D>.Get(path, false);
        }
    }
}
