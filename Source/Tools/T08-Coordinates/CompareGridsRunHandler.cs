using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Coordinates
{
    /// <summary>
    /// Read-only audit: collects grids from the host and every selected link into a common world
    /// frame and reports, per grid name, whether the grid is consistent across files or differs
    /// (missing in a file, laterally offset / rotated, or present in only one file). Makes no
    /// model changes — no transaction. Comparison is meaningful once files share coordinates.
    /// </summary>
    public sealed class CompareGridsRunHandler : IExternalEventHandler
    {
        public List<long> FileLinkInstIds { get; set; } = new List<long>();   // 0 entries = host
        public bool       IncludeHost     { get; set; } = true;
        public double     PosTolInches    { get; set; } = 0.0625;             // 1/16"
        public double     AngleTolDegrees { get; set; } = 0.10;

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Coordinates.CompareGridsRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        private sealed class WorldGrid
        {
            public string File = "";
            public string Name = "";
            public XYZ    Point = XYZ.Zero;
            public XYZ    Dir   = XYZ.BasisX;
        }

        public void Execute(UIApplication app)
        {
            int consistent = 0, discrepancies = 0;
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) { Log("No active document.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                double tolFt  = Math.Max(0.0, PosTolInches) / 12.0;
                double tolRad = Math.Max(0.0, AngleTolDegrees) * Math.PI / 180.0;

                var grids    = new List<WorldGrid>();
                var fileNames = new List<string>();

                if (IncludeHost)
                {
                    fileNames.Add("[Host]");
                    CollectGrids(doc, Transform.Identity, "[Host]", grids);
                }

                foreach (long linkId in FileLinkInstIds ?? new List<long>())
                {
                    if (linkId == 0L) continue;
                    var li = doc.GetElement(new ElementId(linkId)) as RevitLinkInstance;
                    var ld = li?.GetLinkDocument();
                    if (li == null || ld == null) { Log($"⚠ Link {linkId} is not loaded — skipped.", "warn"); continue; }
                    string name = SafeLinkName(ld);
                    fileNames.Add(name);
                    CollectGrids(ld, li.GetTotalTransform(), name, grids);
                }

                if (fileNames.Count < 2)
                {
                    Log("Need at least two files (host + a link, or two links) to compare.", "warn");
                    OnComplete?.Invoke(0, 0, 0); return;
                }

                Log($"Comparing grids across {fileNames.Count} file(s): {string.Join(", ", fileNames)}.", "info");
                Log($"Tolerances: {PosTolInches:0.###}\" lateral, {AngleTolDegrees:0.##}° angular.", "info");

                var units = doc.GetUnits();
                int totalNames = grids.Select(g => g.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (totalNames == 0) { Log("No grids found in any selected file.", "warn"); OnComplete?.Invoke(0, 0, 0); return; }

                int done = 0;
                foreach (var group in grids.GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                                           .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string name = group.Key;
                    var filesWith = group.Select(g => g.File).Distinct().ToList();

                    // Case 1: present in only one file (out of several) — an "extra" grid.
                    // Reported on its own; the per-file "missing" spam would be redundant here.
                    if (filesWith.Count == 1 && fileNames.Count > 1)
                    {
                        Log($"○ Grid '{name}' only in {filesWith[0]}.", "info");
                        discrepancies++;
                        done++;
                        OnProgress?.Invoke((int)(100.0 * done / totalNames), consistent, discrepancies, 0);
                        continue;
                    }

                    bool ok = true;

                    // Case 2: missing in some (but not all) files.
                    foreach (var m in fileNames.Where(f => !filesWith.Contains(f)))
                    {
                        Log($"✗ Grid '{name}' missing in {m}.", "fail");
                        discrepancies++;
                        ok = false;
                    }

                    // Case 3: present in multiple files but offset/rotated beyond tolerance.
                    var reference = group.FirstOrDefault(g => g.File == "[Host]") ?? group.First();
                    foreach (var g in group)
                    {
                        if (ReferenceEquals(g, reference)) continue;
                        double offset = CoordinatesGeometry.LateralOffset(reference.Point, reference.Dir, g.Point);
                        double angle  = CoordinatesGeometry.AngleBetween(reference.Dir, g.Dir);
                        if (offset > tolFt || angle > tolRad)
                        {
                            string offStr = FormatLength(units, offset);
                            string angStr = (angle * 180.0 / Math.PI).ToString("0.##") + "°";
                            Log($"✗ Grid '{name}' differs in {g.File}: offset {offStr}, {angStr} (vs {reference.File}).", "fail");
                            discrepancies++;
                            ok = false;
                        }
                    }

                    if (ok) consistent++;

                    done++;
                    OnProgress?.Invoke((int)(100.0 * done / totalNames), consistent, discrepancies, 0);
                }

                if (discrepancies == 0)
                    Log($"All {consistent} grid(s) consistent across {fileNames.Count} file(s).", "pass");
                else
                    Log($"{consistent} grid(s) consistent, {discrepancies} discrepancy/ies found across {fileNames.Count} file(s).", "warn");

                OnProgress?.Invoke(100, consistent, discrepancies, 0);
                OnComplete?.Invoke(consistent, discrepancies, 0);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("CompareGridsRunHandler.Execute", ex);
                Log($"Run aborted: {ex.Message}", "fail");
                OnComplete?.Invoke(consistent, discrepancies + 1, 0);
            }
            finally
            {
                FileLinkInstIds = new List<long>();
            }
        }

        private static void CollectGrids(Document src, Transform t, string fileName, List<WorldGrid> sink)
        {
            try
            {
                foreach (var g in new FilteredElementCollector(src).OfClass(typeof(Grid)).Cast<Grid>())
                {
                    if (!CoordinatesGeometry.TryGridLine(g, out var p, out var d)) continue;
                    sink.Add(new WorldGrid
                    {
                        File  = fileName,
                        Name  = g.Name,
                        Point = t.OfPoint(p),
                        Dir   = NormalizeXY(t.OfVector(d)),
                    });
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed($"CompareGrids: collect grids in {fileName}", ex); }
        }

        private static XYZ NormalizeXY(XYZ v)
        {
            var flat = new XYZ(v.X, v.Y, 0);
            return flat.GetLength() < 1e-9 ? XYZ.BasisX : flat.Normalize();
        }

        private static string FormatLength(Units units, double feet)
        {
            try { return UnitFormatUtils.Format(units, SpecTypeId.Length, feet, false); }
            catch { return feet.ToString("0.###") + "'"; }
        }

        private static string SafeLinkName(Document ld)
        {
            try { return "[" + System.IO.Path.GetFileNameWithoutExtension(ld.Title) + "]"; }
            catch { return "[link]"; }
        }
    }
}
