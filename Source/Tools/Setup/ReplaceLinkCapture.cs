using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Read-only capture of the host's Revit links for the Replace Link picker. Every field is a
    /// Revit read wrapped so one bad link can't stop the rest from being listed. Must run on
    /// Revit's main/API thread (called from <c>IExternalCommand.Execute</c>).
    ///
    /// <para>A link that cannot be replaced in place (cloud-hosted, unresolvable path, or no
    /// placed instance) is still returned, flagged <see cref="HostLinkInfo.Replaceable"/> = false
    /// with a reason — the picker shows it disabled so the user sees WHY it is unavailable.</para>
    /// </summary>
    public static class ReplaceLinkCapture
    {
        public static List<HostLinkInfo> Capture(Document doc)
        {
            var list = new List<HostLinkInfo>();
            if (doc == null) return list;

            // Instance counts per type, read once — a type with no instance is loaded but not
            // placed, and replacing it would leave nothing on the model.
            var instanceCounts = new Dictionary<long, int>();
            try
            {
                foreach (var li in new FilteredElementCollector(doc)
                             .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    long tid = li.GetTypeId().Value;
                    instanceCounts[tid] = instanceCounts.TryGetValue(tid, out int n) ? n + 1 : 1;
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ReplaceLink: count link instances", ex); }

            foreach (var type in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
            {
                var info = new HostLinkInfo { TypeId = type.Id.Value };
                try
                {
                    // A nested link is owned by its parent link, not by this document — replacing
                    // it from here is not possible.
                    if (type.IsNestedLink)
                    {
                        info.Name          = SafeName(type, info);
                        info.Replaceable   = false;
                        info.BlockedReason = AppStrings.T("replaceLink.blocked.nested");
                        list.Add(info);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("ReplaceLink: read IsNestedLink", ex);
                    Note(info, AppStrings.T("replaceLink.readFail.nested"));
                }

                info.Name          = SafeName(type, info);
                info.InstanceCount = instanceCounts.TryGetValue(info.TypeId, out int count) ? count : 0;

                ExternalFileReference? extRef = null;
                try { extRef = type.GetExternalFileReference(); }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("ReplaceLink: read external file reference", ex);
                    Note(info, AppStrings.T("replaceLink.readFail.reference"));
                }

                if (extRef == null)
                {
                    info.Status        = AppStrings.T("replaceLink.status.unknown");
                    info.Replaceable   = false;
                    info.BlockedReason = AppStrings.T("replaceLink.blocked.noReference");
                    list.Add(info);
                    continue;
                }

                try
                {
                    var status  = extRef.GetLinkedFileStatus();
                    info.IsLoaded = status == LinkedFileStatus.Loaded;
                    info.Status   = status.ToString();
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("ReplaceLink: read linked file status", ex);
                    info.Status = AppStrings.T("replaceLink.status.unknown");
                    Note(info, AppStrings.T("replaceLink.readFail.status"));
                }

                try
                {
                    var mp = extRef.GetAbsolutePath();
                    // A cloud model path converts to an empty user-visible string — that is the
                    // documented way to tell a cloud link from a file-based one here.
                    info.Path    = ModelPathUtils.ConvertModelPathToUserVisiblePath(mp) ?? "";
                    info.IsCloud = string.IsNullOrEmpty(info.Path);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("ReplaceLink: resolve link path", ex);
                    info.Path = "";
                    Note(info, AppStrings.T("replaceLink.readFail.path"));
                }

                if (info.IsCloud)
                {
                    info.Replaceable   = false;
                    info.BlockedReason = AppStrings.T("replaceLink.blocked.cloud");
                }
                else if (string.IsNullOrEmpty(info.Path))
                {
                    info.Replaceable   = false;
                    info.BlockedReason = AppStrings.T("replaceLink.blocked.noPath");
                }
                else if (info.InstanceCount == 0)
                {
                    info.Replaceable   = false;
                    info.BlockedReason = AppStrings.T("replaceLink.blocked.noInstance");
                }

                if (string.IsNullOrEmpty(info.Name) && !string.IsNullOrEmpty(info.Path))
                    info.Name = Path.GetFileNameWithoutExtension(info.Path);

                list.Add(info);
            }

            return list.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string SafeName(RevitLinkType type, HostLinkInfo info)
        {
            try { return type.Name ?? ""; }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ReplaceLink: read link type name", ex);
                Note(info, AppStrings.T("replaceLink.readFail.name"));
                return "";
            }
        }

        /// <summary>Accumulates the names of fields that failed to read, so a row with
        /// placeholder values says so instead of passing them off as real.</summary>
        private static void Note(HostLinkInfo info, string field)
        {
            info.ReadWarning = string.IsNullOrEmpty(info.ReadWarning) ? field : info.ReadWarning + ", " + field;
        }
    }
}
