using System;
using System.Collections.Generic;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Opens (or re-activates) one <see cref="WebStepFlowWindow"/> per tool key on the shared
    /// <see cref="WebUiThread"/>. Replaces the per-command static-window boilerplate: a command's
    /// web branch is one call. The factory runs on the calling (Revit main) thread so it can carry
    /// main-thread-collected data; window construction happens on the web-UI thread.
    /// </summary>
    public static class WebToolLauncher
    {
        private static readonly Dictionary<string, WebStepFlowWindow> _windows
            = new Dictionary<string, WebStepFlowWindow>(StringComparer.Ordinal);
        private static readonly object _gate = new object();

        /// <summary>True when the machine-wide Web UI flag is ON — commands branch on this.</summary>
        public static bool Enabled => WebUiSettings.Instance.Enabled;

        public static void Open(string key, Func<IWebTool> factory)
        {
            // Re-activate an existing window for this tool if it is still alive.
            WebStepFlowWindow? existing;
            lock (_gate) { _windows.TryGetValue(key, out existing); }
            if (existing != null)
            {
                bool activated = false;
                try
                {
                    WebUiThread.Invoke(() =>
                    {
                        if (existing.IsVisible) { existing.Activate(); activated = true; }
                    });
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"WebToolLauncher: activate '{key}'", ex); }
                if (activated) return;
                lock (_gate) { _windows.Remove(key); }
            }

            var tool = factory(); // main thread — may carry Revit-collected data
            WebUiThread.Invoke(() =>
            {
                var win = new WebStepFlowWindow(tool);
                win.Closed += (s, e) => { lock (_gate) { _windows.Remove(key); } };
                win.Show();
                lock (_gate) { _windows[key] = win; }
            });
        }
    }
}
