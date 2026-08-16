using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZoneTitleBlocks — sheet sizes and usable drawing areas.
    //
    // A layout is keyed by title block TYPE because the sheet size is what makes
    // one placement different from another. This resolves that type to a real
    // rectangle, two ways:
    //
    //   MEASURED  — the placed title block instance's bounding box on an actual
    //               sheet. Only valid after a regeneration, and only available
    //               once a sheet using that type exists.
    //   DECLARED  — SHEET_WIDTH / SHEET_HEIGHT on the title block SYMBOL. Works
    //               with no sheet at all, which is the case while a zone library
    //               is still being authored.
    //
    // Both are offered, MEASURED is preferred, and which one was used is always
    // reported — an estimate that is silently presented as a measurement is how
    // a placement ends up subtly wrong with nothing to point at.
    // =========================================================================
    public static class ZoneTitleBlocks
    {
        /// <summary>A title block type and the sheet size it declares.</summary>
        public sealed class TitleBlockType
        {
            public ElementId Id   = ElementId.InvalidElementId;
            /// <summary>"Family : Type" — the name a layout stores.</summary>
            public string    Name = "";
            public string    FamilyName = "";
            public string    TypeName   = "";
            public double    WidthFt    { get; set; }
            public double    HeightFt   { get; set; }
            /// <summary>False when the type declares no readable size.</summary>
            public bool      HasSize    { get; set; }

            public string SizeLabel => HasSize
                ? $"{WidthFt * 12.0:0.#}\" × {HeightFt * 12.0:0.#}\""
                : "size unknown";
        }

        /// <summary>Canonical name for a title block type: "Family : Type".</summary>
        public static string NameOf(FamilySymbol? symbol)
        {
            if (symbol == null) return "";
            string fam = symbol.Family?.Name ?? "";
            string typ = symbol.Name ?? "";
            return string.IsNullOrEmpty(fam) ? typ : fam + " : " + typ;
        }

        /// <summary>
        /// Every title block type in the document, with its declared sheet size. Read-only.
        /// Reports an empty result explicitly rather than returning a silent empty list.
        /// </summary>
        public static List<TitleBlockType> Collect(Document? doc, Action<string, string>? log = null)
        {
            var list = new List<TitleBlockType>();
            if (doc == null) return list;

            try
            {
                foreach (var sym in new FilteredElementCollector(doc)
                             .OfCategory(BuiltInCategory.OST_TitleBlocks)
                             .WhereElementIsElementType()
                             .Cast<FamilySymbol>())
                {
                    var t = new TitleBlockType
                    {
                        Id         = sym.Id,
                        Name       = NameOf(sym),
                        FamilyName = sym.Family?.Name ?? "",
                        TypeName   = sym.Name ?? "",
                    };

                    // SHEET_WIDTH / SHEET_HEIGHT are on the type and are in internal units
                    // (feet), which is the same space every other measurement here uses.
                    try
                    {
                        var pw = sym.get_Parameter(BuiltInParameter.SHEET_WIDTH);
                        var ph = sym.get_Parameter(BuiltInParameter.SHEET_HEIGHT);
                        if (pw != null && ph != null)
                        {
                            t.WidthFt  = pw.AsDouble();
                            t.HeightFt = ph.AsDouble();
                            t.HasSize  = t.WidthFt > 0 && t.HeightFt > 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed($"ZoneTitleBlocks: read size of '{t.Name}'", ex);
                    }

                    if (!t.HasSize)
                        log?.Invoke($"Title block '{t.Name}' does not declare a sheet size — " +
                                    "placements for it must be measured from a real sheet.", "warn");

                    list.Add(t);
                }

                list.Sort((a, b) => NaturalOrderComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

                if (list.Count == 0)
                    log?.Invoke("No title block types found in this document.", "warn");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneTitleBlocks: collect types", ex);
            }
            return list;
        }

        /// <summary>How a drawing area was arrived at.</summary>
        public enum AreaSource
        {
            /// <summary>From a placed title block instance's bounding box.</summary>
            Measured,
            /// <summary>From the type's declared SHEET_WIDTH / SHEET_HEIGHT.</summary>
            Declared,
            /// <summary>Neither was available.</summary>
            Unknown,
        }

        public sealed class DrawingAreaResult
        {
            public ZoneGroupSolver.DrawingArea? Area { get; set; }
            public AreaSource Source { get; set; } = AreaSource.Unknown;
            public double SheetWidthFt  { get; set; }
            public double SheetHeightFt { get; set; }
            public bool   Ok => Area != null && Area.WidthFt > 0 && Area.HeightFt > 0;
        }

        /// <summary>
        /// The usable drawing area for a layout. Prefers a real placed title block on
        /// <paramref name="sheet"/>; falls back to the type's declared size.
        ///
        /// The title block instance bounding box is only valid after a regeneration, which is
        /// the caller's responsibility — passing a sheet whose viewports were just created
        /// without regenerating will read stale geometry.
        /// </summary>
        public static DrawingAreaResult Resolve(Document? doc, ZoneSheetLayout layout,
                                                ViewSheet? sheet = null,
                                                Action<string, string>? log = null)
        {
            var result = new DrawingAreaResult();
            if (doc == null || layout == null) return result;

            // ── Measured ──────────────────────────────────────────────────────
            if (sheet != null)
            {
                try
                {
                    var tb = new FilteredElementCollector(doc, sheet.Id)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .WhereElementIsNotElementType()
                        .FirstElement();

                    BoundingBoxXYZ? bb = tb?.get_BoundingBox(sheet);
                    if (bb != null)
                    {
                        result.SheetWidthFt  = bb.Max.X - bb.Min.X;
                        result.SheetHeightFt = bb.Max.Y - bb.Min.Y;
                        result.Area = new ZoneGroupSolver.DrawingArea
                        {
                            MinX = bb.Min.X + layout.MarginLeftFt,
                            MinY = bb.Min.Y + layout.MarginBottomFt,
                            MaxX = bb.Max.X - layout.MarginRightFt,
                            MaxY = bb.Max.Y - layout.MarginTopFt,
                        };
                        result.Source = AreaSource.Measured;
                        if (result.Ok) return result;
                        result.Area = null;   // margins ate the whole sheet — fall through
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("ZoneTitleBlocks: measure title block on sheet", ex);
                }
            }

            // ── Declared ──────────────────────────────────────────────────────
            var type = Collect(doc).FirstOrDefault(
                t => string.Equals(t.Name, layout.TitleBlockTypeName, StringComparison.OrdinalIgnoreCase));

            if (type != null && type.HasSize)
            {
                result.SheetWidthFt  = type.WidthFt;
                result.SheetHeightFt = type.HeightFt;
                result.Area = ZoneGroupSolver.DrawingArea.FromSize(
                    type.WidthFt, type.HeightFt,
                    layout.MarginLeftFt, layout.MarginRightFt,
                    layout.MarginBottomFt, layout.MarginTopFt);
                result.Source = AreaSource.Declared;

                if (result.Ok)
                {
                    // Never let an estimate pass as a measurement.
                    log?.Invoke($"Layout '{layout.Name}': drawing area estimated from the title block's " +
                                "declared size — no placed sheet was available to measure.", "info");
                    return result;
                }
                result.Area = null;
            }

            log?.Invoke($"Layout '{layout.Name}': could not determine a drawing area for title block " +
                        $"'{layout.TitleBlockTypeName}'. Placements for it cannot be solved.", "fail");
            result.Source = AreaSource.Unknown;
            return result;
        }

        /// <summary>
        /// Warns when a layout's recorded sheet size no longer matches the type it names — a
        /// title block edited after placements were stored silently invalidates them.
        /// </summary>
        public static bool CheckRecordedSize(ZoneSheetLayout layout, double actualW, double actualH,
                                             Action<string, string>? log = null)
        {
            if (layout == null) return true;
            if (layout.SheetWidthFt <= 0 || layout.SheetHeightFt <= 0) return true;   // never recorded

            const double tolFt = 1e-4;
            if (Math.Abs(layout.SheetWidthFt - actualW) <= tolFt &&
                Math.Abs(layout.SheetHeightFt - actualH) <= tolFt)
                return true;

            log?.Invoke($"Layout '{layout.Name}': title block '{layout.TitleBlockTypeName}' is now " +
                        $"{actualW * 12.0:0.#}\" × {actualH * 12.0:0.#}\" but placements were stored against " +
                        $"{layout.SheetWidthFt * 12.0:0.#}\" × {layout.SheetHeightFt * 12.0:0.#}\". " +
                        "Stored placements will not land where they did before.", "warn");
            return false;
        }
    }
}
