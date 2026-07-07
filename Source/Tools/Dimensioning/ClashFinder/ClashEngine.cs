using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Tools.AutoFilters;

using RevitColor = Autodesk.Revit.DB.Color;

namespace LemoineTools.Tools.Dimensioning
{
    /// <summary>Marking options for one clash definition (lifted from ClashDimensionSettings).</summary>
    public struct ClashMarkingOptions
    {
        public double ToleranceMm;
        public string FillStyle;          // "Solid" | "Outline"
        public string FallbackColorHex;   // colour for clashes matching no Auto Filter rule
        public string CrossLineTypeName;  // "" = default line style
        public string DimTarget;          // "Edge" | "Centre"
        public int    MaxClashes;
        public double StoreyMarginMm;     // depth below a level still counted as its storey (slabs/structure)
        public double RoundSizeMm;        // marker oversize margin added to the Group 1 element size; 0 = exact
        public string PhaseMode;          // "All" (null/empty = All) | "MatchView" | "Specific"
        public string SpecificPhaseName;  // host phase name, used only when PhaseMode == "Specific"
        public bool   ElevationMode;      // true → draw the round in the view's vertical plane (sections/elevations)
                                          // with a single tagged diameter line for the spot-elevation pass
    }

    /// <summary>Aggregate result of one engine run.</summary>
    public struct ClashEngineResult
    {
        public int Markers;   // filled-region + cross-line markers placed
        public int Fails;     // per-view marker failures
        public int Clashes;   // distinct clashes detected
    }

    /// <summary>
    /// Detection + marking engine for the Clash Finder: scans two groups, finds solid
    /// intersections, and draws a coloured filled region + tagged cross lines per clash.
    /// Detection + marking only — all dimension placement lives in the AutoDimension engine.
    ///
    /// <see cref="Run"/> executes inside the caller's open transaction; it never opens or
    /// commits one. Markers are tagged via <see cref="ClashTagSchema"/>.
    /// </summary>
    public sealed class ClashEngine
    {
        private readonly ClashMarkingOptions     _opts;
        private readonly Action<string, string>  _log;

        public ClashEngine(ClashMarkingOptions opts, Action<string, string> log)
        {
            _opts = opts;
            _log  = log ?? ((a, b) => { });
        }

        private void Log(string text, string status) => _log(text, status);

        // ── BIP map for fast parameter resolution ─────────────────────────────
        private static readonly Dictionary<string, BuiltInParameter> BipMap =
            new Dictionary<string, BuiltInParameter>(StringComparer.Ordinal)
            {
                ["System Classification"] = BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM,
                ["Fabrication Service"]   = BuiltInParameter.FABRICATION_SERVICE_NAME,
                ["Type Name"]             = BuiltInParameter.ALL_MODEL_TYPE_NAME,
                ["Family Name"]           = BuiltInParameter.ELEM_FAMILY_PARAM,
                ["Structural Material"]   = BuiltInParameter.STRUCTURAL_MATERIAL_PARAM,
            };

        // ── Inner types (internal: surfaced through the public ClashDetection's internal fields) ──
        internal class ClashElement
        {
            public Document           Doc          = null!;
            public RevitLinkInstance? LinkInstance;
            public Transform          HostTransform = Transform.Identity;
            public ElementId          Id            = ElementId.InvalidElementId;
            public string             Label         = "";
            public string             ColorHex      = "#888888";
            public bool               RuleColored   = true;
            public BoundingBoxXYZ     HostBBox      = null!;
            public Solid?             HostSolid;
            public bool               SolidTried;
            // Cross-section of the element (inherited by the marker drawn for its clashes).
            public bool               IsRectangular; // true → rectangular duct/tray; false → round / unknown
            public double             WidthFt;       // round: diameter; rectangular: width; 0 = unknown
            public double             HeightFt;      // round: diameter; rectangular: height; 0 = unknown
            public XYZ?               WidthDir;      // world unit vector of the rectangular width axis (null → view-aligned)
            public XYZ?               HeightDir;     // world unit vector of the rectangular height axis
            // Phase names read in the element's OWN document ("" = none/unread). Linked-model
            // phases are mapped to the host by NAME against ClashDetection.HostPhaseSeq.
            public string             CreatedPhaseName    = "";
            public string             DemolishedPhaseName = "";
        }

        internal class ClashResult
        {
            public ClashElement   Group1      = null!;
            public ClashElement   Group2      = null!;
            public BoundingBoxXYZ OverlapBBox = null!;
        }

        // ── View-independent detection output ─────────────────────────────────
        /// <summary>What <see cref="Detect"/> produces once and <see cref="PlaceInView"/> consumes per
        /// view: the clashes (model-wide) plus the per-view gating data and marking context. Lets a
        /// multi-view run scan the model only once and place markers view-by-view.</summary>
        public sealed class ClashDetection
        {
            // Internal so the enclosing ClashEngine (same assembly) reads/writes them. The
            // ClashResult/ClashElement types are likewise internal, so an internal field is never
            // backed by a less-accessible type (CS0052).
            internal List<ClashResult> Clashes = new List<ClashResult>();
            internal ElementId LineStyleId = ElementId.InvalidElementId;
            internal List<double> LevelElevs = new List<double>();
            internal double StoreyMarginFt;
            internal double ToleranceFt;
            /// <summary>Host phase name → sequence index, in phase order. The host timeline is
            /// the source of truth for every phase comparison (a view phase is a host phase);
            /// link phases match it by name. Built once in Detect, like LevelElevs.</summary>
            internal Dictionary<string, int> HostPhaseSeq = new Dictionary<string, int>(StringComparer.Ordinal);
            /// <summary>Phase names already warned about as unmapped — one log line per name.</summary>
            internal HashSet<string> WarnedPhaseNames = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>True when detection could not run (a group produced no elements).</summary>
            public bool Failed { get; internal set; }
            /// <summary>Distinct clashes detected, model-wide.</summary>
            public int ClashCount => Clashes.Count;
        }

        /// <summary>Per-view marker placement tally.</summary>
        public struct PlacementResult { public int Markers; public int Fails; public int Skipped; }

        // ── Entry point (detect once, then place in every view) ───────────────
        public ClashEngineResult Run(
            Document doc, IList<ElementId> viewIds,
            ClashGroupSpec group1Spec, ClashGroupSpec group2Spec)
        {
            var result = new ClashEngineResult();
            var det = Detect(doc, group1Spec, group2Spec);
            result.Clashes = det.ClashCount;
            if (det.Failed) { result.Fails++; return result; }
            if (det.ClashCount == 0) return result;

            Log($"Placing markers for {det.ClashCount} clash(es) across {viewIds.Count} view(s)…", "info");
            var regionTypeCache = new Dictionary<string, ElementId?>();
            foreach (var viewId in viewIds)
            {
                if (!(doc.GetElement(viewId) is View view)) continue;
                var pr = PlaceInView(doc, view, det, regionTypeCache);
                result.Markers += pr.Markers;
                result.Fails   += pr.Fails;
            }
            return result;
        }

        /// <summary>
        /// Scans both groups and finds solid intersections — view-independent, no element changes,
        /// safe outside a transaction. Logs the group counts, the no-elements / clash-limit cases.
        /// </summary>
        public ClashDetection Detect(Document doc, ClashGroupSpec group1Spec, ClashGroupSpec group2Spec)
        {
            var det = new ClashDetection();

            // 1. Source documents (host + all loaded links)
            var sources = new List<(Document doc, RevitLinkInstance? link, Transform tx)>
            {
                (doc, null, Transform.Identity)
            };
            foreach (var li in new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld == null) continue;
                sources.Add((ld, li, li.GetTotalTransform()));
            }

