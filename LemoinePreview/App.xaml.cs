using System.Windows;
using LemoineTools.Preview.Parity;

namespace LemoineTools.Preview
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // `LemoinePreview.exe --capture [outDir] [--full]` renders every registered
            // design-twin parity surface off-screen and exits — no window is ever shown,
            // no clicking required. See plan-design-twin-parity-and-webview2-overview.md
            // Part A3/A5 and devtools/design-twin/README.md for the full loop.
            if (e.Args.Length > 0 && e.Args[0] == "--capture")
            {
                int exitCode = 0;
                try
                {
                    CaptureRunner.Run(e.Args);
                }
                catch (System.Exception ex)
                {
                    System.Console.Error.WriteLine("CaptureRunner failed: " + ex);
                    exitCode = 1;
                }
                Shutdown(exitCode);
                return;
            }

            new PreviewMainWindow().Show();
        }
    }
}
