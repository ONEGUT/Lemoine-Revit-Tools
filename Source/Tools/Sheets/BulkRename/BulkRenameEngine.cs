using System;
using System.Collections.Generic;
using System.Text;
using LemoineTools.Framework.Controls;

namespace LemoineTools.Tools.LinkViews.BulkRename
{
    /// <summary>
    /// Which kind of element the rename operates on.
    /// </summary>
    public enum RenameTarget { Sheets, Views }

    /// <summary>
    /// Which field is rewritten. <see cref="Number"/> is only valid for sheets;
    /// views expose no sheet number, so a view rename always uses <see cref="Name"/>.
    /// </summary>
    public enum RenameField { Number, Name }

    /// <summary>
    /// The four rename operations the tool offers.
    /// </summary>
    public enum RenameMode { FindReplace, PrefixSuffix, Sequential, Token }

    /// <summary>
    /// Per-item outcome computed by <see cref="BulkRenameEngine.Plan"/>.
    /// </summary>
    public enum RenameStatus
    {
        /// <summary>New value is valid and differs from the old value.</summary>
        Change,
        /// <summary>New value equals the old value — nothing to write.</summary>
        Unchanged,
        /// <summary>New value resolved to empty/whitespace — cannot write.</summary>
        Empty,
        /// <summary>New value duplicates another element's value (unique fields only).</summary>
        Collision,
    }

    /// <summary>
    /// All inputs for a rename run. Pure data — no Revit or WPF state — so the
    /// same instance is shared by the live preview (ViewModel) and the run handler.
    /// </summary>
    public sealed class RenameConfig
    {
        public RenameMode Mode { get; set; } = RenameMode.FindReplace;

        // ── Find & Replace ────────────────────────────────────────────────────
        public string Find          { get; set; } = "";
        public string Replace       { get; set; } = "";
        public bool   CaseSensitive { get; set; } = false;
        public bool   WholeField    { get; set; } = false;

        // ── Prefix / Suffix ───────────────────────────────────────────────────
        public string Prefix { get; set; } = "";
        public string Suffix { get; set; } = "";

        // ── Sequential & Token (both share the counter) ───────────────────────
        /// <summary>Pattern for Sequential mode — typically contains <c>{Seq}</c>.</summary>
        public string SeqPattern   { get; set; } = "{Seq}";
        /// <summary>Pattern for Token mode — field tokens plus optional <c>{Seq}</c>.</summary>
        public string TokenPattern { get; set; } = "";
        public int    SeqStart     { get; set; } = 1;
        public int    SeqIncrement { get; set; } = 1;
        /// <summary>Zero-pad width for the counter (0 = no padding).</summary>
        public int    SeqPad       { get; set; } = 0;
    }

    /// <summary>
    /// One planned rename: the realized old/new strings and the outcome status.
    /// <see cref="Tag"/> carries a caller payload (the element id) untouched.
    /// </summary>
    public sealed class RenamePlanItem
    {
        public string       OldValue { get; set; } = "";
        public string       NewValue { get; set; } = "";
        public RenameStatus Status   { get; set; }
        public object?      Tag      { get; set; }
    }