            // Host phase timeline (name → sequence), used by both phase modes. Built up front,
            // like LevelElevs — link phase names are compared against this host sequence.
            try
            {
                foreach (Phase ph in doc.Phases)
                    det.HostPhaseSeq[ph.Name] = det.HostPhaseSeq.Count;
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read host phases", ex); }

            // 2. Scan each group per its mode
            var group1Elements = ScanGroupSpec(group1Spec, sources, "Group 1");
            var group2Elements = ScanGroupSpec(group2Spec, sources, "Group 2");
            int group1Rect = group1Elements.Count(e => e.IsRectangular);
            Log($"Group 1: {group1Elements.Count} element(s) ({group1Rect} rectangular)   Group 2: {group2Elements.Count} element(s)", "info");

            if (group1Elements.Count == 0)
            {
                Log("Group 1 produced no elements — check its mode, selection, and source documents.", "fail");
                det.Failed = true; return det;
            }
            if (group2Elements.Count == 0)
            {
                Log("Group 2 produced no elements — check its mode, selection, and source documents.", "fail");
                det.Failed = true; return det;
            }

            // 2b. Specific-phase mode: cull both groups to the chosen host phase BEFORE the
            // boolean-intersection pass (the cheap scoping/performance path). An unknown phase
            // name is passed through + logged — never a silent drop.
            if (string.Equals(_opts.PhaseMode, "Specific", StringComparison.OrdinalIgnoreCase))
            {
                if (!det.HostPhaseSeq.TryGetValue(_opts.SpecificPhaseName ?? "", out int targetSeq))
                {
                    Log($"Phase filter: host phase '{_opts.SpecificPhaseName}' not found in this document — "
                      + "phase filtering skipped for this run.", "fail");
                }
                else
                {
                    int b1 = group1Elements.Count, b2 = group2Elements.Count;
                    group1Elements = group1Elements.Where(e => PhasePresent(e, targetSeq, det)).ToList();
                    group2Elements = group2Elements.Where(e => PhasePresent(e, targetSeq, det)).ToList();
                    Log($"Phase '{_opts.SpecificPhaseName}': kept {group1Elements.Count}/{b1} Group 1 and "
                      + $"{group2Elements.Count}/{b2} Group 2 element(s).", "info");
                    if (group1Elements.Count == 0 || group2Elements.Count == 0)
                    {
                        Log($"No {(group1Elements.Count == 0 ? "Group 1" : "Group 2")} element exists in phase "
                          + $"'{_opts.SpecificPhaseName}' — nothing to clash.", "fail");
                        det.Failed = true; return det;
                    }
                }
            }

            // 3. Find clashes
            int maxClashes = _opts.MaxClashes > 0 ? _opts.MaxClashes : 500;
            det.ToleranceFt = _opts.ToleranceMm / 304.8;
            det.Clashes = FindClashes(group1Elements, group2Elements, maxClashes);
            bool hitLimit = det.Clashes.Count >= maxClashes;

            if (det.Clashes.Count == 0)
            {
                Log("No solid intersections detected between the two groups.", "info");
                return det;
            }

            Log(hitLimit
                ? $"Found {det.Clashes.Count} clash(es) — limit of {maxClashes} reached. Increase Max Clashes to detect more."
                : $"Found {det.Clashes.Count} clash(es).", "info");

            int unruled = det.Clashes.Count(c => !c.Group1.RuleColored);
            if (unruled > 0)
                Log($"{unruled} clash(es) matched no Auto Filter rule — shown in fallback colour {_opts.FallbackColorHex} with a solid fill.", "info");

            // Per-view gating context, resolved once. A clash is drawn in a view only when its overlap
            // region falls inside that view's volume — crop box in XY, a storey-height band in Z — so
            // other levels' clashes (common in identical stacked-level models) don't bleed onto this
            // view. Overlap bboxes are already in host world coordinates, so the gate is link-agnostic.
            det.LineStyleId    = ResolveLineStyleId(doc);
            det.LevelElevs     = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .Select(l => l.Elevation).OrderBy(z => z).ToList();
            det.StoreyMarginFt = Math.Max(0.0, _opts.StoreyMarginMm) / 304.8;
            return det;
        }

        /// <summary>
        /// Places the detected clashes' markers in ONE view, inside the caller's open transaction.
        /// Gated by the view's world box so other levels' / off-crop clashes don't bleed in. Returns
        /// this view's tally. The region-type cache is shared across views to reuse filled-region types.
        /// </summary>
        public PlacementResult PlaceInView(
            Document doc, View view, ClashDetection det, Dictionary<string, ElementId?> regionTypeCache)
        {
            var pr = new PlacementResult();
            if (det == null || det.Failed || det.Clashes.Count == 0) return pr;

            // Phase gate (Match view phase) — a sibling of the volume gate: resolve this view's
            // phase once, then a clash marks here only when BOTH its elements exist in it. A view
            // without a phase, or a phase missing from the host sequence, passes through + logs.
            bool phaseGated = false;
            int  phaseSeq   = -1;
            if (string.Equals(_opts.PhaseMode, "MatchView", StringComparison.OrdinalIgnoreCase))
            {
                string viewPhase = ReadViewPhaseName(view);
                if (viewPhase.Length == 0)
                    Log($"View '{view.Name}': no phase parameter — phase gate skipped for this view.", "info");
                else if (!det.HostPhaseSeq.TryGetValue(viewPhase, out phaseSeq))
                    Log($"View '{view.Name}': phase '{viewPhase}' not in the host phase sequence — phase gate skipped for this view.", "info");
                else
                    phaseGated = true;
            }

            bool gated = TryGetViewWorldBox(view, det.LevelElevs, det.StoreyMarginFt, out BoundingBoxXYZ box);
            int volumeSkipped = 0, phaseSkipped = 0;
            foreach (var clash in det.Clashes)
            {
                if (gated && !BBoxOverlapTol(clash.OverlapBBox, box, det.ToleranceFt))
                { pr.Skipped++; volumeSkipped++; continue; }
                if (phaseGated && (!PhasePresent(clash.Group1, phaseSeq, det)
                                || !PhasePresent(clash.Group2, phaseSeq, det)))
                { pr.Skipped++; phaseSkipped++; continue; }
                try
                {
                    if (CreateClashGraphics(doc, view, clash, det.LineStyleId, det.ToleranceFt, regionTypeCache))
                        pr.Markers++;
                }
                catch (Exception ex)
                {
                    Log($"Error in '{view.Name}': {ex.Message}", "fail");
                    pr.Fails++;
                }
            }

            if (volumeSkipped > 0)
                Log($"View '{view.Name}': {volumeSkipped} clash(es) outside the view volume — skipped.", "info");
            if (phaseSkipped > 0)
                Log($"View '{view.Name}': {phaseSkipped} clash(es) not in this view's phase — skipped.", "info");
            return pr;
        }

        // ── View-volume gate (keeps other levels' / off-crop clashes off this view) ───
        private const double DefaultStoreyFt = 14.0;  // top-most level (no level above) fallback height

        /// <summary>
        /// True, with the view's world-space box (unbounded axes stay at double.Max/MinValue), when the view can be
        /// scoped: plan views get crop-box XY (when cropped) and a storey-height Z band from their
        /// level to the next level up; 3D views use an active section box. Returns false (no gate)
        /// for un-scopable views — an uncropped section/elevation — so they are never over-filtered.
        /// </summary>
        private static bool TryGetViewWorldBox(View view, List<double> sortedLevelElevs, double storeyMarginFt, out BoundingBoxXYZ box)
        {
            // double.Max/MinValue stand in for "unbounded" on un-scoped axes (XYZ rejects non-finite).
            box = new BoundingBoxXYZ
            {
                Min = new XYZ(double.MinValue, double.MinValue, double.MinValue),
                Max = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue),
            };

