using System;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Emits a run-log line at every 5% interval while a tool iterates a large
    /// collection, so long operations report steady progress instead of going silent
    /// between "Starting…" and the final summary. Optionally drives the progress-bar
    /// percentage through an <c>onProgress</c> callback as well.
    ///
    /// Only collections at or above <see cref="DefaultThreshold"/> items report at
    /// intervals — smaller batches stay quiet, because a 5% step on (say) 12 items is
    /// roughly one item and would flood the log. Below the threshold a tool should just
    /// log a start line and a pass/fail/skip summary.
    ///
    /// Revit-free: it only invokes the supplied callbacks, so any layer can use it.
    /// Typical use inside an ExternalEventHandler:
    /// <code>
    /// var prog = new RunProgressReporter(pushLog, elements.Count, "ceilings");
    /// foreach (var el in elements)
    /// {
    ///     // …do the work…
    ///     prog.Tick();                                   // logs "25% — 250 of 1000 ceilings"
    ///     onProgress(prog.Percent, pass, fail, skip);    // optional: bump the bar
    /// }
    /// </code>
    /// </summary>
    public sealed class RunProgressReporter
    {
        /// <summary>Collections smaller than this never emit interval log lines.</summary>
        public const int DefaultThreshold = 20;

        private readonly Action<string, string> _pushLog;
        private readonly string _noun;
        private readonly int    _stepPct;
        private readonly bool   _active;
        private int _done;
        private int _lastBucket;

        /// <param name="pushLog">The tool's run-log callback (text, status).</param>
        /// <param name="total">Total number of items that will be processed.</param>
        /// <param name="noun">Plural noun for the items, e.g. "sheets", "ceilings".</param>
        /// <param name="stepPct">Interval size in percent (default 5).</param>
        /// <param name="threshold">Minimum total for interval reporting (default 20).</param>
        public RunProgressReporter(Action<string, string> pushLog, int total, string noun,
                                   int stepPct = 5, int threshold = DefaultThreshold)
        {
            _pushLog = pushLog ?? throw new ArgumentNullException(nameof(pushLog));
            _noun    = string.IsNullOrWhiteSpace(noun) ? "items" : noun;
            _stepPct = stepPct < 1 ? 1 : stepPct;
            Total    = total;
            _active  = total >= threshold;
        }

        /// <summary>Total item count supplied at construction.</summary>
        public int Total { get; }

        /// <summary>Items processed so far.</summary>
        public int Done => _done;

        /// <summary>Completion percentage 0–100 (100 when total is zero).</summary>
        public int Percent => Total <= 0 ? 100 : (int)(100L * _done / Total);

        /// <summary>
        /// Advance the processed counter by <paramref name="count"/> and emit a log line
        /// if a new interval boundary (5%, 10%, …) was crossed. No-op for small batches.
        /// </summary>
        public void Tick(int count = 1)
        {
            if (count > 0) _done += count;
            if (!_active || Total <= 0) return;

            int bucket = Percent - (Percent % _stepPct);
            if (bucket > _lastBucket)
            {
                _lastBucket = bucket;
                _pushLog($"{bucket}% — {_done:N0} of {Total:N0} {_noun} processed", "info");
            }
        }
    }
}
