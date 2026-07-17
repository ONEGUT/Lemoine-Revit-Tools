using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Convenience base for IWebTool ports: ValidationChanged/Fire, default step visibility,
    /// and the value-coercion helpers every OnState needs (bridge values arrive as
    /// string / double / bool / List&lt;object?&gt; / Dictionary from MiniJson).
    /// </summary>
    public abstract class WebToolBase : IWebTool
    {
        public abstract string Title    { get; }
        public abstract string RunLabel { get; }

        public event EventHandler? ValidationChanged;
        protected void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public abstract IReadOnlyList<WebStep> BuildSteps();
        public abstract void OnState(string stepId, string inputId, object? value);
        public abstract bool IsStepValid(string stepId);
        public abstract bool CanRun();
        public abstract string SummaryFor(string stepId);
        public virtual bool IsStepVisible(string stepId) => true;

        public abstract void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete);

        // ── Bridge value coercion ─────────────────────────────────────────────

        /// <summary>A JSON string array (multi-select / tree / checklist selections).</summary>
        protected static List<string> StrList(object? value) =>
            value is IEnumerable seq && !(value is string)
                ? seq.Cast<object?>().Select(o => o?.ToString()).Where(s => s != null).Select(s => s!).ToList()
                : new List<string>();

        /// <summary>Same list parsed as ElementId longs (BrowserTree ids travel as strings).</summary>
        protected static List<long> IdList(object? value) =>
            StrList(value).Select(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (long?)null)
                          .Where(v => v.HasValue).Select(v => v!.Value).ToList();

        protected static bool AsBool(object? value, bool dflt = false) => value is bool b ? b : dflt;

        protected static double AsDouble(object? value, double dflt = 0) =>
            value is double d ? d :
            value is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : dflt;

        protected static string AsString(object? value, string dflt = "") => value as string ?? dflt;

        /// <summary>numberRange payload {"min":x,"max":y}.</summary>
        protected static (double Min, double Max) AsRange(object? value, double dfltMin, double dfltMax)
        {
            if (value is Dictionary<string, object?> d)
                return (AsDouble(d.TryGetValue("min", out var lo) ? lo : null, dfltMin),
                        AsDouble(d.TryGetValue("max", out var hi) ? hi : null, dfltMax));
            return (dfltMin, dfltMax);
        }

        // ── BrowserTree eligible-leaf pruning ─────────────────────────────────

        /// <summary>
        /// Restricts a captured browser tree to the given eligible leaf ids, mirroring the WPF
        /// BrowserTreePicker's eligibleIds parameter: folders are kept while they still contain
        /// an eligible leaf, and an ineligible leaf that still has kept children is demoted to
        /// a folder (its id cleared) so it can't be selected.
        /// </summary>
        protected static BrowserTree PruneTree(BrowserTree tree, HashSet<long> keepIds)
        {
            var pruned = new BrowserTree();
            foreach (var root in tree.Roots)
            {
                var copy = PruneNode(root, keepIds);
                if (copy != null) pruned.Roots.Add(copy);
            }
            return pruned;
        }

        private static BrowserNode? PruneNode(BrowserNode node, HashSet<long> keepIds)
        {
            var copy = new BrowserNode { Title = node.Title, Id = node.Id, IsSheet = node.IsSheet };
            foreach (var child in node.Children)
            {
                var kept = PruneNode(child, keepIds);
                if (kept != null) copy.Children.Add(kept);
            }
            bool selfEligible = node.Id.HasValue && keepIds.Contains(node.Id.Value);
            if (selfEligible || copy.Children.Count > 0)
            {
                if (node.Id.HasValue && !selfEligible) copy.Id = null; // ineligible leaf demoted to folder
                return copy;
            }
            return null;
        }

        // ── Naming-token preview sample ───────────────────────────────────────

        /// <summary>
        /// Builds the token→sample-value map for the web TokenInput preview by resolving each
        /// token against <paramref name="ctx"/> via <see cref="TokenResolver"/> (Revit-free for
        /// computed/environment tokens). Tokens the context can't resolve keep their literal
        /// {Key} form in the page preview, mirroring the resolver's unknown-token behavior.
        /// </summary>
        protected static Dictionary<string, string> SampleFor(
            IEnumerable<TokenDefinition> tokens, TokenContext ctx)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in tokens)
            {
                string resolved;
                try { resolved = TokenResolver.Resolve(t.Braced, ctx) ?? ""; }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"WebToolBase: sample for token '{t.Key}'", ex);
                    continue;
                }
                if (!string.IsNullOrEmpty(resolved) && resolved != t.Braced)
                    map[t.Key] = resolved;
            }
            return map;
        }
    }
}
