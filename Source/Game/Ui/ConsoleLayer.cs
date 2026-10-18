using System;
using UnityEngine;
using Verse;
using RoslynProbe.Repl;

namespace RoslynProbe
{
    /// <summary>
    /// Resident MonoBehaviour that owns the console's open/closed state and pushes the real
    /// Verse.Window into the WindowStack when opened.
    ///
    /// Why a WindowStack window and not a hand-rolled IMGUI overlay: the vanilla stack gives us
    /// the dark window background, the drop shadow, drag-by-title-bar, resize handle, ESC-to-close
    /// via KeyBindingDefOf.Cancel, and the input-gating that keeps keyboard focus inside a
    /// TextField. All of that is what makes it read as "a console" instead of a floating label.
    ///
    /// It also keeps the "usable from the main menu" property: WindowStackOnGUI runs for both
    /// UIRoot_Entry (main menu) and UIRoot_Play, and Window.PreOpen only reaches into
    /// Find.Selector / Find.WorldSelector when Current.ProgramState == Playing.
    /// </summary>
    public class ConsoleLayer : MonoBehaviour
    {
        private static ConsoleWindow instance;
        private static ReplHost host;
        private static Executor executor;

        /// <summary>Injected once by BootstrapBehaviour.Init before the console can be opened.</summary>
        public static void Wire(ReplHost replHost, Executor replExecutor)
        {
            host = replHost;
            executor = replExecutor;
        }

        public static bool IsOpen
        {
            get { return instance != null; }
        }

        public static void Open()
        {
            if (instance != null) return;
            if (host == null || executor == null)
            {
                Log.ErrorOnce("[Roslyn] 控制台未接线（ConsoleLayer.Wire 未调用）", "RoslynProbe.ConsoleNotWired".GetHashCode());
                return;
            }
            try
            {
                instance = new ConsoleWindow(host, executor);
                Find.WindowStack.Add(instance);
            }
            catch (Exception ex)
            {
                instance = null;
                Log.Error("[Roslyn] 打开控制台失败: " + ex);
            }
        }

        public static void Close()
        {
            try
            {
                if (instance != null)
                    instance.Close();
            }
            catch { }
            instance = null;
        }

        /// <summary>Called by ConsoleWindow when the stack removes it (ESC, close-X, outside click).</summary>
        public static void NotifyRemoved()
        {
            instance = null;
        }
    }
}
