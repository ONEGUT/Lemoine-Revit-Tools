using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Commands
{
    /// <summary>
    /// DEBUG HARNESS — Scope Box API capability probe.
    ///
    /// The Revit API cannot create a scope box from scratch. The Scope Box Creator's design
    /// therefore hinges on empirical answers this probe collects on a real Windows/Revit build:
    ///   • Which parameters does a scope box expose (name, height, width/depth, …) and are they
    ///     writable? (differs by Revit year — 2024 vs 2025+)
    ///   • Does ElementTransformUtils.CopyElement duplicate a scope box, and how is the copy named?
    ///   • Can the copy be moved / rotated / renamed?
    ///   • Can any dimension (height / width / depth) actually be resized via a parameter?
    ///   • Is the view Scope Box parameter (VIEWER_VOLUME_OF_INTEREST_CROP) writable?
    ///
    /// Every mutation runs inside a transaction that is ROLLED BACK, so the probe never modifies
    /// the model. Results are written to %AppData%\LemoineTools\ScopeBoxProbe.txt and opened in
    /// Notepad. This is developer-only output — strings stay hardcoded per CLAUDE.md.
    ///
    /// Remove or repoint this harness once the Creator's sizing behaviour is confirmed.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ScopeBoxProbeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            var doc   = uidoc?.Document;
            if (doc == null) { message = "No active document."; return Result.Failed; }

            var sb = new StringBuilder();
            Header(sb, "SCOPE BOX API CAPABILITY PROBE");
            sb.AppendLine($"Generated   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Document    : {doc.Title}");
            sb.AppendLine($"Revit build : {commandData.Application.Application.VersionNumber} " +
                          $"({commandData.Application.Application.SubVersionNumber})");
            sb.AppendLine();

            try { RunProbe(doc, uidoc!, sb); }
            catch (Exception ex)
            {
                sb.AppendLine();
                sb.AppendLine("FATAL: probe aborted — " + ex);
                DiagnosticsLog.Error("ScopeBoxProbe: aborted", ex);
            }

            Write(sb);
            return Result.Succeeded;
        }

        // ═════════════════════════════════════════════════════════════════════════
        private static void RunProbe(Document doc, UIDocument uidoc, StringBuilder sb)
        {
            // ── 1. Inventory ──────────────────────────────────────────────────────
            var boxes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                .WhereElementIsNotElementType()
                .ToList();

            Header(sb, $"INVENTORY — {boxes.Count} scope box(es)");
            if (boxes.Count == 0)
            {
                sb.AppendLine("No scope boxes in this document.");
                sb.AppendLine("Draw ONE scope box (View ▸ Scope Box) and run this probe again —");
                sb.AppendLine("the Creator needs a seed box to duplicate.");
                return;
            }

            foreach (var b in boxes)
            {
                Sub(sb, $"'{b.Name}'  (Id {b.Id.Value})");
                DumpBBox(sb, b.get_BoundingBox(null));
            }

            var seed = boxes[0];

            // ── 2. Full parameter dump of the seed ────────────────────────────────
            Header(sb, $"PARAMETER DUMP — seed '{seed.Name}' (Id {seed.Id.Value})");
            sb.AppendLine($"{"Parameter",-34}{"BuiltIn",-40}{"Storage",-10}{"RO",-4}Value");
            sb.AppendLine(new string('─', 110));
            foreach (Parameter p in seed.Parameters.Cast<Parameter>()
                         .OrderBy(p => p.Definition?.Name ?? ""))
                DumpParam(sb, p);

            // ── 3. Reflection: public instance methods on the scope box element ────
            Header(sb, "ELEMENT TYPE — " + seed.GetType().FullName);
            sb.AppendLine("Public instance methods (name — return type):");
            foreach (var m in seed.GetType()
                         .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => !m.IsSpecialName)
                         .OrderBy(m => m.Name)
                         .GroupBy(m => m.Name).Select(g => g.First()))
                sb.AppendLine($"  {m.Name,-34} {m.ReturnType.Name}");

            // ── 4. Mutation tests (rolled back — non-destructive) ─────────────────
            Header(sb, "MUTATION TESTS  (transaction is ROLLED BACK — model untouched)");
            using (var tx = new Transaction(doc, "ScopeBox probe (rollback)"))
            {
                tx.Start();

                // 4a. Duplicate via CopyElement
                ElementId copyId = ElementId.InvalidElementId;
                Sub(sb, "CopyElement(seed) — can we duplicate a scope box?");
                try
                {
                    var copies = ElementTransformUtils.CopyElement(doc, seed.Id, XYZ.Zero);
                    copyId = copies.FirstOrDefault() ?? ElementId.InvalidElementId;
                    if (copyId == ElementId.InvalidElementId)
                        sb.AppendLine("  → returned no ids (copy FAILED).");
                    else
                    {
                        doc.Regenerate();
                        var copy = doc.GetElement(copyId);
                        sb.AppendLine($"  → OK. New id {copyId.Value}, auto-name '{copy?.Name}'.");
                    }
                }
                catch (Exception ex) { sb.AppendLine("  → EXCEPTION: " + ex.Message); }

                Element? work = copyId != ElementId.InvalidElementId ? doc.GetElement(copyId) : null;

                // 4b. Rename the copy
                Sub(sb, "Rename copy");
                if (work == null) sb.AppendLine("  skipped — no copy.");
                else
                {
                    try
                    {
                        work.Name = "PROBE-RENAME-TEST";
                        doc.Regenerate();
                        sb.AppendLine($"  → OK. Name is now '{work.Name}'.");
                    }
                    catch (Exception ex) { sb.AppendLine("  → EXCEPTION: " + ex.Message); }
                }

                // 4c. Move the copy and read the bbox delta
                Sub(sb, "MoveElement(copy, +100,+50,0) — does the box translate?");
                if (work == null) sb.AppendLine("  skipped — no copy.");
                else
                {
                    try
                    {
                        var before = work.get_BoundingBox(null);
                        ElementTransformUtils.MoveElement(doc, work.Id, new XYZ(100, 50, 0));
                        doc.Regenerate();
                        var after = work.get_BoundingBox(null);
                        sb.AppendLine("  before: " + FmtBox(before));
                        sb.AppendLine("  after : " + FmtBox(after));
                    }
                    catch (Exception ex) { sb.AppendLine("  → EXCEPTION: " + ex.Message); }
                }

                // 4d. Rotate the copy 30° about vertical axis through its centre
                Sub(sb, "RotateElement(copy, 30° about Z) — does the box rotate?");
                if (work == null) sb.AppendLine("  skipped — no copy.");
                else
                {
                    try
                    {
                        var bb = work.get_BoundingBox(null);
                        var ctr = bb != null
                            ? new XYZ((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, 0)
                            : XYZ.Zero;
                        var axis = Line.CreateBound(ctr, ctr + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, work.Id, axis, Math.PI / 6);
                        doc.Regenerate();
                        sb.AppendLine("  after : " + FmtBox(work.get_BoundingBox(null)));
                        sb.AppendLine("  (bbox is axis-aligned, so a rotated box reports a larger AABB — expected.)");
                    }
                    catch (Exception ex) { sb.AppendLine("  → EXCEPTION: " + ex.Message); }
                }

                // 4e. Try to resize every writable numeric parameter — the width/depth question
                Sub(sb, "RESIZE — set each writable Double parameter and read the bbox back");
                Element resizeTarget = work ?? seed;
                sb.AppendLine($"  (target: {(work != null ? "copy" : "seed (no copy) — still rolled back")})");
                var before2 = resizeTarget.get_BoundingBox(null);
                sb.AppendLine("  baseline bbox: " + FmtBox(before2));
                bool anyWritableDouble = false;
                foreach (Parameter p in resizeTarget.Parameters.Cast<Parameter>())
                {
                    if (p.IsReadOnly || p.StorageType != StorageType.Double) continue;
                    anyWritableDouble = true;
                    string pname = p.Definition?.Name ?? "(?)";
                    double orig = p.AsDouble();
                    try
                    {
                        p.Set(orig + 10.0);   // +10 ft
                        doc.Regenerate();
                        var nb = resizeTarget.get_BoundingBox(null);
                        sb.AppendLine($"  set '{pname}' {orig:F3}→{orig + 10.0:F3}: bbox {FmtBox(nb)}");
                    }
                    catch (Exception ex) { sb.AppendLine($"  set '{pname}' → EXCEPTION: {ex.Message}"); }
                }
                if (!anyWritableDouble)
                    sb.AppendLine("  → NO writable Double parameters. Width/Depth/Height cannot be set " +
                                  "via parameter on this Revit year — the Creator must log required sizes " +
                                  "for manual handle-resizing.");

                tx.RollBack();
                sb.AppendLine();
                sb.AppendLine("(transaction rolled back — no changes were saved.)");
            }

            // ── 5. View Scope Box parameter writability ───────────────────────────
            Header(sb, "VIEW ASSIGNMENT — is VIEWER_VOLUME_OF_INTEREST_CROP writable?");
            var av = uidoc.ActiveView;
            sb.AppendLine($"Active view : '{av?.Name}' ({av?.ViewType})");
            var vp = av?.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
            if (vp == null)
                sb.AppendLine("  Active view has NO Scope Box parameter (not a croppable plan/section). " +
                              "Activate a floor plan to test assignment.");
            else
            {
                sb.AppendLine($"  Present. IsReadOnly={vp.IsReadOnly}, current={vp.AsElementId()?.Value}");
                using (var tx = new Transaction(doc, "ScopeBox probe view assign (rollback)"))
                {
                    tx.Start();
                    try
                    {
                        vp.Set(seed.Id);
                        doc.Regenerate();
                        sb.AppendLine($"  → Set(seed) OK. View now reports scope box id {vp.AsElementId()?.Value}.");
                    }
                    catch (Exception ex) { sb.AppendLine("  → Set(seed) EXCEPTION: " + ex.Message); }
                    tx.RollBack();
                }
            }

            Header(sb, "SUMMARY / NEXT STEP");
            sb.AppendLine("Read sections 4a–4e: they tell us exactly which of copy / rename / move /");
            sb.AppendLine("rotate / resize succeed on this Revit year. Send this file back so the Scope Box");
            sb.AppendLine("Creator's sizing path can be finalised (parameter resize vs. manual-resize log).");
        }

        // ── Formatting helpers ───────────────────────────────────────────────────
        private static void DumpParam(StringBuilder sb, Parameter p)
        {
            string name = p.Definition?.Name ?? "(no def)";
            string bip  = (p.Definition as InternalDefinition)?.BuiltInParameter.ToString() ?? "";
            string ro   = p.IsReadOnly ? "R" : "";
            string val;
            switch (p.StorageType)
            {
                case StorageType.Double:    val = $"{p.AsDouble():F4} ({p.AsValueString()})"; break;
                case StorageType.Integer:   val = p.AsInteger().ToString(); break;
                case StorageType.String:    val = p.AsString() ?? ""; break;
                case StorageType.ElementId: val = p.AsElementId()?.Value.ToString() ?? ""; break;
                default:                    val = "(none)"; break;
            }
            if (name.Length > 33) name = name.Substring(0, 32) + "…";
            if (bip.Length  > 39) bip  = bip.Substring(0, 38) + "…";
            sb.AppendLine($"{name,-34}{bip,-40}{p.StorageType,-10}{ro,-4}{val}");
        }

        private static void DumpBBox(StringBuilder sb, BoundingBoxXYZ? bb)
        {
            if (bb == null) { sb.AppendLine("  bbox: null"); return; }
            double w = bb.Max.X - bb.Min.X, d = bb.Max.Y - bb.Min.Y, h = bb.Max.Z - bb.Min.Z;
            sb.AppendLine($"  bbox min ({bb.Min.X:F3},{bb.Min.Y:F3},{bb.Min.Z:F3}) " +
                          $"max ({bb.Max.X:F3},{bb.Max.Y:F3},{bb.Max.Z:F3})");
            sb.AppendLine($"  size  W={w:F3}  D={d:F3}  H={h:F3} ft");
        }

        private static string FmtBox(BoundingBoxXYZ? bb)
        {
            if (bb == null) return "null";
            double w = bb.Max.X - bb.Min.X, d = bb.Max.Y - bb.Min.Y, h = bb.Max.Z - bb.Min.Z;
            return $"min({bb.Min.X:F2},{bb.Min.Y:F2},{bb.Min.Z:F2}) W={w:F2} D={d:F2} H={h:F2}";
        }

        private static void Header(StringBuilder sb, string t)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', 72));
            sb.AppendLine("  " + t);
            sb.AppendLine(new string('=', 72));
        }

        private static void Sub(StringBuilder sb, string t)
        {
            sb.AppendLine();
            sb.AppendLine("  ── " + t);
        }

        private static void Write(StringBuilder sb)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LemoineTools");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "ScopeBoxProbe.txt");
                File.WriteAllText(path, sb.ToString());
                Process.Start("notepad.exe", path);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ScopeBoxProbe: write report", ex); }
        }
    }
}
