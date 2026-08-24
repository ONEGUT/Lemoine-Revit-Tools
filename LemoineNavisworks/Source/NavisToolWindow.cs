using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
using LemoineTools.Framework;
using NavisApp = Autodesk.Navisworks.Api.Application;

namespace LemoineNavisworks
{
    // =========================================================================
    // NavisToolWindow — shared launcher for any Lemoine tool inside Navisworks.
    //
    // One live window per tool type (re-activates instead of stacking copies),
    // owned to the Navisworks main window so it does not fall behind on Alt+Tab.
    //
    // EACH WINDOW RUNS ON ITS OWN DEDICATED STA THREAD with its own Dispatcher.Run()
    // pump — the same architecture every Revit tool command in this repo uses.
    // An earlier version showed the window on Navisworks' own thread and let the
    // host's message loop pump it. That looked simpler and rendered fine, but NO
    // TEXT INPUT WORKED ANYWHERE in the window: a box could be focused and its
    // caret shown, yet keystrokes never arrived. Navisworks pre-processes keyboard
    // messages for its own single-key shortcuts before WPF sees them, so WM_KEYDOWN
    // was consumed by the host. A private message pump on a private thread has no
    // such filtering, which is exactly why the Revit side has always done it.
    //
    // The cost is that window code is no longer on the thread the Navisworks API may
    // be used from — every document read/write marshals through NavisMainThread.
    // =========================================================================
    internal static class NavisToolWindow
    {
        private static readonly Dictionary<Type, StepFlowWindow> _open =
            new Dictionary<Type, StepFlowWindow>();

        private static bool _bootstrapped;

        /// <summary>
        /// Host startup that Revit does in App.OnStartup and this add-in has no equivalent for.
        /// Runs once, before the first window is built.
        ///
        /// StepFlowWindow applies the theme and control styles itself, so only two things are
        /// missing here:
        ///   • AppStrings — without the load, every AppStrings.T() falls back to the key literal
        ///     and the UI renders as "navis.levelModels.title" instead of "Level Models".
        ///   • ToolReloadBridge — Revit installs a marshaller that hops the rebuild onto its main
        ///     thread via an ExternalEvent. Here the equivalent hop is NavisMainThread, because the
        ///     factory re-reads the document and the window now lives on its own thread.
        ///
        /// MUST run on Navisworks' main thread — NavisMainThread.Capture() records that thread's
        /// dispatcher, and everything the tools do to the document is marshalled back to it.
        /// </summary>
        private static void EnsureBootstrapped()
        {
            if (_bootstrapped) return;
            _bootstrapped = true;

            NavisMainThread.Capture();

            try { AppStrings.Load(AppSettings.Instance.Language); }
            catch (Exception ex)
            {
                // Not fatal: lookups fall back to English and then to the key literal, which is
                // visible in the UI — but it must be traceable rather than a mystery.
                DiagnosticsLog.Error("NavisToolWindow: load AppStrings", ex);
            }

            try { LegacyFileCleanup.RunOnce(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("NavisToolWindow: legacy file cleanup", ex); }

            // The factory re-reads the document, so it has to run on the main thread; onBuilt is
            // safe from any thread (StepFlowWindow marshals it onto the window's own dispatcher).
            ToolReloadBridge.Marshal = (factory, onBuilt) => NavisMainThread.Post(() =>
            {
                IStepFlowTool? rebuilt = null;
                try { rebuilt = factory(); }
                catch (Exception ex)
                {
                    // The bridge's contract: the host logs WHY before handing back null, so the
                    // window can report "reload failed" without swallowing the cause.
                    DiagnosticsLog.Error("NavisToolWindow: rebuild tool for reload", ex);
                }
                onBuilt(rebuilt);
            });
        }

        /// <summary>
        /// Opens (or re-activates) the window for this tool on its own STA thread.
        /// Call from Navisworks' main thread — i.e. from AddInPlugin.Execute.
        ///
        /// The tool instance is built by the CALLER, on the main thread, so its initial document
        /// capture is a legal API read. Nothing this method does afterwards may touch the API:
        /// it blocks the main thread until the window is shown, so a marshalled call from the
        /// window's construction path would deadlock against that wait.
        /// </summary>
        public static void Open(IStepFlowTool tool)
        {
            EnsureBootstrapped();
            var key = tool.GetType();

            if (_open.TryGetValue(key, out var existing) && existing != null)
            {
                try
                {
                    // The window lives on another thread now — Activate must be marshalled onto
                    // its dispatcher, not called across threads.
                    existing.Dispatcher.Invoke(() =>
                    {
                        if (existing.IsVisible) existing.Activate();
                    });
                    return;
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("NavisToolWindow: activate existing", ex);
                    _open.Remove(key);
                }
            }

            // Grab the owner HWND here, on the main thread; NavisApp.Gui is API surface and must
            // not be read from the window's thread.
            IntPtr owner = IntPtr.Zero;
            try { owner = NavisApp.Gui.MainWindow.Handle; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("NavisToolWindow: read owner handle", ex); }

            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
            {
                try
                {
                    win = new StepFlowWindow(tool);
                    win.Closed += (s, e) =>
                    {
                        _open.Remove(key);
                        Dispatcher.CurrentDispatcher.InvokeShutdown();
                    };

                    // Owned to Navisworks' main window so it does not fall behind on Alt+Tab.
                    // Cross-thread ownership of an HWND is fine — this is a Win32 relationship,
                    // not a WPF one.
                    if (owner != IntPtr.Zero)
                    {
                        try { new WindowInteropHelper(win) { Owner = owner }; }
                        catch (Exception ex) { DiagnosticsLog.Swallowed("NavisToolWindow: set window owner", ex); }
                    }

                    win.Show();
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("NavisToolWindow: build window", ex);
                }
                finally
                {
                    // Released even on failure, or Execute would block forever on a window that
                    // is never coming.
                    ready.Set();
                }

                if (win != null) Dispatcher.Run();   // private pump — this is what makes typing work
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            ready.Wait();
            if (win != null) _open[key] = win;
        }
    }
}