            if (view is View3D v3 && v3.IsSectionBoxActive)
            {
                try
                {
                    var sb = v3.GetSectionBox();
                    box = WorldAabb(BoxCorners(sb.Min, sb.Max), sb.Transform);
                    return true;
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: get 3D section box", ex); return false; }
            }

            bool bounded = false;

            // XY from the crop box (corners → world) when the view is cropped.
            try
            {
                if (view.CropBoxActive && view.CropBox != null)
                {
                    var cb    = view.CropBox;
                    var world = WorldAabb(BoxCorners(cb.Min, cb.Max), cb.Transform);
                    box.Min = new XYZ(world.Min.X, world.Min.Y, box.Min.Z);
                    box.Max = new XYZ(world.Max.X, world.Max.Y, box.Max.Z);
                    bounded = true;
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read view crop box", ex); }

            // Z from the storey band [Li - margin, Lnext - margin) for plan views.
            if (view is ViewPlan plan && TryGetStoreyZBand(plan, sortedLevelElevs, storeyMarginFt, out double zMin, out double zMax))
            {
                box.Min = new XYZ(box.Min.X, box.Min.Y, zMin);
                box.Max = new XYZ(box.Max.X, box.Max.Y, zMax);
                bounded = true;
            }

            return bounded;
        }

        /// <summary>Storey-height world-Z band for a plan view: from its level (less a margin for
        /// sub-floor structure) up to the next level above, or a default height when it is the top
        /// level. False when the plan has no associated level.</summary>
        private static bool TryGetStoreyZBand(ViewPlan plan, List<double> sortedLevelElevs, double storeyMarginFt, out double zMin, out double zMax)
        {
            zMin = double.NegativeInfinity;
            zMax = double.PositiveInfinity;

            Level? genLevel;
            try { genLevel = plan.GenLevel; }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read plan GenLevel", ex); return false; }
            if (genLevel == null) return false;

            double baseElev = genLevel.Elevation;
            double? nextAbove = sortedLevelElevs
                .Where(e => e > baseElev + 1e-6)
                .Select(e => (double?)e)
                .FirstOrDefault();

            zMin = baseElev - storeyMarginFt;
            zMax = (nextAbove ?? baseElev + DefaultStoreyFt) - storeyMarginFt;
            return true;
        }

        /// <summary>AABB overlap test tolerant of an inflation on each axis; double.Max/MinValue
        /// bounds (unbounded box axes) always pass — the ± tol never overflows them.</summary>
        private static bool BBoxOverlapTol(BoundingBoxXYZ a, BoundingBoxXYZ b, double tol)
        {
            return a.Min.X <= b.Max.X + tol && a.Max.X >= b.Min.X - tol
                && a.Min.Y <= b.Max.Y + tol && a.Max.Y >= b.Min.Y - tol
                && a.Min.Z <= b.Max.Z + tol && a.Max.Z >= b.Min.Z - tol;
        }

        // ── Phase filtering ───────────────────────────────────────────────────
        /// <summary>True when a phase mode that needs per-element phase names is active —
        /// gates the (otherwise wasted) parameter reads during scanning.</summary>
        private bool PhaseFilteringActive =>
            string.Equals(_opts.PhaseMode, "MatchView", StringComparison.OrdinalIgnoreCase)
         || string.Equals(_opts.PhaseMode, "Specific",  StringComparison.OrdinalIgnoreCase);

        /// <summary>Phase Created / Phase Demolished NAMES of an element, resolved in its own
        /// document ("" = none). Names (not ids) so linked-model phases can be matched against
        /// the host timeline. Never throws.</summary>
        private static (string created, string demolished) ReadElementPhases(Element el)
        {
            string created = "", demolished = "";
            try
            {
                var pc = el.get_Parameter(BuiltInParameter.PHASE_CREATED);
                if (pc != null && pc.StorageType == StorageType.ElementId
                    && el.Document.GetElement(pc.AsElementId()) is Phase phC)
                    created = phC.Name ?? "";

                var pd = el.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                if (pd != null && pd.StorageType == StorageType.ElementId
                    && el.Document.GetElement(pd.AsElementId()) is Phase phD)
                    demolished = phD.Name ?? "";
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read element phases", ex); }
            return (created, demolished);
        }

        /// <summary>Phase name of a view ("" when the view has none). Never throws.</summary>
        private static string ReadViewPhaseName(View view)
        {
            try
            {
                var p = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (p != null && p.StorageType == StorageType.ElementId
                    && view.Document.GetElement(p.AsElementId()) is Phase ph)
                    return ph.Name ?? "";
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read view phase", ex); }
            return "";
        }

        /// <summary>
        /// "Exists in the target host phase": created at or before it, and not demolished at or
        /// before it. A blank phase (element has none) or a name absent from the host sequence
        /// passes through — logged once per name, never silently dropped.
        /// </summary>
        private bool PhasePresent(ClashElement e, int targetSeq, ClashDetection det)
        {
            if (e.CreatedPhaseName.Length > 0)
            {
                if (det.HostPhaseSeq.TryGetValue(e.CreatedPhaseName, out int createdSeq))
                {
                    if (createdSeq > targetSeq) return false;      // not yet created in this phase
                }
                else WarnUnmappedPhase(det, e.CreatedPhaseName);
            }
            if (e.DemolishedPhaseName.Length > 0)
            {
                if (det.HostPhaseSeq.TryGetValue(e.DemolishedPhaseName, out int demoSeq))
                {
                    if (demoSeq <= targetSeq) return false;        // already demolished by this phase
                }
                else WarnUnmappedPhase(det, e.DemolishedPhaseName);
            }
            return true;
        }

        private void WarnUnmappedPhase(ClashDetection det, string phaseName)
        {
            if (!det.WarnedPhaseNames.Add(phaseName)) return;
            Log($"Phase filter: phase '{phaseName}' (from a linked model) has no same-named host phase — "
              + "its elements pass through unfiltered.", "info");
            LemoineLog.Warn("ClashEngine phase filter", $"unmapped phase name '{phaseName}'");
        }

        // ── Group scanning (mode-aware) ───────────────────────────────────────
        private List<ClashElement> ScanGroupSpec(
            ClashGroupSpec spec,
            List<(Document doc, RevitLinkInstance? link, Transform tx)> allSources,
            string label)
        {
            var worksetExcl = BuildWorksetExclusions(spec.WorksetFilters);
            switch (spec.Mode)
            {
                case "Categories":
                    return ScanCategories(spec.Categories, FilterSources(allSources, spec.SourceLinkIds), worksetExcl);
                case "Elements":
                    return ScanElements(spec.ElemIds, spec.ElemLinkIds, allSources);
                default:
                    var rules = ResolveRules(spec.RuleKeys);
                    if (rules.Count == 0)
                        Log($"{label}: no filter rules resolved — check Auto Filters or switch mode.", "info");
                    return ScanRules(rules, FilterSources(allSources, spec.SourceLinkIds), worksetExcl);
            }
        }

        private static List<(Document doc, RevitLinkInstance? link, Transform tx)> FilterSources(
            List<(Document doc, RevitLinkInstance? link, Transform tx)> all, List<long> sourceLinkIds)
        {
            if (sourceLinkIds == null || sourceLinkIds.Count == 0) return all;
            var set = new HashSet<long>(sourceLinkIds);
            return all.Where(s => set.Contains(s.link?.Id.Value ?? 0L)).ToList();
        }

        // ── Workset filtering (per source document) ───────────────────────────
        /// <summary>linkId (0 = host) → set of excluded (unchecked) workset ids. Empty/absent = include all.</summary>
        private static Dictionary<long, HashSet<int>> BuildWorksetExclusions(List<ClashWorksetFilter> filters)
        {
            var map = new Dictionary<long, HashSet<int>>();
            foreach (var f in filters ?? new List<ClashWorksetFilter>())
            {
                if (f?.ExcludedWorksetIds == null || f.ExcludedWorksetIds.Count == 0) continue;
                map[f.LinkInstId] = new HashSet<int>(f.ExcludedWorksetIds);
            }
            return map;
        }

        /// <summary>Excluded-workset set for one source (by its link id), or null when nothing is excluded there.</summary>
        private static HashSet<int>? WorksetExclForSource(
            Dictionary<long, HashSet<int>> worksetExcl, RevitLinkInstance? link)
        {
            long linkId = link?.Id.Value ?? 0L;
            return worksetExcl.TryGetValue(linkId, out var set) && set.Count > 0 ? set : null;
        }

        /// <summary>True when the element should be kept: no exclusions, or its workset is not excluded.</summary>
        private static bool PassesWorkset(Element el, HashSet<int>? excluded)
        {
            if (excluded == null || excluded.Count == 0) return true;
            try { return !excluded.Contains(el.WorksetId.IntegerValue); }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read element workset", ex); return true; }
        }

        private List<ClashElement> ScanRules(
            List<(FilterTradeConfig trade, FilterRuleConfig rule)> rules,
            List<(Document doc, RevitLinkInstance? link, Transform tx)> sources,
            Dictionary<long, HashSet<int>> worksetExcl)
        {
            var result = new List<ClashElement>();

            foreach (var (trade, rule) in rules)
            {
                if (!rule.Enabled) continue;

                var catIds = new List<BuiltInCategory>();
                foreach (var bicStr in rule.BuiltInCategories ?? new List<string>())
                    if (Enum.TryParse<BuiltInCategory>(bicStr, false, out var bic))
                        catIds.Add(bic);

                if (catIds.Count == 0)
                {
                    Log($"Rule '{rule.Name}' has no categories configured — skipped.", "info");
                    continue;
                }

                int ruleTotal = 0;
                foreach (var (srcDoc, link, tx) in sources)
                {
                    var wsExcluded = WorksetExclForSource(worksetExcl, link);
                    int srcCount = 0;
                    foreach (var bic in catIds)
                    {
                        IEnumerable<Element> elems;
                        try
                        {
                            elems = new FilteredElementCollector(srcDoc)
                                .OfCategory(bic).WhereElementIsNotElementType().ToElements();
                        }
                        catch { continue; }

                        foreach (var el in elems)
                        {
                            if (!MatchesRule(el, rule)) continue;
                            if (!PassesWorkset(el, wsExcluded)) continue;
                            var bb = GetHostBBox(el, tx);
                            if (bb == null) continue;
                            var sh = ComputeElementShape(el, tx);
                            var (phC, phD) = PhaseFilteringActive ? ReadElementPhases(el) : ("", "");

                            result.Add(new ClashElement
                            {
                                Doc           = srcDoc,
                                LinkInstance  = link,
                                HostTransform = tx,
                                Id            = el.Id,
                                Label         = rule.Name,
                                ColorHex      = rule.SurfColor ?? "#888888",
                                HostBBox      = bb,
                                IsRectangular = sh.IsRect,
                                WidthFt       = sh.W,
                                HeightFt      = sh.H,
                                WidthDir      = sh.WDir,
                                HeightDir     = sh.HDir,
                                CreatedPhaseName    = phC,
                                DemolishedPhaseName = phD,
                            });
                            srcCount++;
                        }
                    }
                    if (srcCount > 0 || link != null)
                        Log($"  [{srcDoc.Title}] '{rule.Name}': {srcCount} element(s)", "info");
                    ruleTotal += srcCount;
                }
                if (ruleTotal == 0)
                    Log($"  Rule '{rule.Name}': 0 matching elements — check categories/match criteria/source docs.", "info");
            }

            return result;
        }

        private List<ClashElement> ScanCategories(
            List<string> osts,
            List<(Document doc, RevitLinkInstance? link, Transform tx)> sources,
            Dictionary<long, HashSet<int>> worksetExcl)
        {
            var result = new List<ClashElement>();
            foreach (var ostStr in osts ?? new List<string>())
            {
                if (!Enum.TryParse<BuiltInCategory>(ostStr, false, out var bic)) continue;

                int catTotal = 0;
                foreach (var (srcDoc, link, tx) in sources)
                {
                    var wsExcluded = WorksetExclForSource(worksetExcl, link);
                    int srcCount = 0;
                    IEnumerable<Element> elems;
                    try
                    {
                        elems = new FilteredElementCollector(srcDoc)
                            .OfCategory(bic).WhereElementIsNotElementType().ToElements();
                    }
                    catch { continue; }

                    foreach (var el in elems)
                    {
                        if (!PassesWorkset(el, wsExcluded)) continue;
                        var bb = GetHostBBox(el, tx);
                        if (bb == null) continue;
                        string? ruleColor = ResolveRuleColor(el);
                        var sh = ComputeElementShape(el, tx);
                        var (phC, phD) = PhaseFilteringActive ? ReadElementPhases(el) : ("", "");
                        result.Add(new ClashElement
                        {
                            Doc           = srcDoc,
                            LinkInstance  = link,
                            HostTransform = tx,
                            Id            = el.Id,
                            Label         = ostStr,
                            ColorHex      = ruleColor ?? _opts.FallbackColorHex,
                            RuleColored   = ruleColor != null,
                            HostBBox      = bb,
                            IsRectangular = sh.IsRect,
                            WidthFt       = sh.W,
                            HeightFt      = sh.H,
                            WidthDir      = sh.WDir,
                            HeightDir     = sh.HDir,
                            CreatedPhaseName    = phC,
                            DemolishedPhaseName = phD,
                        });
                        srcCount++;
                    }
                    catTotal += srcCount;
                }
                Log($"  Category {ostStr}: {catTotal} element(s)", "info");
            }
            return result;
        }

        private List<ClashElement> ScanElements(
            List<long> elemIds, List<long> elemLinkIds,
            List<(Document doc, RevitLinkInstance? link, Transform tx)> allSources)
        {
            var result = new List<ClashElement>();
            var byLink = new Dictionary<long, (Document doc, RevitLinkInstance? link, Transform tx)>();
            foreach (var s in allSources) byLink[s.link?.Id.Value ?? 0L] = s;

            for (int i = 0; i < elemIds.Count; i++)
            {
                long lnk = (i < elemLinkIds.Count) ? elemLinkIds[i] : 0L;
                if (!byLink.TryGetValue(lnk, out var src)) continue;

                var el = src.doc.GetElement(new ElementId(elemIds[i]));
                if (el == null) continue;
                var bb = GetHostBBox(el, src.tx);
                if (bb == null) continue;

                string? ruleColor = ResolveRuleColor(el);
                var sh = ComputeElementShape(el, src.tx);
                var (phC, phD) = PhaseFilteringActive ? ReadElementPhases(el) : ("", "");
                result.Add(new ClashElement
                {
                    Doc           = src.doc,
                    LinkInstance  = src.link,
                    HostTransform = src.tx,
                    Id            = el.Id,
                    Label         = el.Name ?? "(element)",
                    ColorHex      = ruleColor ?? _opts.FallbackColorHex,
                    RuleColored   = ruleColor != null,
                    HostBBox      = bb,
                    IsRectangular = sh.IsRect,
                    WidthFt       = sh.W,
                    HeightFt      = sh.H,
                    WidthDir      = sh.WDir,
                    HeightDir     = sh.HDir,
                    CreatedPhaseName    = phC,
                    DemolishedPhaseName = phD,
                });
            }
            Log($"  Picked elements resolved: {result.Count}", "info");
            return result;
        }

        // ── Rule resolution & matching ────────────────────────────────────────
        private static List<(FilterTradeConfig trade, FilterRuleConfig rule)> ResolveRules(List<string> persistKeys)
        {
            var result = new List<(FilterTradeConfig, FilterRuleConfig)>();
            var keySet = new HashSet<string>(persistKeys ?? new List<string>());
            foreach (var trade in AutoFiltersSettings.Instance.Trades)
                foreach (var rule in trade.Rules)
                    if (keySet.Contains($"{trade.Id}::{rule.Id}"))
                        result.Add((trade, rule));
            return result;
        }

        private static string? ResolveRuleColor(Element el)
        {
            string? bic = ElementBicName(el);
            if (bic == null) return null;

            foreach (var trade in AutoFiltersSettings.Instance.Trades)
            {
                if (trade?.Rules == null) continue;
                foreach (var rule in trade.Rules)
                {
                    if (rule == null || !rule.Enabled) continue;
                    if (rule.BuiltInCategories == null || !rule.BuiltInCategories.Contains(bic)) continue;
                    if (!MatchesRule(el, rule)) continue;
                    if (string.IsNullOrEmpty(rule.SurfColor)) continue;
                    return rule.SurfColor;
                }
            }
            return null;
        }

        private static string? ElementBicName(Element el)
        {
            var cat = el.Category;
            if (cat == null) return null;
            try { return ((BuiltInCategory)cat.Id.Value).ToString(); }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: resolve element category", ex); return null; }
        }

        private static bool MatchesRule(Element el, FilterRuleConfig rule)
        {
            string matchType = (rule.MatchType ?? "contains").ToLowerInvariant();
            if (matchType == "all") return true;
            if (rule.Match == null || rule.Match.Count == 0) return false;

            string? pv = ReadParamValue(el, rule.Parameter);
            if (pv == null) return false;
            string pvLow = pv.ToLowerInvariant();

            foreach (var kw in rule.Match)
            {
                string kwLow = kw.ToLowerInvariant();
                if (matchType == "equals" ? pvLow == kwLow : pvLow.Contains(kwLow))
                    return true;
            }
            return false;
        }

        private static string? ReadParamValue(Element el, string paramName)
        {
            if (paramName == "Type Name")
            {
                try
                {
                    var t = el.Document.GetElement(el.GetTypeId());
                    if (t != null && !string.IsNullOrEmpty(t.Name)) return t.Name;
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read Type Name parameter", ex); }
            }

            try
            {
                var p = el.LookupParameter(paramName);
                if (p != null)
                {
                    string? v = p.AsValueString();
                    if (!string.IsNullOrEmpty(v)) return v;
                    v = p.AsString();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read parameter value", ex); }

            if (BipMap.TryGetValue(paramName, out var bip))
            {
                try
                {
                    var p = el.get_Parameter(bip);
                    if (p != null)
                    {
                        string? v = p.AsValueString();
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read built-in parameter value", ex); }
            }
            return null;
        }

        // ── Element cross-section (drives the marker shape + size, inherited from Group 1) ───
        /// <summary>
        /// Cross-section of an element in feet: a round pipe / round-duct outer diameter (exact and
        /// orientation-independent) gives a circle; a rectangular duct's width × height gives a
        /// rectangle. Falls back to the smallest dimension of the element's own box (≈ a straight
        /// run's diameter, drawn round). Width/Height are 0 when nothing usable is found, so the
        /// caller can fall back to fitting the clash itself.
        /// </summary>
        private static (bool IsRect, double W, double H, XYZ? WDir, XYZ? HDir) ComputeElementShape(Element el, Transform tx)
        {
            // Rectangular first: a duct / cable tray that reports BOTH a width and a height is
            // rectangular — even when it also exposes an (equivalent) diameter parameter, which would
            // otherwise mis-classify it as round.
            double w = ReadDoubleParam(el, BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            double h = ReadDoubleParam(el, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
            if (w <= 0) w = ReadNamedDoubleParam(el, "Width");
            if (h <= 0) h = ReadNamedDoubleParam(el, "Height");
            if (w > 1e-6 && h > 1e-6)
            {
                ComputeSectionAxes(el, tx, out XYZ? wDir, out XYZ? hDir);
                return (true, w, h, wDir, hDir);
            }

            // Round pipe / round duct outer diameter (orientation-independent, exact).
            double d = ReadDoubleParam(el, BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
            if (d <= 0) d = ReadDoubleParam(el, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (d <= 0) d = ReadDoubleParam(el, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
            if (d <= 0) d = ReadNamedDoubleParam(el, "Diameter");
            if (d > 1e-6) return (false, d, d, null, null);

            if (w > 1e-6 || h > 1e-6) { double s = Math.Max(w, h); return (false, s, s, null, null); }

            try
            {
                var bb = el.get_BoundingBox(null);
                if (bb != null)
                {
                    double dx = Math.Abs(bb.Max.X - bb.Min.X);
                    double dy = Math.Abs(bb.Max.Y - bb.Min.Y);
                    double dz = Math.Abs(bb.Max.Z - bb.Min.Z);
                    double min = Math.Min(dx, Math.Min(dy, dz));
                    if (min > 1e-6) return (false, min, min, null, null);
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: element shape bbox", ex); }
            return (false, 0.0, 0.0, null, null);
        }

        // World width / height axes of a rectangular run: width is horizontal across the run, height is
        // the vertical-most direction perpendicular to it. Null when the element has no run direction
        // (e.g. a fitting), so the marker falls back to the view's own right / up axes.
        private static void ComputeSectionAxes(Element el, Transform tx, out XYZ? widthDir, out XYZ? heightDir)
        {
            widthDir = null; heightDir = null;

            XYZ? run = null;
            try
            {
                if (el.Location is LocationCurve lc && lc.Curve != null)
                {
                    var dir = lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0);
                    if (dir.GetLength() > 1e-9) run = dir.Normalize();
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read element location curve", ex); }
            if (run == null) return;

            if (tx != null && !tx.IsIdentity) run = tx.OfVector(run);
            if (run.GetLength() < 1e-9) return;
            run = run.Normalize();

            XYZ refV = XYZ.BasisZ;
            XYZ hd = refV.Subtract(run.Multiply(refV.DotProduct(run)));     // vertical-most ⟂ to run
            if (hd.GetLength() < 1e-6)                                       // vertical run (riser)
            {
                refV = XYZ.BasisX;
                hd   = refV.Subtract(run.Multiply(refV.DotProduct(run)));
            }
            if (hd.GetLength() < 1e-6) return;
            hd = hd.Normalize();

            XYZ wd = run.CrossProduct(hd);                                  // horizontal across the run
            if (wd.GetLength() < 1e-6) return;
            widthDir  = wd.Normalize();
            heightDir = hd;
        }

        // Builds the marker boundary for a clash. Circles use the view's own axes; rectangles use the
        // element's width / height axes projected into the view plane (so the marker follows the duct as
        // it appears in this view), with an edge-on axis reconstructed from the view normal so the
        // rectangle never collapses.
        private static CurveLoop BuildMarkerLoop(
            XYZ center, XYZ viewRight, XYZ viewUp, XYZ viewNormal,
            bool rect, double radius, double halfW, double halfH,
            XYZ? widthDir, XYZ? heightDir)
        {
            if (!rect) return CircleLoop(center, radius, viewRight, viewUp);

            XYZ axW = viewRight, axH = viewUp;
            if (widthDir != null && heightDir != null)
            {
                XYZ pw = InPlane(widthDir, viewNormal);
                XYZ ph = InPlane(heightDir, viewNormal);
                double lw = pw.GetLength(), lh = ph.GetLength();
                if      (lw > 1e-6 && lh > 1e-6) { axW = pw.Normalize();                       axH = ph.Normalize(); }
                else if (lw > 1e-6)              { axW = pw.Normalize();                       axH = viewNormal.CrossProduct(axW).Normalize(); }
                else if (lh > 1e-6)              { axH = ph.Normalize();                       axW = axH.CrossProduct(viewNormal).Normalize(); }
            }
            return RectLoop(center, axW, axH, halfW, halfH);
        }

        private static XYZ InPlane(XYZ v, XYZ normal) => v.Subtract(normal.Multiply(v.DotProduct(normal)));

        // ── Marker loop builders (shared by plan + vertical paths) ────────────
        private static CurveLoop CircleLoop(XYZ center, double radius, XYZ xAxis, XYZ yAxis)
        {
            var loop = new CurveLoop();
            loop.Append(Arc.Create(center, radius, 0,       Math.PI,     xAxis, yAxis));
            loop.Append(Arc.Create(center, radius, Math.PI, 2 * Math.PI, xAxis, yAxis));
            return loop;
        }

        private static CurveLoop RectLoop(XYZ center, XYZ right, XYZ up, double halfW, double halfH)
        {
            XYZ rW = right.Multiply(halfW);
            XYZ uH = up.Multiply(halfH);
            XYZ bl = center.Subtract(rW).Subtract(uH);
            XYZ br = center.Add(rW).Subtract(uH);
            XYZ tr = center.Add(rW).Add(uH);
            XYZ tl = center.Subtract(rW).Add(uH);
            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(bl, br));
            loop.Append(Line.CreateBound(br, tr));
            loop.Append(Line.CreateBound(tr, tl));
            loop.Append(Line.CreateBound(tl, bl));
            return loop;
        }

        private static double ReadDoubleParam(Element el, BuiltInParameter bip)
        {
            try
            {
                var p = el.get_Parameter(bip);
                if (p != null && p.StorageType == StorageType.Double && p.HasValue) return p.AsDouble();
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read double parameter", ex); }
            return 0.0;
        }

        // Fallback for content whose width/height/diameter is not on the expected built-in parameter
        // (e.g. cable trays, some duct families). Name-based, so it only resolves in matching locales.
        private static double ReadNamedDoubleParam(Element el, string name)
        {
            try
            {
                var p = el.LookupParameter(name);
                if (p != null && p.StorageType == StorageType.Double && p.HasValue) return p.AsDouble();
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read named double parameter", ex); }
            return 0.0;
        }

        // ── BBox helpers ──────────────────────────────────────────────────────
        private static BoundingBoxXYZ? GetHostBBox(Element el, Transform hostTransform)
        {
            BoundingBoxXYZ? bb;
            try { bb = el.get_BoundingBox(null); }
            catch { return null; }
            if (bb == null) return null;

            var pts = BoxCorners(bb.Min, bb.Max);
            return WorldAabb(pts, hostTransform);
        }

        private static XYZ[] BoxCorners(XYZ min, XYZ max) => new[]
        {
            new XYZ(min.X, min.Y, min.Z), new XYZ(max.X, min.Y, min.Z),
            new XYZ(min.X, max.Y, min.Z), new XYZ(max.X, max.Y, min.Z),
            new XYZ(min.X, min.Y, max.Z), new XYZ(max.X, min.Y, max.Z),
            new XYZ(min.X, max.Y, max.Z), new XYZ(max.X, max.Y, max.Z),
        };

        private static BoundingBoxXYZ WorldAabb(XYZ[] pts, Transform tx)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var p in pts)
            {
                XYZ t = (tx == null || tx.IsIdentity) ? p : tx.OfPoint(p);
                if (t.X < minX) minX = t.X; if (t.X > maxX) maxX = t.X;
                if (t.Y < minY) minY = t.Y; if (t.Y > maxY) maxY = t.Y;
                if (t.Z < minZ) minZ = t.Z; if (t.Z > maxZ) maxZ = t.Z;
            }
            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        // ── Clash detection ───────────────────────────────────────────────────
        private List<ClashResult> FindClashes(
            List<ClashElement> group1, List<ClashElement> group2, int maxClashes)
        {
            const double eps = 1e-6;
            var results = new List<ClashResult>();
            int booleanFails = 0;

            // The pair test is O(group1 × group2) — the most expensive loop in the run.
            // Report progress over the outer group at 5% intervals so it isn't silent.
            var progress = new RunProgressReporter(Log, group1.Count, "source elements");

            foreach (var g1 in group1)
            {
                var b1 = g1.HostBBox;
                foreach (var g2 in group2)
                {
                    var b2 = g2.HostBBox;
                    if (!BBoxOverlap(b1, b2)) continue;

                    var s1 = EnsureSolid(g1);
                    var s2 = EnsureSolid(g2);

                    BoundingBoxXYZ? overlap = null;
                    if (s1 != null && s2 != null)
                    {
                        Solid? inter = null;
                        try
                        {
                            inter = BooleanOperationsUtils.ExecuteBooleanOperation(
                                s1, s2, BooleanOperationsType.Intersect);
                        }
                        catch { inter = null; booleanFails++; }

                        if (inter != null && inter.Volume > eps)
                            overlap = SolidWorldBBox(inter);
                        else if (inter != null)
                            continue;
                        else
                            overlap = BBoxOverlapRegion(b1, b2);
                    }
                    else
                    {
                        overlap = BBoxOverlapRegion(b1, b2);
                    }

                    if (overlap == null) continue;
                    results.Add(new ClashResult { Group1 = g1, Group2 = g2, OverlapBBox = overlap });
                    if (results.Count >= maxClashes)
                    {
                        if (booleanFails > 0) Log($"  ({booleanFails} boolean op fallback(s) to bbox)", "info");
                        return results;
                    }
                }

                progress.Tick();
            }
            if (booleanFails > 0) Log($"  ({booleanFails} boolean op fallback(s) to bbox)", "info");
            return results;
        }

        private static bool BBoxOverlap(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            return a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
                && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y
                && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
        }

        private static BoundingBoxXYZ BBoxOverlapRegion(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            return new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Max(a.Min.X, b.Min.X), Math.Max(a.Min.Y, b.Min.Y), Math.Max(a.Min.Z, b.Min.Z)),
                Max = new XYZ(Math.Min(a.Max.X, b.Max.X), Math.Min(a.Max.Y, b.Max.Y), Math.Min(a.Max.Z, b.Max.Z)),
            };
        }

        private Solid? EnsureSolid(ClashElement e)
        {
            if (e.SolidTried) return e.HostSolid;
            e.SolidTried = true;
            var el = e.Doc.GetElement(e.Id);
            if (el != null) e.HostSolid = GetUnionSolidHost(el, e.HostTransform);
            return e.HostSolid;
        }

        private static Solid? GetUnionSolidHost(Element el, Transform tx)
        {
            GeometryElement? ge;
            try
            {
                ge = el.get_Geometry(new Options
                {
                    ComputeReferences        = false,
                    IncludeNonVisibleObjects = false,
                    DetailLevel              = ViewDetailLevel.Medium,
                });
            }
            catch { return null; }
            if (ge == null) return null;
            return AccumulateSolids(ge, tx, null);
        }

        private static Solid? AccumulateSolids(GeometryElement ge, Transform? tx, Solid? acc)
        {
            foreach (GeometryObject obj in ge)
            {
                if (obj is Solid s && s.Volume > 1e-6)
                {
                    Solid hs;
                    try { hs = (tx == null || tx.IsIdentity) ? s : SolidUtils.CreateTransformed(s, tx); }
                    catch { continue; }
                    acc = Combine(acc, hs);
                }
                else if (obj is GeometryInstance gi)
                {
                    GeometryElement? ige = null;
                    try { ige = gi.GetInstanceGeometry(); }
                    catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: extract instance geometry", ex); }
                    if (ige != null) acc = AccumulateSolids(ige, tx, acc);
                }
            }
            return acc;
        }

        private static Solid? Combine(Solid? acc, Solid s)
        {
            if (acc == null) return s;
            try { return BooleanOperationsUtils.ExecuteBooleanOperation(acc, s, BooleanOperationsType.Union); }
            catch { return acc; }
        }

        private static BoundingBoxXYZ SolidWorldBBox(Solid solid)
        {
            var bb = solid.GetBoundingBox();
            var pts = BoxCorners(bb.Min, bb.Max);
            return WorldAabb(pts, bb.Transform);
        }

        // ── Marker creation (filled region + tagged cross lines) ──────────────
        private bool CreateClashGraphics(
            Document doc, View view, ClashResult clash,
            ElementId lineStyleId, double toleranceFt, Dictionary<string, ElementId?> regionTypeCache)
        {
            // Section / elevation views draw the round in the view's vertical cut plane.
            if (_opts.ElevationMode)
                return CreateClashGraphicsVertical(doc, view, clash, lineStyleId, toleranceFt, regionTypeCache);

            var zone = clash.OverlapBBox;
            double minX = zone.Min.X - toleranceFt;
            double maxX = zone.Max.X + toleranceFt;
            double minY = zone.Min.Y - toleranceFt;
            double maxY = zone.Max.Y + toleranceFt;

            if (maxX - minX < 0.001 || maxY - minY < 0.001) return false;

            double cx     = (minX + maxX) / 2.0;
            double cy     = (minY + maxY) / 2.0;
            double halfW  = (maxX - minX) / 2.0;
            double halfH  = (maxY - minY) / 2.0;

            // Marker shape + size inherited from the Group 1 element, enlarged by the oversize margin.
            // Round elements → circle (Ø + oversize); rectangular ducts → rectangle (W/H + oversize);
            // unknown size → auto-fit a circle to the clash footprint.
            double overFt = Math.Max(0.0, _opts.RoundSizeMm) / 304.8;
            bool   rect   = clash.Group1.IsRectangular;
            double elemW  = clash.Group1.WidthFt;
            double elemH  = clash.Group1.HeightFt;
            if (elemW <= 1e-6 || elemH <= 1e-6)
            {
                rect  = false;
                double fit = Math.Max(0.25, Math.Max(halfW, halfH)); // auto-fit radius
                elemW = elemH = fit * 2.0;
            }
            double mHalfW = (elemW + overFt) / 2.0;
            double mHalfH = (elemH + overFt) / 2.0;
            double radius = mHalfW;                                  // circle (mHalfW == mHalfH when round)
            // Cross lines end AT the marker's edge — never past it (fixed rule, no option).
            // Circle: the radius in any direction. Rectangle: the distance from centre to the
            // (possibly rotated) rectangle's edge along the view axis — slab test against the
            // element's own width/height axes, so a rotated duct's arms still stop at its edge.
            double EdgeDist(XYZ u)
            {
                if (!rect) return radius;
                XYZ wd = clash.Group1.WidthDir  ?? XYZ.BasisX;
                XYZ hd = clash.Group1.HeightDir ?? XYZ.BasisY;
                double cw = Math.Abs(u.DotProduct(wd));
                double ch = Math.Abs(u.DotProduct(hd));
                double t = double.MaxValue;
                if (cw > 1e-9) t = Math.Min(t, mHalfW / cw);
                if (ch > 1e-9) t = Math.Min(t, mHalfH / ch);
                return t == double.MaxValue ? Math.Max(mHalfW, mHalfH) : t;
            }
            double armX = EdgeDist(XYZ.BasisX);
            double armY = EdgeDist(XYZ.BasisY);

            // One id shared by this clash's region + every cross line, so the dimension pass can
            // re-group the 2–4 lines back into a single clash and dimension it once (not per line).
            string clashGroup = Guid.NewGuid().ToString("N");

            // The exact Group 2 element this clash hit, stamped on every marker so the dimension pass
            // can dimension straight to that element's edge (linked slabs, walls — anything in Group 2).
            ElementId tgtLink = clash.Group2.LinkInstance?.Id ?? ElementId.InvalidElementId;
            ElementId tgtElem = clash.Group2.Id;

            // ── FilledRegion (circular) ───────────────────────────────────────
            bool fallback   = !clash.Group1.RuleColored;
            string hexColor = (clash.Group1.ColorHex ?? "#888888").TrimStart('#').ToUpperInvariant();
            string cacheKey = fallback ? $"{hexColor}_FB" : $"{hexColor}_{_opts.FillStyle}";
            if (!regionTypeCache.TryGetValue(cacheKey, out ElementId? typeId))
            {
                typeId = GetOrCreateFilledRegionType(doc, hexColor, fallback);
                regionTypeCache[cacheKey] = typeId;
                if (typeId == null || typeId == ElementId.InvalidElementId)
                    Log($"Clash marker: no filled region type for #{hexColor} — circles skipped.", "info");
            }

            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                try
                {
                    var ctr  = new XYZ(cx, cy, 0);
                    CurveLoop loop = BuildMarkerLoop(
                        ctr, view.RightDirection.Normalize(), view.UpDirection.Normalize(), view.ViewDirection.Normalize(),
                        rect, radius, mHalfW, mHalfH, clash.Group1.WidthDir, clash.Group1.HeightDir);
                    var fr = FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop });
                    ClashTagSchema.StampTag(fr, clashGroup, tgtLink, tgtElem);
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: create clash filled region", ex); }
            }

            // ── Cross lines (tagged so the discovery pass can re-find them) ────
            if (_opts.DimTarget == "Centre")
            {
                var hLeft  = CreateLine(doc, view, lineStyleId, new XYZ(cx - armX, cy, 0), new XYZ(cx,       cy, 0), clashGroup, tgtLink, tgtElem);
                var hRight = CreateLine(doc, view, lineStyleId, new XYZ(cx,       cy, 0), new XYZ(cx + armX, cy, 0), clashGroup, tgtLink, tgtElem);
                var vBot   = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy - armY, 0), new XYZ(cx, cy,       0), clashGroup, tgtLink, tgtElem);
                var vTop   = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy,       0), new XYZ(cx, cy + armY, 0), clashGroup, tgtLink, tgtElem);
                return hLeft != null && hRight != null && vBot != null && vTop != null;
            }
            else
            {
                var hLine = CreateLine(doc, view, lineStyleId, new XYZ(cx - armX, cy, 0), new XYZ(cx + armX, cy, 0), clashGroup, tgtLink, tgtElem);
                var vLine = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy - armY, 0), new XYZ(cx, cy + armY, 0), clashGroup, tgtLink, tgtElem);
                return hLine != null && vLine != null;
            }
        }

        // ── Marker creation for vertical views (section / elevation) ──────────
        // The round fill lives in the view's cut plane (built from RightDirection / UpDirection),
        // with ONE tagged vertical diameter line spanning the round top→bottom. The elevation-tag
        // pass re-finds that line and anchors a spot elevation at its top / centre / bottom point.
        private bool CreateClashGraphicsVertical(
            Document doc, View view, ClashResult clash,
            ElementId lineStyleId, double toleranceFt, Dictionary<string, ElementId?> regionTypeCache)
        {
            var zone = clash.OverlapBBox;
            var c = new XYZ((zone.Min.X + zone.Max.X) / 2.0,
                            (zone.Min.Y + zone.Max.Y) / 2.0,
                            (zone.Min.Z + zone.Max.Z) / 2.0);

            XYZ right  = view.RightDirection.Normalize();
            XYZ up     = view.UpDirection.Normalize();
            XYZ normal = view.ViewDirection.Normalize();
            XYZ origin = view.Origin;

            // Project the clash centre onto the view's cut plane (drop the view-direction component).
            double depth = (c - origin).DotProduct(normal);
            XYZ cp = c - normal.Multiply(depth);

            // In-plane half extents of the clash (project the 8 bbox corners onto right / up).
            double halfU = 0, halfV = 0;
            foreach (var corner in BoxCorners(zone.Min, zone.Max))
            {
                var d = corner - cp;
                halfU = Math.Max(halfU, Math.Abs(d.DotProduct(right)));
                halfV = Math.Max(halfV, Math.Abs(d.DotProduct(up)));
            }
            halfU += toleranceFt;
            halfV += toleranceFt;

            // Marker shape + size inherited from the Group 1 element, enlarged by the oversize margin.
            // Width runs along the view's right axis, height along its up axis (matches a duct seen in
            // cross-section). Unknown size → auto-fit a circle to the clash footprint.
            double overFt = Math.Max(0.0, _opts.RoundSizeMm) / 304.8;
            bool   rect   = clash.Group1.IsRectangular;
            double elemW  = clash.Group1.WidthFt;
            double elemH  = clash.Group1.HeightFt;
            if (elemW <= 1e-6 || elemH <= 1e-6)
            {
                rect  = false;
                double fit = Math.Max(0.25, Math.Max(halfU, halfV));
                elemW = elemH = fit * 2.0;
            }
            double mHalfW = (elemW + overFt) / 2.0;
            double mHalfH = (elemH + overFt) / 2.0;
            double radius = mHalfW;                                  // circle (mHalfW == mHalfH when round)

            // One id shared by this clash's region + diameter line, so the spot-elevation pass can
            // carry the group through to its placed tag.
            string clashGroup = Guid.NewGuid().ToString("N");

            // The exact Group 2 element this clash hit — stamped so the dimension pass can target it.
            ElementId tgtLink = clash.Group2.LinkInstance?.Id ?? ElementId.InvalidElementId;
            ElementId tgtElem = clash.Group2.Id;

            // ── FilledRegion (circular, in the view plane) ────────────────────
            bool fallback   = !clash.Group1.RuleColored;
            string hexColor = (clash.Group1.ColorHex ?? "#888888").TrimStart('#').ToUpperInvariant();
            string cacheKey = fallback ? $"{hexColor}_FB" : $"{hexColor}_{_opts.FillStyle}";
            if (!regionTypeCache.TryGetValue(cacheKey, out ElementId? typeId))
            {
                typeId = GetOrCreateFilledRegionType(doc, hexColor, fallback);
                regionTypeCache[cacheKey] = typeId;
                if (typeId == null || typeId == ElementId.InvalidElementId)
                    Log($"Clash marker: no filled region type for #{hexColor} — circles skipped.", "info");
            }

            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                try
                {
                    CurveLoop loop = BuildMarkerLoop(
                        cp, right, up, normal,
                        rect, radius, mHalfW, mHalfH, clash.Group1.WidthDir, clash.Group1.HeightDir);
                    var fr = FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop });
                    ClashTagSchema.StampTag(fr, clashGroup, tgtLink, tgtElem);
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: create clash filled region (vertical)", ex); }
            }

            // ── Vertical line spanning the marker top→bottom, tagged for the spot-elevation pass ──
            var bottom = cp.Subtract(up.Multiply(mHalfH));
            var top    = cp.Add(up.Multiply(mHalfH));
            var line   = CreateLine(doc, view, lineStyleId, bottom, top, clashGroup, tgtLink, tgtElem);
            return line != null;
        }

        // ── FilledRegionType management ───────────────────────────────────────
        private ElementId? GetOrCreateFilledRegionType(Document doc, string hexColor, bool fallback)
        {
            string suffix   = fallback ? "FB" : (_opts.FillStyle == "Solid" ? "S" : "O");
            string typeName = $"LemoineClash_{hexColor}_{suffix}";

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                .FirstOrDefault(t => t.Name == typeName);
            if (existing != null) return existing.Id;

            var template = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                .FirstOrDefault();
            if (template == null) return null;

            var newType = template.Duplicate(typeName) as FilledRegionType;
            if (newType == null) return null;

            newType.IsMasking = false;

            var clr = ParseHexColor(hexColor);
            if (clr != null)
                newType.ForegroundPatternColor = clr;

            if (fallback)
            {
                var solidId = GetSolidFillId(doc);
                if (solidId != ElementId.InvalidElementId)
                    newType.ForegroundPatternId = solidId;
            }
            else if (_opts.FillStyle == "Solid")
            {
                var solidId = GetSolidFillId(doc);
                if (solidId != ElementId.InvalidElementId)
                    newType.ForegroundPatternId = solidId;
            }
            else
            {
                newType.ForegroundPatternId = ElementId.InvalidElementId;
            }

            return newType.Id;
        }

        // ── Detail line creation (tagged) ─────────────────────────────────────
        private static DetailCurve? CreateLine(
            Document doc, View view, ElementId lineStyleId, XYZ start, XYZ end, string group,
            ElementId targetLinkId, ElementId targetElemId)
        {
            var line = Line.CreateBound(start, end);
            var dc   = doc.Create.NewDetailCurve(view, line);
            ClashTagSchema.StampTag(dc, group, targetLinkId, targetElemId);

            if (lineStyleId != ElementId.InvalidElementId)
            {
                var gs = doc.GetElement(lineStyleId) as GraphicsStyle;
                if (gs != null) try { dc.LineStyle = gs; }
                    catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: apply detail-curve line style", ex); }
            }
            return dc;
        }

        // ── Helper utilities ──────────────────────────────────────────────────
        private ElementId ResolveLineStyleId(Document doc)
        {
            if (string.IsNullOrEmpty(_opts.CrossLineTypeName)) return ElementId.InvalidElementId;
            try
            {
                var linesCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                if (linesCat == null) return ElementId.InvalidElementId;
                foreach (Category sub in linesCat.SubCategories)
                {
                    if (sub.Name == _opts.CrossLineTypeName)
                    {
                        var gs = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                        return gs?.Id ?? ElementId.InvalidElementId;
                    }
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: resolve cross-line graphics style", ex); }
            return ElementId.InvalidElementId;
        }

        private static ElementId GetSolidFillId(Document doc)
        {
            foreach (var fp in new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                try { if (fp.GetFillPattern().IsSolidFill) return fp.Id; }
                catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: inspect fill pattern", ex); }
            }
            return ElementId.InvalidElementId;
        }

        private static RevitColor? ParseHexColor(string hex)
        {
            try
            {
                string h = hex.TrimStart('#');
                if (h.Length == 6)
                {
                    int v = Convert.ToInt32(h, 16);
                    return new RevitColor((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: parse hex colour", ex); }
            return null;
        }
    }
}
