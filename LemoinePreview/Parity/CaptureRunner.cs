using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using LemoineTools.Framework;

namespace LemoineTools.Preview.Parity
{
    /// <summary>
    /// Drives <c>LemoinePreview.exe --capture</c>: renders each registered surface
    /// off-screen (no click-through needed) and writes its parity snapshot via
    /// <see cref="SnapshotExporter"/>. See plan-design-twin-parity-and-webview2-overview.md
    /// Part A3 for the full harness design.
    ///
    /// Default matrix is Dark Mono + Light Clean x Medium (fast, matches the design
    /// twin's baseline theme/size); pass --full for all 8 themes x 3 sizes
    /// (ExtraLarge is intentionally excluded even in --full — it has no twin
    /// counterpart yet and would only pad capture time).
    /// </summary>
    internal static class CaptureRunner
    {
        public static int Run(string[] args)
        {
            bool full = args.Contains("--full");
            string outDir = args.FirstOrDefault(a => !a.StartsWith("--") && a != "--capture")
                             ?? Path.Combine(FindRepoRoot(), "devtools", "design-twin", "snapshots", "wpf");
            Directory.CreateDirectory(outDir);

            var themes = full ? ThemePalette.All : new[] { ThemePalette.DarkMono, ThemePalette.LightClean };
            var sizes  = full ? new[] { UiSize.Small, UiSize.Medium, UiSize.Large } : new[] { UiSize.Medium };

            int captured = 0;
            foreach (var theme in themes)
            {
                foreach (var size in sizes)
                {
                    AppSettings.Instance.SetTheme(theme);
                    AppSettings.Instance.SetUiSize(size);
                    captured += CaptureToolsOverview(outDir, ToKebab(theme.Name), size.ToString().ToLowerInvariant());
                }
            }

            Console.WriteLine($"CaptureRunner: wrote {captured} snapshot(s) to {outDir}");
            return captured;
        }

        private static int CaptureToolsOverview(string outDir, string themeId, string sizeId)
        {
            var window = new ToolsOverviewWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
            };

            window.Show();
            PumpDispatcher();

            int count = 0;
            foreach (var cat in ToolsOverviewCatalog.Categories)
            {
                window.SelectCategory(cat.Id);
                PumpDispatcher();

                string surfaceId = $"tools-overview.{cat.Id}.{themeId}.{sizeId}";
                SnapshotExporter.Capture(window, surfaceId, outDir);
                count++;
            }

            window.Close();
            PumpDispatcher();
            return count;
        }

        /// <summary>
        /// Runs a nested dispatcher loop until the queue drains to Background priority —
        /// the standard WPF "DoEvents" pattern. Needed because capture runs fully
        /// synchronously inside App.OnStartup, before Application.Run's own dispatcher
        /// loop has started, so Show()'d windows won't otherwise get a Loaded/layout/
        /// render pass without pumping them here.
        /// </summary>
        private static void PumpDispatcher()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        private static string ToKebab(string displayName) =>
            displayName.ToLowerInvariant().Replace(" ", "-");

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LemoineTools.sln")))
                dir = dir.Parent;
            if (dir == null)
                throw new InvalidOperationException("CaptureRunner: could not locate repo root (LemoineTools.sln not found above " + AppContext.BaseDirectory + ")");
            return dir.FullName;
        }
    }
}