    /// <summary>
    /// Revit-free rename engine. Computes the new value for one item
    /// (<see cref="Compute"/>) and plans a whole ordered batch with uniqueness
    /// enforcement (<see cref="Plan"/>). The ViewModel preview and the
    /// <c>BulkRenameRunHandler</c> both call <see cref="Plan"/> with identical
    /// inputs, so the preview is exactly what gets written.
    /// </summary>
    public static class BulkRenameEngine
    {
        /// <summary>
        /// Computes the new field value for a single item. <paramref name="tokens"/>
        /// supplies the field tokens (e.g. SheetNumber, ViewName); a <c>Seq</c> token
        /// derived from <paramref name="index"/> is added for the counter-based modes.
        /// </summary>
        public static string Compute(RenameConfig cfg, string oldValue,
                                     Dictionary<string, string> tokens, int index)
        {
            oldValue ??= "";
            tokens   ??= new Dictionary<string, string>();

            switch (cfg.Mode)
            {
                case RenameMode.FindReplace:
                    if (string.IsNullOrEmpty(cfg.Find)) return oldValue;
                    if (cfg.WholeField)
                    {
                        bool match = string.Equals(oldValue, cfg.Find,
                            cfg.CaseSensitive ? StringComparison.Ordinal
                                              : StringComparison.OrdinalIgnoreCase);
                        return match ? cfg.Replace : oldValue;
                    }
                    return ReplaceSubstring(oldValue, cfg.Find, cfg.Replace, cfg.CaseSensitive);

                case RenameMode.PrefixSuffix:
                    return (cfg.Prefix ?? "") + oldValue + (cfg.Suffix ?? "");

                case RenameMode.Sequential:
                    return TokenInput.Resolve(cfg.SeqPattern,
                        WithSeq(tokens, cfg, index));

                case RenameMode.Token:
                    return TokenInput.Resolve(cfg.TokenPattern,
                        WithSeq(tokens, cfg, index));
            }
            return oldValue;
        }

        /// <summary>
        /// Plans an ordered batch. <paramref name="existingValuesNotSelected"/> are the
        /// field values of elements NOT in the batch (used for uniqueness when
        /// <paramref name="enforceUnique"/> is true — i.e. sheet numbers and view names).
        /// Uniqueness is compared case-insensitively to mirror Revit's behaviour.
        /// A renamed element frees its old value for a later item in the same batch.
        /// </summary>
        public static List<RenamePlanItem> Plan(
            RenameConfig cfg,
            IReadOnlyList<(string oldValue, Dictionary<string, string> tokens, object? tag)> items,
            IEnumerable<string> existingValuesNotSelected,
            bool enforceUnique)
        {
            var used = new HashSet<string>(
                existingValuesNotSelected ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            // Each selected item still occupies its current value until reassigned.
            foreach (var it in items) used.Add(it.oldValue ?? "");

            var results = new List<RenamePlanItem>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                var (oldValue, tokens, tag) = items[i];
                oldValue ??= "";
                string newValue = (Compute(cfg, oldValue, tokens, i) ?? "").Trim();

                var r = new RenamePlanItem { OldValue = oldValue, NewValue = newValue, Tag = tag };

                if (string.IsNullOrWhiteSpace(newValue))
                {
                    r.Status = RenameStatus.Empty;
                }
                else if (string.Equals(newValue, oldValue, StringComparison.Ordinal))
                {
                    r.Status = RenameStatus.Unchanged;
                }
                else if (enforceUnique && used.Contains(newValue))
                {
                    r.Status = RenameStatus.Collision;
                }
                else
                {
                    r.Status = RenameStatus.Change;
                    if (enforceUnique)
                    {
                        used.Remove(oldValue);
                        used.Add(newValue);
                    }
                }
                results.Add(r);
            }
            return results;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static Dictionary<string, string> WithSeq(
            Dictionary<string, string> tokens, RenameConfig cfg, int index)
        {
            string seq = (cfg.SeqStart + index * cfg.SeqIncrement).ToString();
            if (cfg.SeqPad > 0) seq = seq.PadLeft(cfg.SeqPad, '0');

            var t = new Dictionary<string, string>(tokens) { ["Seq"] = seq };
            return t;
        }

        private static string ReplaceSubstring(string input, string find, string repl, bool caseSensitive)
        {
            if (string.IsNullOrEmpty(find)) return input;
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            var sb = new StringBuilder();
            int i = 0;
            while (true)
            {
                int idx = input.IndexOf(find, i, comparison);
                if (idx < 0) { sb.Append(input, i, input.Length - i); break; }
                sb.Append(input, i, idx - i);
                sb.Append(repl ?? "");
                i = idx + find.Length;
            }
            return sb.ToString();
        }
    }
}
