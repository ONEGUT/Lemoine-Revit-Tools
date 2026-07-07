using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>
    /// Shared reader for the chosen source (host or a link): resolves the document + transform,
    /// collects the category-filtered, workset-filtered elements, and reads the system / size /
    /// phase keywords the SAME way for both the phase-1 scan and the run — so a value offered as a
    /// filter is exactly the value the run compares against.
    /// </summary>
    public static class CopyLinearSource
    {
        /// <summary>Resolved source: its document, the link instance (null for host), and the link transform.</summary>
        public sealed class ResolvedSource
        {
            public Document?          Doc;
            public RevitLinkInstance? Link;
            public Transform          Transform = Transform.Identity;
            public string             LinkInstUid = "host";
        }

        public static ResolvedSource? Resolve(Document hostDoc, long linkInstId)
        {
            if (hostDoc == null) return null;
            if (linkInstId == 0L)
                return new ResolvedSource { Doc = hostDoc, Link = null, Transform = Transform.Identity, LinkInstUid = "host" };

            try
            {
                var li = hostDoc.GetElement(new ElementId(linkInstId)) as RevitLinkInstance;
                var ld = li?.GetLinkDocument();
                if (li == null || ld == null) return null;
                return new ResolvedSource
                {
                    Doc         = ld,
                    Link        = li,
                    Transform   = li.GetTotalTransform(),
                    LinkInstUid = li.UniqueId,
                };
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("CopyLinearSource.Resolve", ex);
                return null;
            }
        }

        /// <summary>Category-filtered, workset-filtered elements of the source document.</summary>
        public static List<Element> Collect(Document srcDoc, CopyLinearSourceSpec spec)
        {
            var result = new List<Element>();
            if (srcDoc == null || spec == null) return result;

            var excluded = new HashSet<int>(spec.ExcludedWorksetIds ?? new List<int>());
            bool checkWs = excluded.Count > 0 && SafeIsWorkshared(srcDoc);

            foreach (var ost in spec.Categories ?? new List<string>())
            {
                if (!TryParseCategory(ost, out var bic)) continue;
                IList<Element> elems;
                try
                {
                    elems = new FilteredElementCollector(srcDoc)
                        .OfCategory(bic)
                        .WhereElementIsNotElementType()
                        .ToElements();
                }
                catch (Exception ex) { LemoineLog.Swallowed($"CopyLinearSource.Collect: {ost}", ex); continue; }

                foreach (var el in elems)
                {
                    if (checkWs && IsExcludedWorkset(el, excluded)) continue;
                    result.Add(el);
                }
            }
            return result;
        }

        /// <summary>True when the element passes every active parameter filter (empty value list = pass all).</summary>
        public static bool PassesFilters(Element el, CopyLinearSourceSpec spec)
        {
            if (spec?.ParamFilters == null || spec.ParamFilters.Count == 0) return true;
            foreach (var kv in spec.ParamFilters)
            {
                if (kv.Value == null || kv.Value.Count == 0) continue;
                try
                {
                    string val = ReadParamDisplay(el?.LookupParameter(kv.Key));
                    if (!kv.Value.Contains(val)) return false;
                }
                catch (Exception ex) { LemoineLog.Swallowed($"CopyLinearSource.PassesFilters: param '{kv.Key}'", ex); }
            }
            return true;
        }

        /// <summary>
        /// Formatted display string for a parameter value. Returns "(no value)" when the parameter
        /// is absent, has no value, or uses Double storage (numerics are excluded from filter chips).
        /// </summary>
        public static string ReadParamDisplay(Parameter? p)
        {
            if (p == null || p.StorageType == StorageType.None || p.StorageType == StorageType.Double)
                return "(no value)";
            if (p.StorageType == StorageType.String)
            {
                var s = p.AsString();
                return string.IsNullOrEmpty(s) ? "(no value)" : s;
            }
            var vs = p.AsValueString();
            return string.IsNullOrEmpty(vs) ? "(no value)" : vs;
        }

        // ── Keyword readers (one source of truth for scan + run) ───────────────

        public static string ReadSystem(Element el)
        {
            try
            {
                if (el is MEPCurve mc)
                {
                    var sysName = mc.MEPSystem?.Name;
                    if (!string.IsNullOrWhiteSpace(sysName)) return sysName!;
                }
                var p = el?.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM);
                var v = p?.AsValueString() ?? p?.AsString();
                return string.IsNullOrWhiteSpace(v) ? "(none)" : v!;
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinearSource.ReadSystem", ex); return "(none)"; }
        }

        public static string ReadSize(Element el)
        {
            try
            {
                // Rectangular first (a round duct can also expose an equivalent diameter — CLAUDE.md).
                var w = el?.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                var h = el?.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                if (w != null && h != null && w.HasValue && h.HasValue)
                {
                    string ws = w.AsValueString() ?? w.AsDouble().ToString("F3");
                    string hs = h.AsValueString() ?? h.AsDouble().ToString("F3");
                    return ws + " x " + hs;
                }
                foreach (var bip in new[] { BuiltInParameter.RBS_PIPE_DIAMETER_PARAM,
                                            BuiltInParameter.RBS_CURVE_DIAMETER_PARAM })
                {
                    var d = el?.get_Parameter(bip);
                    if (d != null && d.HasValue)
                        return "Ø " + (d.AsValueString() ?? d.AsDouble().ToString("F3"));
                }
                return "(no size)";
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinearSource.ReadSize", ex); return "(no size)"; }
        }

        public static string ReadPhase(Document srcDoc, Element el)
        {
            try
            {
                var id = el?.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return "(none)";
                var ph = srcDoc?.GetElement(id);
                return string.IsNullOrWhiteSpace(ph?.Name) ? "(none)" : ph!.Name;
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinearSource.ReadPhase", ex); return "(none)"; }
        }

        /// <summary>The element's straight LocationCurve as an [A,B] line, or null when it is not a straight run.</summary>
        public static (XYZ A, XYZ B)? StraightLine(Element el)
        {
            if (el?.Location is LocationCurve lc && lc.Curve is Line ln)
                return (ln.GetEndPoint(0), ln.GetEndPoint(1));
            return null;
        }

        // ── helpers ────────────────────────────────────────────────────────────

        public static bool TryParseCategory(string ost, out BuiltInCategory bic)
        {
            bic = BuiltInCategory.INVALID;
            if (string.IsNullOrWhiteSpace(ost)) return false;
            try { bic = (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), ost); return true; }
            catch { return false; }
        }

        private static bool SafeIsWorkshared(Document d)
        {
            try { return d.IsWorkshared; } catch { return false; }
        }

        private static bool IsExcludedWorkset(Element el, HashSet<int> excluded)
        {
            try { return excluded.Contains(el.WorksetId.IntegerValue); }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinearSource: read element workset", ex); return false; }
        }
    }
}
