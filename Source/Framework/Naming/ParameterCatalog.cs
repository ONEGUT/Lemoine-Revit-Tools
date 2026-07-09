using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LemoineTools.Framework.Naming
{
    /// <summary>One bindable parameter, as offered by the Naming settings page's parameter picker.</summary>
    public sealed class ParameterCatalogEntry
    {
        public string  Name        { get; set; } = "";
        /// <summary>Set for shared parameters — the authoritative binding (see CLAUDE.md on
        /// name-only lookups silently picking the wrong duplicate).</summary>
        public Guid?   Guid        { get; set; }
        public string  StorageType { get; set; } = "";
        /// <summary>Display origin: "Project parameter" | "Shared parameter" | "Built-in (common)".</summary>
        public string  OriginLabel { get; set; } = "";
        public bool    IsInstance  { get; set; }
        /// <summary>Value read off the sampled element, shown as a live preview in the settings page.</summary>
        public string  SampleValue { get; set; } = "";
    }

    /// <summary>Immutable snapshot handed from the Revit-main-thread command to the (Revit-free)
    /// Naming settings window.</summary>
    public sealed class ParameterCatalogSnapshot
    {
        public List<ParameterCatalogEntry> SheetParameters       { get; set; } = new List<ParameterCatalogEntry>();
        public List<ParameterCatalogEntry> ViewParameters        { get; set; } = new List<ParameterCatalogEntry>();
        public List<ParameterCatalogEntry> ProjectInfoParameters { get; set; } = new List<ParameterCatalogEntry>();
        public string DocTitle { get; set; } = "";
    }

    /// <summary>
    /// MAIN-THREAD-ONLY capture of every parameter a user token could bind. Taken once when the
    /// Naming settings page opens, mirroring the existing
    /// <c>AutoFiltersSettings.CaptureFilterableCategories(doc)</c> pattern — capture in the
    /// command, hand the immutable snapshot to the window. Never call from a tool window's own
    /// STA thread. Empty results are reported (repo rule: a survey that finds zero must say so),
    /// never silently returned as an empty list with no explanation.
    /// </summary>
    public static class ParameterCatalog
    {
        public static ParameterCatalogSnapshot Capture(Document doc)
        {
            var snapshot = new ParameterCatalogSnapshot();
            if (doc == null) return snapshot;

            try { snapshot.DocTitle = doc.Title ?? ""; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ParameterCatalog: read doc title", ex); }

            try { CaptureBindings(doc, snapshot); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ParameterCatalog: capture parameter bindings", ex); }

            try { CaptureFromSampleElements(doc, snapshot); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ParameterCatalog: capture from sample elements", ex); }

            try { CaptureProjectInfo(doc, snapshot); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ParameterCatalog: capture project information", ex); }

            snapshot.SheetParameters = Dedupe(snapshot.SheetParameters);
            snapshot.ViewParameters  = Dedupe(snapshot.ViewParameters);
            snapshot.ProjectInfoParameters = Dedupe(snapshot.ProjectInfoParameters);

            if (snapshot.SheetParameters.Count == 0)
                DiagnosticsLog.Info("ParameterCatalog", "No bindable sheet parameters found");
            if (snapshot.ViewParameters.Count == 0)
                DiagnosticsLog.Info("ParameterCatalog", "No bindable view parameters found");

            return snapshot;
        }

        private static List<ParameterCatalogEntry> Dedupe(List<ParameterCatalogEntry> entries) =>
            entries
                .GroupBy(e => (e.Name.ToUpperInvariant(), e.Guid))
                .Select(g => g.OrderByDescending(e => !string.IsNullOrEmpty(e.StorageType)).First())
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // ── Pass 1: catalog-wide bindings (catches params not populated on the sampled elements) ──
        private static void CaptureBindings(Document doc, ParameterCatalogSnapshot snapshot)
        {
            long sheetsCatId = (long)BuiltInCategory.OST_Sheets;
            long viewsCatId  = (long)BuiltInCategory.OST_Views;

            var map = doc.ParameterBindings;
            var it  = map.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                try
                {
                    if (!(it.Key is InternalDefinition def)) continue;
                    if (!(it.Current is ElementBinding binding) || binding.Categories == null) continue;

                    bool sheet = false, view = false;
                    foreach (Category cat in binding.Categories)
                    {
                        if (cat.Id.Value == sheetsCatId) sheet = true;
                        if (cat.Id.Value == viewsCatId)  view  = true;
                    }
                    if (!sheet && !view) continue;

                    var entry = BuildEntryFromBinding(doc, def, binding is InstanceBinding);
                    if (sheet) snapshot.SheetParameters.Add(entry);
                    if (view)  snapshot.ViewParameters.Add(entry);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("ParameterCatalog: read one parameter binding", ex);
                }
            }
        }

        private static ParameterCatalogEntry BuildEntryFromBinding(Document doc, InternalDefinition def, bool isInstance)
        {
            Guid?  guid   = null;
            string origin = AppStrings.T("naming.settings.paramOrigin.project");

            try
            {
                if (def.BuiltInParameter != BuiltInParameter.INVALID)
                {
                    origin = AppStrings.T("naming.settings.paramOrigin.builtIn");
                }
                else if (doc.GetElement(def.Id) is SharedParameterElement spe)
                {
                    guid   = spe.GuidValue;
                    origin = AppStrings.T("naming.settings.paramOrigin.shared");
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ParameterCatalog: classify binding '{def.Name}'", ex);
            }

            return new ParameterCatalogEntry
            {
                Name        = def.Name,
                Guid        = guid,
                OriginLabel = origin,
                IsInstance  = isInstance,
            };
        }

        // ── Pass 2: sample a live sheet + a live non-template view for real storage types and
        // preview values, and to pick up project/family-borne parameters the binding pass misses ──
        private static void CaptureFromSampleElements(Document doc, ParameterCatalogSnapshot snapshot)
        {
            var sheet = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .FirstOrDefault();
            if (sheet != null) CaptureFromElement(sheet, snapshot.SheetParameters);

            var view = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && !(v is ViewSheet));
            if (view != null) CaptureFromElement(view, snapshot.ViewParameters);
        }

        private static void CaptureProjectInfo(Document doc, ParameterCatalogSnapshot snapshot)
        {
            var pi = doc.ProjectInformation;
            if (pi == null) return;
            CaptureFromElement(pi, snapshot.ProjectInfoParameters);
        }

        private static void CaptureFromElement(Element element, List<ParameterCatalogEntry> into)
        {
            IList<Parameter> parameters;
            try { parameters = element.GetOrderedParameters(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ParameterCatalog: enumerate parameters on '{element.Name}'", ex);
                return;
            }

            foreach (var p in parameters)
            {
                try
                {
                    string name = p.Definition?.Name ?? "";
                    if (string.IsNullOrEmpty(name)) continue;

                    Guid?  guid;
                    string origin;
                    if (p.IsShared)
                    {
                        guid   = p.GUID;
                        origin = AppStrings.T("naming.settings.paramOrigin.shared");
                    }
                    else
                    {
                        guid = null;
                        bool builtIn = p.Definition is InternalDefinition id && id.BuiltInParameter != BuiltInParameter.INVALID;
                        origin = builtIn
                            ? AppStrings.T("naming.settings.paramOrigin.builtIn")
                            : AppStrings.T("naming.settings.paramOrigin.project");
                    }

                    string sample = "";
                    try { sample = p.StorageType == StorageType.String ? (p.AsString() ?? "") : (p.AsValueString() ?? ""); }
                    catch (Exception exSample)
                    {
                        DiagnosticsLog.Swallowed($"ParameterCatalog: read sample value for '{name}'", exSample);
                    }

                    var existing = into.FirstOrDefault(e =>
                        string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        Equals(e.Guid, guid));

                    if (existing != null)
                    {
                        existing.StorageType = p.StorageType.ToString();
                        existing.SampleValue = sample;
                        existing.IsInstance  = true;
                    }
                    else
                    {
                        into.Add(new ParameterCatalogEntry
                        {
                            Name        = name,
                            Guid        = guid,
                            StorageType = p.StorageType.ToString(),
                            OriginLabel = origin,
                            IsInstance  = true,
                            SampleValue = sample,
                        });
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("ParameterCatalog: read one element parameter", ex);
                }
            }
        }
    }
}
