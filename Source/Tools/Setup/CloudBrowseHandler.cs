using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ForgeDM;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Read-only Autodesk Docs (ACC / BIM 360) browsing for the cloud model picker.
    ///
    /// <para>Every call here is a Revit API call that goes to the network, so it runs on the
    /// Revit main thread through an <see cref="ExternalEvent"/> — the picker window lives on the
    /// tool's own STA thread and cannot make them directly. Results are handed back as Revit-free
    /// DTOs; the window marshals them onto its dispatcher itself.</para>
    ///
    /// <para>Nothing here mutates the model.</para>
    /// </summary>
    public sealed class CloudBrowseHandler : IExternalEventHandler
    {
        // ── Inputs (set before Raise) ─────────────────────────────────────────
        public CloudBrowseRequest Request     { get; set; } = CloudBrowseRequest.Hubs;
        public string             HubId       { get; set; } = "";
        public string             ProjectId   { get; set; } = "";

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<List<CloudHubItem>, List<CloudProjectItem>>? OnHubs     { get; set; }
        public Action<List<CloudProjectItem>>?                     OnProjects { get; set; }
        public Action<CloudTreeResult>?                            OnTree     { get; set; }
        /// <summary>Called with a user-facing reason when a fetch could not complete. A blank
        /// tree with no explanation is indistinguishable from a broken collector, so every
        /// failure path ends here.</summary>
        public Action<string>?                                     OnError    { get; set; }

        /// <summary>The host document's own hub/project, captured at launch so the picker can
        /// default to them. Empty when the host is not a cloud model.</summary>
        public Guid   HostProjectGuid { get; set; }
        public string HostRegion      { get; set; } = "";

        // Traversal guards — an ACC project can be arbitrarily deep and wide, and this runs
        // synchronously on Revit's main thread. Hitting either cap is REPORTED, never silent.
        private const int MaxDepth   = 8;
        private const int MaxFolders = 400;

        public string GetName() => "LemoineTools.Tools.Setup.CloudBrowseHandler";

        public void Execute(UIApplication app)
        {
            try
            {
                switch (Request)
                {
                    case CloudBrowseRequest.Hubs:     DoHubs();     break;
                    case CloudBrowseRequest.Projects: DoProjects(); break;
                    case CloudBrowseRequest.Tree:     DoTree();     break;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CloudBrowse: fetch", ex);
                Report(AppStrings.T("cloudPicker.error.fetch", ex.Message));
            }
            finally
            {
                // Static handler — it outlives the window, so nothing from this run may be
                // left parked on it (CLAUDE.md memory discipline).
                HubId = ""; ProjectId = "";
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        private void DoHubs()
        {
            var hubs = new List<CloudHubItem>();
            IList<CloudHub>? raw = null;
            try { raw = CloudHub.GetAllHubs(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CloudBrowse: GetAllHubs", ex);
                Report(AppStrings.T("cloudPicker.error.signIn", ex.Message));
                return;
            }

            if (raw == null || raw.Count == 0)
            {
                // Almost always "not signed in to Autodesk Docs" — say so rather than showing
                // an empty picker the user cannot interpret.
                Report(AppStrings.T("cloudPicker.error.noHubs"));
                return;
            }

            foreach (var h in raw)
            {
                try
                {
                    hubs.Add(new CloudHubItem
                    {
                        Id     = h.Id     ?? "",
                        Name   = h.Name   ?? "",
                        Region = h.Region ?? "",
                    });
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("CloudBrowse: read hub", ex); }
            }

            if (hubs.Count == 0)
            {
                Report(AppStrings.T("cloudPicker.error.noHubs"));
                return;
            }

            // Default to whichever hub owns the host model. Each GetProjects() is a network
            // round-trip, so the hubs whose region matches the host are tried first and the
            // walk stops at the first hit — the common case costs exactly one fetch.
            var ordered = hubs
                .OrderByDescending(h => !string.IsNullOrEmpty(HostRegion) &&
                                        string.Equals(h.Region, HostRegion, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var chosenProjects = new List<CloudProjectItem>();

            foreach (var candidate in ordered)
            {
                var projects = ReadProjectsFor(raw, candidate.Id);

                // An empty hub is never a useful default — keep walking, or the picker opens
                // blank on an account whose first hub happens to hold nothing.
                if (projects.Count == 0) continue;

                if (chosenProjects.Count == 0) chosenProjects = projects;   // first usable = fallback

                if (HostProjectGuid == Guid.Empty) break;                   // nothing to search for
                if (projects.Any(p => p.Guid == HostProjectGuid))
                {
                    chosenProjects = projects;
                    break;
                }
            }

            if (chosenProjects.Count == 0)
                Report(AppStrings.T("cloudPicker.error.noProjects"));

            OnHubs?.Invoke(hubs, chosenProjects);
        }

        private void DoProjects()
        {
            IList<CloudHub>? raw = null;
            try { raw = CloudHub.GetAllHubs(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CloudBrowse: GetAllHubs for projects", ex);
                Report(AppStrings.T("cloudPicker.error.fetch", ex.Message));
                return;
            }
            if (raw == null) { Report(AppStrings.T("cloudPicker.error.noHubs")); return; }

            OnProjects?.Invoke(ReadProjectsFor(raw, HubId));
        }

        private List<CloudProjectItem> ReadProjectsFor(IList<CloudHub> hubs, string hubId)
        {
            var list = new List<CloudProjectItem>();
            CloudHub? hub = null;
            foreach (var h in hubs)
            {
                try { if (string.Equals(h.Id, hubId, StringComparison.Ordinal)) { hub = h; break; } }
                catch (Exception ex) { DiagnosticsLog.Swallowed("CloudBrowse: match hub id", ex); }
            }
            if (hub == null) return list;

            string region = "";
            try { region = hub.Region ?? ""; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CloudBrowse: read hub region", ex); }

            IList<CloudProject>? projects = null;
            try { projects = hub.GetProjects(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CloudBrowse: GetProjects", ex);
                Report(AppStrings.T("cloudPicker.error.fetch", ex.Message));
                return list;
            }

            if (projects == null || projects.Count == 0)
            {
                // Not reported to the user from here: the default-hub search below probes several
                // hubs, and an empty one along the way is normal, not a failure. The picker states
                // the zero result for the hub actually being shown.
                DiagnosticsLog.Info("CloudBrowse: hub has no projects", hubId);
                return list;
            }

            foreach (var p in projects)
            {
                try
                {
                    list.Add(new CloudProjectItem
                    {
                        Id     = p.Id   ?? "",
                        Name   = p.Name ?? "",
                        Guid   = p.GUID,
                        HubId  = hubId,
                        Region = region,
                    });
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("CloudBrowse: read project", ex); }
            }

            return list
                .OrderBy(p => p.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ══════════════════════════════════════════════════════════════════════
        private void DoTree()
        {
            IList<CloudHub>? hubs = null;
            try { hubs = CloudHub.GetAllHubs(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CloudBrowse: GetAllHubs for tree", ex);
                Report(AppStrings.T("cloudPicker.error.fetch", ex.Message));
                return;
            }
            if (hubs == null) { Report(AppStrings.T("cloudPicker.error.noHubs")); return; }

            CloudProject? project = null;
            string        region  = "";
            foreach (var h in hubs)
            {
                try
                {
                    if (!string.Equals(h.Id, HubId, StringComparison.Ordinal)) continue;
                    region = h.Region ?? "";
                    foreach (var p in h.GetProjects() ?? new List<CloudProject>())
                    {
                        if (!string.Equals(p.Id, ProjectId, StringComparison.Ordinal)) continue;
                        project = p;
                        break;
                    }
                    break;
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("CloudBrowse: locate project", ex); }
            }

            if (project == null)
            {
                Report(AppStrings.T("cloudPicker.error.projectGone"));
                return;
            }

            var result = new CloudTreeResult();
            Guid projGuid = Guid.Empty;
            try { projGuid = project.GUID; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CloudBrowse: read project guid", ex); }

            IList<CloudFolder>? roots = null;
            try { roots = project.GetFolders(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CloudBrowse: GetFolders", ex);
                Report(AppStrings.T("cloudPicker.error.fetch", ex.Message));
                return;
            }

            if (roots != null)
            {
                foreach (var f in roots)
                {
                    var node = BuildFolder(f, "", 0, region, projGuid, result);
                    if (node != null) result.Tree.Roots.Add(node);
                }
            }

            // A zero result is stated, never left as an empty box (CLAUDE.md).
            OnTree?.Invoke(result);
        }

        /// <summary>Recursively turns one cloud folder into a <see cref="BrowserNode"/>, adding its
        /// models as leaves. Returns null for a folder that could not be read at all.</summary>
        private BrowserNode? BuildFolder(CloudFolder folder, string parentPath, int depth,
                                         string region, Guid projGuid, CloudTreeResult result)
        {
            if (folder == null) return null;

            if (depth >= MaxDepth || result.FolderCount >= MaxFolders)
            {
                result.Truncated = true;
                return null;
            }

            string name;
            try { name = folder.Name ?? ""; }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("CloudBrowse: read folder name", ex);
                return null;
            }

            var node = new BrowserNode { Title = name };
            result.FolderCount++;
            string path = string.IsNullOrEmpty(parentPath) ? name : parentPath + " / " + name;

            // ── Models in this folder ─────────────────────────────────────────
            IList<CloudModel>? models = null;
            try { models = folder.GetModels(); }
            catch (Exception ex)
            { DiagnosticsLog.Swallowed($"CloudBrowse: GetModels in '{name}'", ex); }

            if (models != null)
            {
                var leaves = new List<BrowserNode>();
                foreach (var m in models)
                {
                    try
                    {
                        long id = result.Models.Count + 1;   // synthetic — see CloudTreeResult
                        var item = new CloudModelItem
                        {
                            Name         = m.Name ?? "",
                            ModelGuid    = m.GUID,
                            ProjectGuid  = projGuid,
                            Region       = region,
                            IsWorkshared = m.IsWorkshared,
                            FolderPath   = path,
                        };
                        result.Models[id] = item;
                        result.ModelCount++;
                        leaves.Add(new BrowserNode { Title = item.Name, Id = id });
                    }
                    catch (Exception ex)
                    { DiagnosticsLog.Swallowed($"CloudBrowse: read model in '{name}'", ex); }
                }

                foreach (var leaf in leaves.OrderBy(l => l.Title, NaturalOrderComparer.OrdinalIgnoreCase))
                    node.Children.Add(leaf);
            }

            // ── Sub-folders ───────────────────────────────────────────────────
            IList<CloudFolder>? subs = null;
            try { subs = folder.GetFolders(); }
            catch (Exception ex)
            { DiagnosticsLog.Swallowed($"CloudBrowse: GetFolders in '{name}'", ex); }

            if (subs != null)
            {
                var childNodes = new List<BrowserNode>();
                foreach (var sub in subs)
                {
                    var child = BuildFolder(sub, path, depth + 1, region, projGuid, result);
                    if (child != null) childNodes.Add(child);
                }
                foreach (var c in childNodes.OrderBy(c => c.Title, NaturalOrderComparer.OrdinalIgnoreCase))
                    node.Children.Add(c);
            }

            return node;
        }

        // ══════════════════════════════════════════════════════════════════════
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
