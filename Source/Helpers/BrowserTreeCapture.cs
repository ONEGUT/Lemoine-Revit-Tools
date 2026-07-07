using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;

namespace LemoineTools.Helpers
{
    /// <summary>
    /// Captures the source document's Project Browser organization into a
    /// Revit-free <see cref="BrowserTree"/>. Call on the Revit main
    /// thread (inside an <c>IExternalCommand.Execute</c>) before opening the
    /// tool window, so every picker mirrors the browser layout the user sees —
    /// folder titles, nesting, ordering, and dependent views nested under
    /// their primary view.
    /// </summary>
    public static class BrowserTreeCapture
    {
        public static BrowserTree Capture(Document doc)
        {
            var tree = new BrowserTree();
            // Browser display order: Views, Legends, Schedules/Quantities, Sheets.
            AddRoot(tree, "views",     () => CaptureViews(doc));
            AddRoot(tree, "legends",   () => CaptureLegends(doc));
            AddRoot(tree, "schedules", () => CaptureSchedules(doc));
            AddRoot(tree, "sheets",    () => CaptureSheets(doc));
            return tree;
        }

        private static void AddRoot(BrowserTree tree, string what, Func<BrowserNode> capture)
        {
            try { tree.Roots.Add(capture()); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"BrowserTreeCapture: {what} capture failed — that browser node is missing from pickers", ex);
            }
        }

        // ── Views (graphical views only — schedules/legends live under their own
        //    browser nodes and no tool picks them) ──────────────────────────────
        private static BrowserNode CaptureViews(Document doc)
        {
            var org = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc);
            var root = new BrowserNode { Title = $"Views ({OrgName(org, "all")})" };

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate
                         && !(v is ViewSheet)
                         && !(v is ViewSchedule)
                         && v.ViewType != ViewType.Legend
                         && v.ViewType != ViewType.DrawingSheet
                         && v.ViewType != ViewType.ProjectBrowser
                         && v.ViewType != ViewType.SystemBrowser
                         && v.ViewType != ViewType.Internal
                         && v.ViewType != ViewType.Undefined)
                .ToList();

            // Dependent views nest under their primary, exactly as the browser shows them.
            var primaries  = new List<View>();
            var dependents = new Dictionary<long, List<View>>();
            foreach (var v in views)
            {
                var primaryId = v.GetPrimaryViewId();
                if (primaryId != ElementId.InvalidElementId)
                {
                    if (!dependents.TryGetValue(primaryId.Value, out var deps))
                        dependents[primaryId.Value] = deps = new List<View>();
                    deps.Add(v);
                }
                else primaries.Add(v);
            }

            var leavesById = new Dictionary<long, BrowserNode>();
            foreach (var v in primaries)
            {
                var leaf = new BrowserNode { Title = v.Name, Id = v.Id.Value };
                leavesById[v.Id.Value] = leaf;
                PlaceInFolders(root, org, v, leaf);
            }

            foreach (var pair in dependents)
            {
                foreach (var dep in pair.Value)
                {
                    var leaf = new BrowserNode { Title = dep.Name, Id = dep.Id.Value };
                    if (leavesById.TryGetValue(pair.Key, out var primaryLeaf))
                        primaryLeaf.Children.Add(leaf);
                    else
                        PlaceInFolders(root, org, dep, leaf); // primary filtered out — place by own path
                }
            }

            SortRecursive(root, IsDescending(org));
            return root;
        }

        // ── Legends (flat under their own browser root) ───────────────────────
        private static BrowserNode CaptureLegends(Document doc)
        {
            var root = new BrowserNode { Title = "Legends" };
            var legends = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Legend)
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var v in legends)
                root.Children.Add(new BrowserNode { Title = v.Name, Id = v.Id.Value });
            return root;
        }

        // ── Schedules (flat; titleblock revision schedules are browser-hidden) ─
        private static BrowserNode CaptureSchedules(Document doc)
        {
            var root = new BrowserNode { Title = "Schedules/Quantities" };
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var s in schedules)
                root.Children.Add(new BrowserNode { Title = s.Name, Id = s.Id.Value });
            return root;
        }

        // ── Sheets ────────────────────────────────────────────────────────────
        private static BrowserNode CaptureSheets(Document doc)
        {
            var org = BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc);
            var root = new BrowserNode { Title = $"Sheets ({OrgName(org, "all sheets")})" };

            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate)
                .ToList();

            foreach (var s in sheets)
            {
                var leaf = new BrowserNode
                {
                    Title   = $"{s.SheetNumber} - {s.Name}",
                    Id      = s.Id.Value,
                    IsSheet = true,
                };
                PlaceInFolders(root, org, s, leaf);
            }

            SortRecursive(root, IsDescending(org));
            return root;
        }

        // ── Shared helpers ────────────────────────────────────────────────────
        private static string OrgName(BrowserOrganization? org, string fallback)
        {
            var name = org?.Name;
            return string.IsNullOrWhiteSpace(name) ? fallback : name!;
        }

        private static bool IsDescending(BrowserOrganization? org)
        {
            try { return org != null && org.SortingOrder == SortingOrder.Descending; }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("BrowserTreeCapture: SortingOrder read failed — assuming ascending", ex);
                return false;
            }
        }

        /// <summary>Walks/creates the folder chain from the org's folder items and appends the leaf.</summary>
        private static void PlaceInFolders(BrowserNode root, BrowserOrganization? org,
                                           Element element, BrowserNode leaf)
        {
            var target = root;
            try
            {
                if (org != null)
                {
                    foreach (var folder in org.GetFolderItems(element.Id))
                    {
                        var folderName = folder.Name;
                        if (string.IsNullOrEmpty(folderName)) continue;
                        var next = target.Children.FirstOrDefault(
                            c => !c.IsLeaf && string.Equals(c.Title, folderName, StringComparison.Ordinal));
                        if (next == null)
                        {
                            next = new BrowserNode { Title = folderName };
                            target.Children.Add(next);
                        }
                        target = next;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"BrowserTreeCapture: folder path for '{leaf.Title}' unreadable — placed at root", ex);
                target = root;
            }
            target.Children.Add(leaf);
        }

        /// <summary>Folders before leaves, each alphabetical, honouring the org's sort direction.</summary>
        private static void SortRecursive(BrowserNode node, bool descending)
        {
            var folders = node.Children.Where(c => !c.IsLeaf);
            var leaves  = node.Children.Where(c => c.IsLeaf);
            var sorted  = (descending
                    ? folders.OrderByDescending(c => c.Title, StringComparer.OrdinalIgnoreCase)
                    : folders.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase))
                .Concat(descending
                    ? leaves.OrderByDescending(c => c.Title, StringComparer.OrdinalIgnoreCase)
                    : leaves.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase))
                .ToList();
            node.Children.Clear();
            node.Children.AddRange(sorted);
            foreach (var child in sorted) SortRecursive(child, descending);
        }
    }
}
