using System;
using System.Windows.Threading;
using LemoineTools.Framework;

namespace LemoineNavisworks
{
    // =========================================================================
    // NavisMainThread — marshals work onto the thread Navisworks calls plugins on.
    //
    // Tool windows now run on their OWN dedicated STA thread (see NavisToolWindow
    // for why), which means window code — button clicks, the run loop — is no
    // longer on the thread the Navisworks API may be touched from. Everything that
    // reads or writes the document has to hop back here first.
    //
    // The main thread's Dispatcher is captured during bootstrap, which runs inside
    // AddInPlugin.Execute and is therefore genuinely on Navisworks' main thread.
    // Navisworks' own UI is WPF (the ribbon), so that thread already owns a live,
    // pumping Dispatcher — posted work is picked up by the host's existing message
    // loop with nothing extra to install.
    //
    // Deadlock discipline: Post() is fire-and-forget and is what long work uses, so
    // the window thread never blocks on the main thread. Invoke() is synchronous and
    // carries a timeout for exactly that reason — a hang here would freeze
    // Navisworks itself, which is worse than the failure it is guarding.
    // =========================================================================
    internal static class NavisMainThread
    {
        private static Dispatcher? _main;

        /// <summary>Called once from bootstrap, on Navisworks' main thread.</summary>
        public static void Capture()
        {
            if (_main != null) return;
            try { _main = Dispatcher.CurrentDispatcher; }
            catch (Exception ex) { DiagnosticsLog.Error("NavisMainThread: capture dispatcher", ex); }
        }

        /// <summary>True when a main-thread dispatcher was captured and is still alive.</summary>
        public static bool IsAvailable =>
            _main != null && !_main.HasShutdownStarted && !_main.HasShutdownFinished;

        /// <summary>True when the caller is already ON the main thread.</summary>
        public static bool OnMainThread => _main != null && _main.CheckAccess();

        /// <summary>
        /// Queues <paramref name="work"/> on the main thread and returns immediately. Use this for
        /// anything long (an export run): the calling window thread stays free to paint and to
        /// take a Cancel click while the work proceeds.
        /// </summary>
        public static void Post(Action work)
        {
            if (work == null) return;

            if (!IsAvailable)
            {
                // No dispatcher — run inline rather than dropping the work silently. This is the
                // pre-STA-thread behaviour, so it is degraded but not broken.
                DiagnosticsLog.Warn("NavisMainThread", "no main dispatcher captured; running inline.");
                RunGuarded(work);
                return;
            }
            if (OnMainThread) { RunGuarded(work); return; }

            try { _main!.BeginInvoke(new Action(() => RunGuarded(work)), DispatcherPriority.Normal); }
            catch (Exception ex) { DiagnosticsLog.Error("NavisMainThread.Post", ex); }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the main thread and waits for its result. For SHORT
        /// reads only — a capture, a rescan. Returns <paramref name="fallback"/> and logs if the
        /// main thread does not answer within <paramref name="timeoutSeconds"/>.
        /// </summary>
        public static T Invoke<T>(Func<T> work, T fallback, int timeoutSeconds = 30)
        {
            if (work == null) return fallback;
            if (!IsAvailable || OnMainThread)
            {
                try { return work(); }
                catch (Exception ex) { DiagnosticsLog.Error("NavisMainThread.Invoke (inline)", ex); return fallback; }
            }

            try
            {
                return _main!.Invoke(work, DispatcherPriority.Normal,
                                     System.Threading.CancellationToken.None,
                                     TimeSpan.FromSeconds(timeoutSeconds));
            }
            catch (TimeoutException)
            {
                // Never rethrow into a click handler — an unhandled throw on the window's
                // dispatcher is a hard Revit/Navisworks-style crash (CLAUDE.md).
                DiagnosticsLog.Warn("NavisMainThread",
                    $"main thread did not respond within {timeoutSeconds}s; using fallback.");
                return fallback;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("NavisMainThread.Invoke", ex);
                return fallback;
            }
        }

        private static void RunGuarded(Action work)
        {
            try { work(); }
            catch (Exception ex) { DiagnosticsLog.Error("NavisMainThread: work threw", ex); }
        }
    }
}
