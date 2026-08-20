using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Finds the Autodesk Docs models that can serve as a replacement, using ONLY public Revit
    /// 2024 API.
    ///
    /// <para><b>Why there is no folder browser.</b> Revit ships an ACC browsing API —
    /// <c>Autodesk.Revit.DB.ForgeDM.CloudHub / CloudProject / CloudFolder / CloudModel</c> — but
    /// every one of those types is <c>internal</c> in <c>RevitAPI.dll</c> (verified against the
    /// assembly's TypeDef flags), so a plugin cannot call them. Neither can it manufacture a
    /// cloud reference from a path: <c>ExternalResourceReference.CreateFromCloudPath</c> is
    /// internal too. What IS public is enough to do the job from what Revit already has open or
    /// linked, plus GUIDs the user supplies.</para>
    ///
    /// <para>Read-only; nothing here mutates a model. Runs on Revit's main thread through an
    /// <see cref="ExternalEvent"/> because the picker window is on its own STA thread.</para>
    /// </summary>
    public sealed class CloudBrowseHandler : IExternalEventHandler
    {
        // ── Inputs (set before Raise) ─────────────────────────────────────────
        /// <summary>ElementId value of the link being replaced, so it is not offered as its own
        /// replacement — picking it would re-point a link at the model it already shows.</summary>
        public long ExcludeTypeId { get; set; }

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<CloudScanResult>? OnScanned { get; set; }
        /// <summary>Called with a user-facing reason when the scan could not complete. A blank
        /// list with no explanation is indistinguishable from a broken collector.</summary>
        public Action<string>?          OnError   { get; set; }

        public string GetName() => "LemoineTools.Tools.Setup.CloudBrowseHandler";

        public void Execute(UIApplication app)
        {
            try
            {
                var result = Scan(app);
                OnScanned?.Invoke(result);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CloudBrowse: scan", ex);
                Report(AppStrings.T("cloudPicker.error.fetch", ex.Message));
            }
            finally
            {
                // Session-long static handler — drop the run's payload (CLAUDE.md).
                ExcludeTypeId = 0;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        private CloudScanResult Scan(UIApplication app)
        {
            var result = new CloudScanResult();

            var hostDoc = app.ActiveUIDocument?.Document;
            var openDocs = new List<Document>();
            try
            {
                foreach (Document d in app.Application.Documents)
                    if (d != null && !d.IsFamilyDocument) openDocs.Add(d);
            }
            catch (Exception ex)
            { DiagnosticsLog.Swallowed("CloudBrowse: enumerate open documents", ex); }

            var openNode = new BrowserNode { Title = AppStrings.T("cloudPicker.groups.open") };
            var linkNode = new BrowserNode { Title = AppStrings.T("cloudPicker.groups.linked") };

            // ── 1. Cloud models open in this session ──────────────────────────
            // Their cloud path yields real GUIDs, which is the route that does not need an
            // existing link to borrow a reference from.
            var seenModels = new HashSet<Guid>();
            foreach (var d in openDocs)
            {
                var item = ReadOpenDocument(d);
                if (item == null) continue;
                if (item.ModelGuid != Guid.Empty && !seenModels.Add(item.ModelGuid)) continue;

                long id = result.Models.Count + 1;
                result.Models[id] = item;
                result.OpenCount++;
                openNode.Children.Add(new BrowserNode { Title = item.Name, Id = id });
            }

            // ── 2. Cloud links already loaded in the host ─────────────────────
            // No GUIDs are reachable from an ExternalResourceReference publicly, so these carry
            // their source link type id and the run re-reads the reference off that element.
            if (hostDoc != null)
            {
                foreach (var item in ReadCloudLinks(hostDoc, ExcludeTypeId))
                {
                    long id = result.Models.Count + 1;
                    result.Models[id] = item;
                    result.LinkCount++;
                    linkNode.Children.Add(new BrowserNode { Title = item.Name, Id = id });
                }
            }

            if (openNode.Children.Count > 0) result.Tree.Roots.Add(openNode);
            if (linkNode.Children.Count > 0) result.Tree.Roots.Add(linkNode);

            return result;
        }

        /// <summary>An open cloud document, as a replacement candidate. Null when the document is
        /// not a cloud model or its path can't be read.</summary>
        private CloudModelItem? ReadOpenDocument(Document doc)
        {
            try
            {
                if (!doc.IsModelInCloud) return null;

                var mp = doc.GetCloudModelPath();
                if (mp == null) return null;

                var item = new CloudModelItem
                {
                    Source = CloudModelSource.OpenDocument,
                    Name   = SafeTitle(doc),
                };

                try { item.ProjectGuid = mp.GetProjectGUID(); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("CloudBrowse: read project guid", ex); }
                try { item.ModelGuid = mp.GetModelGUID(); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("CloudBrowse: read model guid", ex); }
                try { item.Region = mp.Region ?? ""; }
                catch (Exception ex) { DiagnosticsLog.Swallowed("CloudBrowse: read region", ex); }

                if (!item.HasGuids)
                {
                    // Listing it would offer a pick the run cannot act on.
                    DiagnosticsLog.Warn("CloudBrowse: open cloud doc has no usable GUIDs", item.Name);
                    return null;
                }

                try { item.IsWorkshared = doc.IsWorkshared; }
                catch (Exception ex)
                {
                    // Leave it null — unknown, so the row shows no workshared badge at all.
                    DiagnosticsLog.Swallowed("CloudBrowse: read IsWorkshared", ex);
                }

                item.Detail = AppStrings.T("cloudPicker.detail.open");
                return item;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("CloudBrowse: read open document", ex);
                return null;
            }
        }

        /// <summary>Every cloud link already loaded in <paramref name="doc"/>, as candidates.</summary>
        private List<CloudModelItem> ReadCloudLinks(Document doc, long excludeTypeId)
        {
            var list = new List<CloudModelItem>();
            try
            {
                foreach (var type in new FilteredElementCollector(doc)
                             .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
                {
                    try
                    {
                        if (type.IsNestedLink) continue;
                        if (excludeTypeId != 0 && type.Id.Value == excludeTypeId) continue;

                        var reference = LinkReference.Resolve(type);
                        if (reference.Kind != LinkReferenceKind.Cloud) continue;

                        string name = !string.IsNullOrEmpty(reference.DisplayName)
                            ? reference.DisplayName
                            : SafeName(type);
                        if (string.IsNullOrEmpty(name)) continue;

                        list.Add(new CloudModelItem
                        {
                            Source       = CloudModelSource.ExistingLink,
                            Name         = name,
                            SourceTypeId = type.Id.Value,
                            Detail       = AppStrings.T("cloudPicker.detail.linked"),
                        });
                    }
                    catch (Exception ex)
                    { DiagnosticsLog.Swallowed("CloudBrowse: read cloud link", ex); }
                }
            }
            catch (Exception ex)
            { DiagnosticsLog.Swallowed("CloudBrowse: collect link types", ex); }

            return list
                .OrderBy(i => i.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string SafeTitle(Document doc)
        {
            try { return doc.Title ?? ""; }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("CloudBrowse: read document title", ex);
                return "";
            }
        }

        private static string SafeName(RevitLinkType type)
        {
            try { return type.Name ?? ""; }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("CloudBrowse: read link type name", ex);
                return "";
            }
        }

        private void Report(string message)
        {
            var cb = OnError;
            if (cb == null)
            {
                // No live picker to tell — still record it so the failure is never truly silent.
                DiagnosticsLog.Warn("CloudBrowse", message);
                return;
            }
            cb(message);
        }
    }
}
