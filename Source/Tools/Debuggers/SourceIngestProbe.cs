using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Clash;

namespace LemoineTools.Tools.Debuggers
{
    /// <summary>
    /// One titled block of diagnostic output. Lines are plain strings so the
    /// display ViewModel stays completely Revit-free (it runs on the window's STA
    /// thread, where touching the Revit API would crash).
    /// </summary>
    public sealed class ProbeSection
    {
        public string Title { get; }
        public List<string> Lines { get; } = new List<string>();
        public ProbeSection(string title) { Title = title; }
        public void Add(string line) => Lines.Add(line);
        public void Add(string label, object? value) => Lines.Add($"{label,-34}{value}");
    }

    /// <summary>A complete, serialised-to-strings snapshot of the source-ingest probe.</summary>
    public sealed class ProbeReport
    {
        public string Headline { get; set; } = "";
        public List<ProbeSection> Sections { get; } = new List<ProbeSection>();

        public string ToText()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Headline);
            foreach (var s in Sections)
            {
                sb.AppendLine();
                sb.AppendLine("── " + s.Title + " ──");
                foreach (var l in s.Lines) sb.AppendLine(l);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Data-gathering harness for the "Auto-Dimension found no lines" investigation
    /// (per CLAUDE.md "Crashes &amp; Large Ambiguous Issues — Build a Debugger First").
    /// Runs on Revit's main thread inside <see cref="LemoineTools.Commands.DebugToolCommand"/>,
    /// replicates <c>SourceIngest</c>'s exact filter chain stage-by-stage, and records why
    /// each line does or does not survive. Read-only — opens no transaction, mutates nothing.
    /// </summary>
    public static class SourceIngestProbe
    {
        private const int MaxLineRows = 80;

        public static ProbeReport Collect(UIApplication app)
        {
            var report = new ProbeReport();

            UIDocument? uidoc = app?.ActiveUIDocument;
            Document? doc     = uidoc?.Document;
            View? view        = uidoc?.ActiveView;

            if (doc == null || view == null)
            {
                report.Headline = "No active document / view — open the problem view and re-run.";
                return report;
            }

            // ── 1. Context ────────────────────────────────────────────────────────
            var ctxSec = new ProbeSection("View & schema context");
            ctxSec.Add("Active view", $"{view.Name}  (id {view.Id.IntegerValue}, {view.ViewType})");
            ctxSec.Add("Document", doc.Title);
            int linkCount = SafeCount(() => new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).GetElementCount());
            ctxSec.Add("Linked models", linkCount + "  (SourceIngest reads the HOST doc only — detail lines are host/view-scoped)");

            // Schema state BEFORE we force a rebuild — reveals "written last session, not yet registered".
            Schema? preLookup = SafeRun(() => Schema.Lookup(ClashTagSchema.SchemaGuid), report, "Schema.Lookup");
            ctxSec.Add("Schema.Lookup (pre)", preLookup == null ? "null  (not registered yet this session)" : "found  (already registered)");

            Schema? schema = SafeRun(() => ClashTagSchema.GetOrCreateTagSchema(), report, "GetOrCreateTagSchema");
            ctxSec.Add("GetOrCreateTagSchema", schema == null ? "FAILED — schema unavailable" : "ok");
            if (schema != null)
            {
                ctxSec.Add("  schema name", SafeRun(() => schema.SchemaName, report, "schema.SchemaName"));
                var fields = SafeRun(() => schema.ListFields().Select(f => $"{f.FieldName}:{f.ValueType.Name}").ToList(), report, "schema.ListFields")
                             ?? new List<string>();
                ctxSec.Add("  fields", string.Join(", ", fields));
            }
            ctxSec.Add("Expected tag value", ClashTagSchema.TagValue);
            report.Sections.Add(ctxSec);

            // ── 2. SourceIngest filter chain, stage by stage ──────────────────────
            // Mirror SourceIngest.Collect exactly:
            //   OfCategory(OST_Lines) → WhereElementIsNotElementType → Where(IsOurs) → OfType<DetailCurve>
            var chain = new ProbeSection("SourceIngest filter chain (what the engine actually sees)");

            List<Element> ostLines = SafeRun(() => new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Lines)
                .WhereElementIsNotElementType()
                .ToList(), report, "collect OST_Lines") ?? new List<Element>();
            chain.Add("1. OST_Lines in view (non-type)", ostLines.Count);

            int detailBefore = ostLines.Count(e => e is DetailCurve);
            chain.Add("   …of which DetailCurve", detailBefore);
            chain.Add("   …of which other types", ostLines.Count - detailBefore);

            var tagged = ostLines.Where(e => SafeIsOurs(e)).ToList();
            chain.Add("2. After .Where(IsOurs)", tagged.Count);

            int detailAndTagged = tagged.Count(e => e is DetailCurve);
            chain.Add("3. After .OfType<DetailCurve>()", detailAndTagged + "   ← this is what SourceIngest returns");
            chain.Add("");
            if (detailAndTagged == 0)
            {
                if (ostLines.Count == 0)
                    chain.Add(">> No detail lines exist in this view at all. Wrong view, or markers were never created here.");
                else if (tagged.Count == 0)
                    chain.Add(">> Lines exist but NONE carry the clash tag → StampTag never ran / failed, or these aren't Clash-Finder lines.");
                else if (detailBefore == 0)
                    chain.Add(">> Tagged lines exist but none are DetailCurve (they may be ModelCurve) → the .OfType<DetailCurve>() filter drops them.");
                else
                    chain.Add(">> Tagged DetailCurves exist in the set but the count is still zero — inspect the per-line table.");
            }
            report.Sections.Add(chain);

            // ── 3. Per-line breakdown ─────────────────────────────────────────────
            var rows = new ProbeSection($"Per-line detail (first {MaxLineRows} of {ostLines.Count})");
            rows.Add($"{"Id",-10}{"Type",-16}{"Detail?",-9}{"Entity",-8}{"Stored",-12}IsOurs");
            rows.Add(new string('-', 66));
            foreach (var e in ostLines.Take(MaxLineRows))
            {
                string id      = e.Id.IntegerValue.ToString(CultureInfo.InvariantCulture);
                string type    = e.GetType().Name;
                string isDetail = (e is DetailCurve) ? "yes" : "no";
                var (present, stored) = ReadEntity(e, schema);
                string isOurs  = SafeIsOurs(e) ? "TRUE" : "false";
                rows.Add($"{id,-10}{type,-16}{isDetail,-9}{(present ? "yes" : "—"),-8}{Trunc(stored, 11),-12}{isOurs}");
            }
            if (ostLines.Count > MaxLineRows) rows.Add($"… {ostLines.Count - MaxLineRows} more not shown.");
            report.Sections.Add(rows);

            // ── 4. Census: every TAGGED element in the view, by category ───────────
            // If filled regions are tagged but lines aren't, the tagging pass partially failed.
            var census = new ProbeSection("Tagged-element census (whole view, all categories)");
            var allInView = SafeRun(() => new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType().ToList(), report, "collect all-in-view") ?? new List<Element>();
            census.Add("Total elements in view", allInView.Count);
            var taggedByCat = allInView
                .Where(e => SafeIsOurs(e))
                .GroupBy(e => e.Category?.Name ?? "(no category)")
                .OrderByDescending(g => g.Count())
                .ToList();
            int taggedTotal = taggedByCat.Sum(g => g.Count());
            census.Add("Total carrying the clash tag", taggedTotal);
            if (taggedTotal == 0)
                census.Add(">> Nothing in this view is tagged. Either the Clash Finder hasn't run here, or the tag schema written earlier isn't being matched.");
            foreach (var g in taggedByCat)
                census.Add($"   {g.Key}", g.Count());
            report.Sections.Add(census);

            report.Headline = $"SourceIngest would return {detailAndTagged} line(s) from \"{view.Name}\" "
                            + $"({ostLines.Count} detail line(s) present, {tagged.Count} tagged).";
            return report;
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private static (bool present, string stored) ReadEntity(Element e, Schema? schema)
        {
            if (schema == null) return (false, "");
            try
            {
                var ent = e.GetEntity(schema);
                if (ent == null || !ent.IsValid()) return (false, "");
                foreach (var f in schema.ListFields())
                {
                    if (f.ValueType == typeof(string))
                    {
                        try { return (true, ent.Get<string>(f) ?? ""); }
                        catch (Exception ex) { LemoineLog.Swallowed("SourceIngestProbe: read field", ex); return (true, "<read err>"); }
                    }
                }
                return (true, "<no string field>");
            }
            catch (Exception ex) { LemoineLog.Swallowed("SourceIngestProbe: GetEntity", ex); return (false, "<err>"); }
        }

        private static bool SafeIsOurs(Element e)
        {
            try { return ClashTagSchema.IsOurs(e); }
            catch (Exception ex) { LemoineLog.Swallowed("SourceIngestProbe: IsOurs", ex); return false; }
        }

        private static int SafeCount(Func<int> f)
        {
            try { return f(); }
            catch (Exception ex) { LemoineLog.Swallowed("SourceIngestProbe: count", ex); return -1; }
        }

        private static T? SafeRun<T>(Func<T> f, ProbeReport report, string context) where T : class
        {
            try { return f(); }
            catch (Exception ex)
            {
                LemoineLog.Error("SourceIngestProbe: " + context, ex);
                if (report.Sections.Count > 0)
                    report.Sections[report.Sections.Count - 1].Add($"!! {context} threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "—" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");
    }
}
