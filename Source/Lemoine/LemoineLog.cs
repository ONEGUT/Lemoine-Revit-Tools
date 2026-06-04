using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Central diagnostic sink for the whole plugin.
    ///
    /// Every <c>catch</c> that deliberately swallows an exception reports here so
    /// that no failure is ever truly silent. Entries are:
    ///   • appended to a durable log file at %AppData%\LemoineTools\diagnostics.log
    ///     (rolled when it grows past ~1 MB so it never grows unbounded), and
    ///   • kept in an in-memory ring of the most recent entries for in-app viewing, and
    ///   • forwarded to any live sink (e.g. the StepFlow Output Log of a running tool)
    ///     via <see cref="EntryLogged"/>.
    ///
    /// This type is intentionally Revit-free so it can be used from any layer
    /// (settings, controls, helpers) as well as from tool handlers.
    /// </summary>
    public static class LemoineLog
    {
        public enum Severity { Info, Warning, Error }

        public sealed class Entry
        {
            public DateTime Timestamp { get; }
            public Severity Severity  { get; }
            public string   Context   { get; }
            public string   Detail    { get; }

            public Entry(DateTime ts, Severity sev, string context, string detail)
            {
                Timestamp = ts;
                Severity  = sev;
                Context   = context;
                Detail    = detail;
            }

            public override string ToString() =>
                $"{Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  " +
                $"[{Severity.ToString().ToUpperInvariant()}]  {Context}" +
                (string.IsNullOrEmpty(Detail) ? "" : $"  —  {Detail}");
        }

        // ── State ─────────────────────────────────────────────────────────────
        private const int    RingCapacity   = 500;
        private const long   MaxFileBytes   = 1_000_000;   // ~1 MB before roll
        private static readonly object              _gate = new object();
        private static readonly Queue<Entry>        _ring = new Queue<Entry>(RingCapacity);
        private static long                         _issueCount;   // Warning + Error entries recorded

        /// <summary>
        /// Raised on the calling thread whenever an entry is recorded. Subscribers
        /// (e.g. an open StepFlow window) must unsubscribe when they close. Never
        /// throws back into the logger — handler exceptions are absorbed.
        /// </summary>
        public static event Action<Entry>? EntryLogged;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Record that an exception was deliberately swallowed. <paramref name="context"/>
        /// should be a short human phrase describing what was attempted, e.g.
        /// <c>"ApplyTemplate: view 'L2 Power'"</c>.
        /// </summary>
        public static void Swallowed(string context, Exception ex) =>
            Write(Severity.Warning, context, Describe(ex));

        /// <summary>Record a non-exception warning (e.g. a failure signalled by a return value).</summary>
        public static void Warn(string context, string detail) =>
            Write(Severity.Warning, context, detail ?? "");

        /// <summary>Record an error that is being logged-and-handled rather than rethrown.</summary>
        public static void Error(string context, Exception ex) =>
            Write(Severity.Error, context, Describe(ex));

        /// <summary>Record an informational entry.</summary>
        public static void Info(string context, string detail = "") =>
            Write(Severity.Info, context, detail ?? "");

        /// <summary>Absolute path to the durable log file.</summary>
        public static string LogFilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                return Path.Combine(dir, "diagnostics.log");
            }
        }

        /// <summary>Snapshot of the most recent in-memory entries (oldest first).</summary>
        public static IReadOnlyList<Entry> Recent()
        {
            lock (_gate) return new List<Entry>(_ring);
        }

        /// <summary>
        /// Total number of Warning/Error entries recorded since the plugin loaded.
        /// A tool captures this at run-start and compares at completion to report how
        /// many non-fatal issues its run produced (see <see cref="IssuesSince"/>).
        /// </summary>
        public static long IssueCount
        {
            get { lock (_gate) return _issueCount; }
        }

        /// <summary>Number of Warning/Error entries recorded since <paramref name="startCount"/> was sampled from <see cref="IssueCount"/>.</summary>
        public static long IssuesSince(long startCount)
        {
            long now = IssueCount;
            return now > startCount ? now - startCount : 0;
        }

        /// <summary>
        /// Open the durable log file in the OS default handler. Returns false if it
        /// could not be opened (and records why). Creates an empty file first if none exists.
        /// </summary>
        public static bool OpenInDefaultViewer()
        {
            try
            {
                string path = LogFilePath;
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "");
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                // The viewer-open itself failing must not throw at the call site,
                // but it is still a failure worth recording.
                Write(Severity.Error, "OpenInDefaultViewer", Describe(ex));
                return false;
            }
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private static void Write(Severity sev, string context, string detail)
        {
            var entry = new Entry(DateTime.Now, sev, context ?? "(no context)", detail ?? "");

            lock (_gate)
            {
                if (_ring.Count >= RingCapacity) _ring.Dequeue();
                _ring.Enqueue(entry);
                if (sev != Severity.Info) _issueCount++;
                AppendToFile(entry);
            }

            // Forward to live subscribers outside the lock; absorb their failures so
            // a broken sink can never break logging.
            var handler = EntryLogged;
            if (handler != null)
            {
                try { handler(entry); }
                catch { /* a live-sink failure must never propagate into the logger */ }
            }

            // Mirror to the debugger output for dev sessions.
            Debug.WriteLine(entry.ToString());
        }

        private static void AppendToFile(Entry entry)
        {
            // Called under _gate. A logging-IO failure must never throw at the call
            // site — the in-memory ring and live sink still carry the entry — so we
            // fall back to the debugger output only.
            try
            {
                string path = LogFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RollIfTooLarge(path);
                File.AppendAllText(path, entry + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ioEx)
            {
                Debug.WriteLine("LemoineLog: could not write diagnostics file — " + ioEx.Message);
            }
        }

        private static void RollIfTooLarge(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxFileBytes) return;

                string prev = Path.ChangeExtension(path, ".prev.log");
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(path, prev);
            }
            catch (Exception rollEx)
            {
                Debug.WriteLine("LemoineLog: could not roll diagnostics file — " + rollEx.Message);
            }
        }

        private static string Describe(Exception? ex)
        {
            if (ex == null) return "(null exception)";
            var sb = new StringBuilder();
            sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            if (ex.InnerException != null)
                sb.Append(" | inner ").Append(ex.InnerException.GetType().Name)
                  .Append(": ").Append(ex.InnerException.Message);
            return sb.ToString();
        }
    }
}
