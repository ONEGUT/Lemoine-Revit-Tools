using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Read-only capture for Link Audit — every field a Revit read, wrapped so one bad link
    /// can't stop the rest from being reported. Shared by the command's initial build and the
    /// run handler's re-scan on the "Run" click, so both always see the same logic. Must run on
    /// Revit's main/API thread (called from IExternalCommand.Execute or an ExternalEventHandler).
    /// </summary>
    public static class LinkAuditCapture
    {
        public static LinkAuditData Capture(Document doc)
        {
            var data = new LinkAuditData();
            if (doc == null) return data;

            View? activeView = doc.ActiveView;

            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var row = new LinkAuditRow { LinkInstId = li.Id.Value };
                var linkType = SafeGetLinkType(doc, li);
                var linkDoc  = SafeGetLinkDocument(li);

                row.Name = ResolveName(li, linkType, linkDoc);
                row.LoadStatus = ResolveLoadStatus(linkType);
                row.Positioning = DetectPositioning(linkType);
                row.Pinned = li.Pinned;
                row.WorksetName = ResolveWorkset(doc, li);
                row.DisplayMode = ResolveDisplayMode(activeView, li);
                row.AttachmentType = ResolveAttachmentType(linkType);
                row.LastSaved = ResolveLastSaved(linkType);

                bool loaded = row.LoadStatus.Equals("Loaded", StringComparison.OrdinalIgnoreCase);
                bool byHost = row.DisplayMode.Equals("By Host View", StringComparison.OrdinalIgnoreCase)
                              || row.DisplayMode.Equals("n/a", StringComparison.OrdinalIgnoreCase);
                row.IsWarning = !loaded || !byHost;

                data.Rows.Add(row);
            }

            data.Rows = data.Rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return data;
        }

        private static RevitLinkType? SafeGetLinkType(Document doc, RevitLinkInstance li)
        {
            try { return doc.GetElement(li.GetTypeId()) as RevitLinkType; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LinkAudit: resolve link type", ex); return null; }
        }

        private static Document? SafeGetLinkDocument(RevitLinkInstance li)
        {
            try { return li.GetLinkDocument(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LinkAudit: resolve link document", ex); return null; }
        }

        private static string ResolveName(RevitLinkInstance li, RevitLinkType? linkType, Document? linkDoc)
        {
            if (linkDoc != null)
            {
                try { return "[" + Path.GetFileNameWithoutExtension(linkDoc.Title) + "]"; }
                catch (Exception ex) { DiagnosticsLog.Swallowed("LinkAudit: name from link document", ex); }
            }
            // Guarded resolver — GetExternalFileReference() throws on a cloud link, which used
            // to lose the name of every link in an Autodesk Docs project.
            var reference = LinkReference.Resolve(linkType);
            if (reference.Kind == LinkReferenceKind.File && !string.IsNullOrEmpty(reference.Path))
            {
                try { return "[" + Path.GetFileNameWithoutExtension(reference.Path) + "]"; }
                catch (Exception ex) { DiagnosticsLog.Swallowed("LinkAudit: name from file reference", ex); }
            }
            if (reference.Kind == LinkReferenceKind.Cloud && !string.IsNullOrEmpty(reference.DisplayName))
                return "[" + reference.DisplayName + "]";

            try { return "[" + li.Name + "]"; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LinkAudit: name from instance", ex); return "[link]"; }
        }

        private static string ResolveLoadStatus(RevitLinkType? linkType)
        {
            if (linkType == null) return "Unknown";
            try { return linkType.GetLinkedFileStatus().ToString(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LinkAudit: read load status", ex); return "Unknown"; }
        }

        /// <summary>
        /// Origin-to-Origin vs Shared Coordinates positioning. UNVERIFIED — the Revit 2024 public
        /// API does not expose a documented, reliable getter for a link's positioning mode after
        /// creation (only settable at link-in time via <see cref="ImportPlacement"/>, which is not
        /// itself readable back). Reporting a guessed value here would be worse than reporting
        /// "Unknown": a wrong "Shared" reading could mislead a coordinator into skipping Align
        /// Coordinates for a link that actually needs it (CLAUDE.md: Origin-to-Origin links ignore
        /// base-point corrections entirely). Needs confirming against Revit's own Manage Links
        /// "Positioning" column on a real Windows session before this can report a real value.
        /// </summary>
        private static string DetectPositioning(RevitLinkType? linkType)
        {
            return "Unknown — verify in Manage Links";
        }

        private static string ResolveWorkset(Document doc, RevitLinkInstance li)
        {
            if (!doc.IsWorkshared) return "—";
            try
            {
                var table = doc.GetWorksetTable();
                var workset = table?.GetWorkset(li.WorksetId);
                return workset != null ? workset.Name : "Unknown";
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LinkAudit: read workset", ex); return "Unknown"; }
        }

        private static string ResolveDisplayMode(View? activeView, RevitLinkInstance li)
        {
            if (activeView == null) return "n/a";
            try
            {
                RevitLinkGraphicsSettings? gs = activeView.GetLinkOverrides(li.Id);
                LinkVisibility mode = gs?.LinkVisibilityType ?? LinkVisibility.ByHostView;
                return mode == LinkVisibility.ByHostView ? "By Host View" : mode.ToString();
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LinkAudit: read display mode", ex); return "n/a"; }
        }

        private static string ResolveAttachmentType(RevitLinkType? linkType)
        {
            if (linkType == null) return "Unknown";
            try { return linkType.AttachmentType.ToString(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LinkAudit: read attachment type", ex); return "Unknown"; }
        }

        private static string ResolveLastSaved(RevitLinkType? linkType)
        {
            if (linkType == null) return "n/a";
            try
            {
                var reference = LinkReference.Resolve(linkType);
                // A cloud model has no local file to stat — say so, rather than reporting "n/a"
                // as though the read had failed.
                if (reference.Kind == LinkReferenceKind.Cloud) return "n/a (cloud)";
                if (reference.Kind != LinkReferenceKind.File)  return "n/a";
                if (string.IsNullOrEmpty(reference.Path))      return "n/a";
                if (!File.Exists(reference.Path))              return "n/a (not found)";
                return File.GetLastWriteTime(reference.Path).ToString("yyyy-MM-dd HH:mm");
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LinkAudit: read last saved", ex); return "n/a"; }
        }
    }
}
