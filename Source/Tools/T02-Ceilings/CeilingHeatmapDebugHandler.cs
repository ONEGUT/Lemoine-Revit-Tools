using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Ceilings
{
    /// <summary>
    /// Runs read-only diagnostics against the active ViewPlan and writes a plain-text
    /// report to %AppData%\LemoineTools\CeilingHeatmapDiag.txt, then opens it in Notepad.
    ///
    /// Checks every value the heatmap tool has been guessing at:
    ///   • All three crop-box approaches (raw, transform-applied, GetCropShape)
    ///   • Linked ceiling counts under each approach
    ///   • Full reflection dump of LinkElementId properties
    ///   • GetTaggedLocalElementIds / GetTaggedElementIds return values
    ///   • CreateLinkReference validity
    /// </summary>
    public class CeilingHeatmapDebugHandler : IExternalEventHandler
    {
        public string GetName() => "LemoineTools.Tools.Ceilings.CeilingHeatmapDebugHandler";

        public void Execute(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            var doc   = uidoc?.Document;
            if (doc == null) return;

            var sb = new StringBuilder();
            Header(sb, "CEILING HEATMAP DIAGNOSTICS");
            sb.AppendLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Document  : {doc.Title}");
            sb.AppendLine();

            var view = uidoc.ActiveView as ViewPlan;
            if (view == null)
            {
                sb.AppendLine("Active view is NOT a ViewPlan.");
                sb.AppendLine("Please activate a Reflected Ceiling Plan and run again.");
                Write(sb); return;
            }

            // ═════════════════════════════════════════════════════════════════
            // 1. VIEW INFO
            // ═════════════════════════════════════════════════════════════════
            Header(sb, "ACTIVE VIEW");
            sb.AppendLine($"Name          : {view.Name}");
            sb.AppendLine($"ViewType      : {view.ViewType}");
            sb.AppendLine($"CropBoxActive : {view.CropBoxActive}");
            sb.AppendLine($"CropBoxVisible: {view.CropBoxVisible}");
            sb.AppendLine($"GenLevel      : {view.GenLevel?.Name ?? "null"}");
            sb.AppendLine($"Level Elev    : {view.GenLevel?.Elevation:F6} ft");

            // ── A: Raw CropBox ────────────────────────────────────────────────
            Sub(sb, "A — CropBox raw Min / Max");
            var cb = view.CropBox;
            Xyz(sb, "Min          ", cb.Min);
            Xyz(sb, "Max          ", cb.Max);
            sb.AppendLine();
            Xyz(sb, "Transform.Origin", cb.Transform.Origin);
            Xyz(sb, "Transform.BasisX", cb.Transform.BasisX);
            Xyz(sb, "Transform.BasisY", cb.Transform.BasisY);
            Xyz(sb, "Transform.BasisZ", cb.Transform.BasisZ);

            // ── B: CropBox with Transform applied ─────────────────────────────
            Sub(sb, "B — CropBox.Transform.OfPoint( Min / Max )");
            var bMin = cb.Transform.OfPoint(cb.Min);
            var bMax = cb.Transform.OfPoint(cb.Max);
            Xyz(sb, "WorldMin", bMin);
            Xyz(sb, "WorldMax", bMax);

            // ── C: GetCropRegionShapeManager ──────────────────────────────────
            Sub(sb, "C — GetCropRegionShapeManager().GetCropShape()");
            double cMinX = double.MaxValue, cMinY = double.MaxValue;
            double cMaxX = double.MinValue, cMaxY = double.MinValue;
            bool   cGot  = false;
            try
            {
                var loops = view.GetCropRegionShapeManager().GetCropShape();
                sb.AppendLine($"Loop count : {loops.Count}");
                int li = 0;
                foreach (CurveLoop loop in loops)
                {
                    var pts = loop.SelectMany(c => c.Tessellate()).ToList();
                    sb.AppendLine($"  Loop {li++}: {pts.Count} tessellated pts");
                    foreach (XYZ pt in pts)
                    {
                        sb.AppendLine($"    {Fmt(pt)}");
                        if (pt.X < cMinX) cMinX = pt.X;
                        if (pt.Y < cMinY) cMinY = pt.Y;
                        if (pt.X > cMaxX) cMaxX = pt.X;
                        if (pt.Y > cMaxY) cMaxY = pt.Y;
                        cGot = true;
                    }
                }
                if (cGot)
                {
                    sb.AppendLine($"Computed XY Min : X={cMinX,10:F4}  Y={cMinY,10:F4}");
                    sb.AppendLine($"Computed XY Max : X={cMaxX,10:F4}  Y={cMaxY,10:F4}");
                }
                else sb.AppendLine("(no points returned)");
            }
            catch (Exception ex) { sb.AppendLine($"EXCEPTION: {ex.Message}"); }

            // ═════════════════════════════════════════════════════════════════
            // 2. LINKED DOCUMENTS IN VIEW
            // ═════════════════════════════════════════════════════════════════
            Header(sb, "LINKED DOCUMENTS IN VIEW");
            var links = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();
            sb.AppendLine($"RevitLinkInstances visible in view: {links.Count}");

            foreach (RevitLinkInstance link in links)
            {
                Document linkDoc = link.GetLinkDocument();
                Sub(sb, $"LINK: {link.Name}  (Id: {link.Id.Value})");
                sb.AppendLine($"Link doc      : {linkDoc?.Title ?? "null — GetLinkDocument() returned null"}");
                if (linkDoc == null) continue;

                Transform xf = link.GetTotalTransform();
                sb.AppendLine($"IsIdentity    : {xf.IsIdentity}");
                Xyz(sb, "Origin        ", xf.Origin);
                Xyz(sb, "BasisX        ", xf.BasisX);
                Xyz(sb, "BasisY        ", xf.BasisY);
                sb.AppendLine();

                // Total (unfiltered) ceilings
                int total = new FilteredElementCollector(linkDoc)
                    .OfClass(typeof(Ceiling)).WhereElementIsNotElementType().Count();
                sb.AppendLine($"Total ceilings in link (no filter) : {total}");

                Transform inv = xf.Inverse;

                // Approach A: raw Min/Max
                sb.AppendLine($"Approach A (raw Min/Max)           : {CountWithOutline(linkDoc, BuildOutline(inv, cb.Min, cb.Max))}");

                // Approach B: Transform.OfPoint
                sb.AppendLine($"Approach B (Transform.OfPoint)     : {CountWithOutline(linkDoc, BuildOutline(inv, bMin, bMax))}");

                // Approach C: CropShape
                if (cGot)
                    sb.AppendLine($"Approach C (GetCropShape)          : {CountWithOutline(linkDoc, BuildOutline(inv, new XYZ(cMinX, cMinY, 0), new XYZ(cMaxX, cMaxY, 0)))}");
                else
                    sb.AppendLine($"Approach C (GetCropShape)          : skipped — no shape points");

                // CreateLinkReference test on the first ceiling in the link
                Sub(sb, "CreateLinkReference test");
                Element first = new FilteredElementCollector(linkDoc)
                    .OfClass(typeof(Ceiling)).WhereElementIsNotElementType().FirstOrDefault();
                if (first == null)
                {
                    sb.AppendLine("(no ceilings in link doc to test with)");
                }
                else
                {
                    sb.AppendLine($"Test ceiling Id : {first.Id.Value}");
                    try
                    {
                        Reference r = new Reference(first).CreateLinkReference(link);
                        sb.AppendLine($"CreateLinkReference → OK");
                        sb.AppendLine($"Stable repr : {r.ConvertToStableRepresentation(doc)}");
                    }
                    catch (Exception ex) { sb.AppendLine($"CreateLinkReference → EXCEPTION: {ex.Message}"); }
                }
            }

            // ═════════════════════════════════════════════════════════════════
            // 3. EXISTING CEILING TAGS IN VIEW
            // ═════════════════════════════════════════════════════════════════
            Header(sb, "EXISTING CEILING TAGS IN VIEW");
            var ceilTagCatId = new ElementId(BuiltInCategory.OST_CeilingTags);
            var tags = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>()
                .Where(t => t.Category?.Id == ceilTagCatId)
                .Take(5)   // sample first 5
                .ToList();
            sb.AppendLine($"Ceiling-category IndependentTags in view: {tags.Count} (showing up to 5)");

            foreach (IndependentTag tag in tags)
            {
                Sub(sb, $"Tag Id: {tag.Id.Value}");

                // TaggedLocalElementId is NOT present in this Revit API version —
                // confirmed by CS1061 at compile time.  Use GetTaggedLocalElementIds() instead.
                sb.AppendLine("  TaggedLocalElementId            : N/A — removed in this Revit API version");

                // GetTaggedLocalElementIds
                Try(sb, "GetTaggedLocalElementIds()",
                    () =>
                    {
                        var ids = tag.GetTaggedLocalElementIds();
                        return $"count={ids.Count}  values=[{string.Join(", ", ids.Select(x => x.Value))}]";
                    });

                // GetTaggedElementIds — call and reflect on every property of each returned item.
                // CONFIRMED by diagnostics: HostElementId = -1 (always invalid for linked tags).
                // Use LinkInstanceId to match the RevitLinkInstance, LinkedElementId for the ceiling.
                sb.AppendLine();
                sb.AppendLine("  GetTaggedElementIds():");
                try
                {
                    var result = tag.GetTaggedElementIds();
                    sb.AppendLine($"    count = {result.Count}");
                    int idx = 0;
                    foreach (var item in result)
                    {
                        sb.AppendLine($"    item[{idx++}]  type = {item.GetType().FullName}");
                        foreach (PropertyInfo prop in item.GetType()
                            .GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            try
                            {
                                object val = prop.GetValue(item);
                                sb.AppendLine($"      {prop.Name,-30} = {val}");
                            }
                            catch (Exception ex)
                            {
                                sb.AppendLine($"      {prop.Name,-30} → GET threw: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"    EXCEPTION: {ex.Message}");
                }
            }

            // ═════════════════════════════════════════════════════════════════
            // 4. ALL IndependentTag METHODS (reflect once on the first tag)
            // ═════════════════════════════════════════════════════════════════
            if (tags.Count > 0)
            {
                Header(sb, "IndependentTag PUBLIC METHODS (first tag)");
                foreach (MethodInfo m in tags[0].GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(m => m.Name))
                {
                    var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    sb.AppendLine($"  {m.ReturnType.Name,-30} {m.Name}({parms})");
                }
            }

            Write(sb);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────
        private static int CountWithOutline(Document linkDoc, Outline outline)
        {
            try
            {
                return new FilteredElementCollector(linkDoc)
                    .OfClass(typeof(Ceiling))
                    .WherePasses(new BoundingBoxIntersectsFilter(outline))
                    .WhereElementIsNotElementType()
                    .Count();
            }
            catch { return -1; /* outline may be degenerate */ }
        }

        private static Outline BuildOutline(Transform invLinkXform, XYZ worldMin, XYZ worldMax)
        {
            XYZ p1 = invLinkXform.OfPoint(new XYZ(worldMin.X, worldMin.Y, 0));
            XYZ p2 = invLinkXform.OfPoint(new XYZ(worldMax.X, worldMax.Y, 0));
            return new Outline(
                new XYZ(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), -1000.0),
                new XYZ(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y),  1000.0));
        }

        private static void Try(StringBuilder sb, string label, Func<string> fn)
        {
            try   { sb.AppendLine($"  {label,-38}: {fn()}"); }
            catch (Exception ex) { sb.AppendLine($"  {label,-38}: EXCEPTION — {ex.Message}"); }
        }

        private static void Xyz(StringBuilder sb, string label, XYZ v)
            => sb.AppendLine($"  {label,-16}: {Fmt(v)}");

        private static string Fmt(XYZ v)
            => $"X={v.X,10:F4}  Y={v.Y,10:F4}  Z={v.Z,10:F4}";

        private static void Header(StringBuilder sb, string title)
        {
            sb.AppendLine();
            sb.AppendLine(new string('═', 64));
            sb.AppendLine($"  {title}");
            sb.AppendLine(new string('═', 64));
        }

        private static void Sub(StringBuilder sb, string title)
        {
            sb.AppendLine();
            sb.AppendLine($"  ── {title}");
        }

        private static void Write(StringBuilder sb)
        {
            try
            {
                string dir  = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "CeilingHeatmapDiag.txt");
                File.WriteAllText(path, sb.ToString());
                Process.Start("notepad.exe", path);
            }
            catch (Exception __lex) { LemoineLog.Swallowed("CeilingHeatmap debug: write diagnostics line", __lex); }
        }
    }
}
