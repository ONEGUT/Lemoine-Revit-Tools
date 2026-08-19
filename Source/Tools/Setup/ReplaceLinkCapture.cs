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
    /// <para>Both reference kinds are resolved through <see cref="LinkReference"/>: a file link
    /// is replaced by another file, a cloud (Autodesk Docs) link by another cloud model. A link
    /// that can't be replaced at all — nested, unresolvable, or loaded-but-not-placed — is still
    /// returned, flagged <see cref="HostLinkInfo.Replaceable"/> = false with a reason, so the
    /// picker shows it disabled and the user sees WHY rather than facing a shorter list.</para>
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

                // One guarded resolver for BOTH reference kinds. Calling
                // GetExternalFileReference() directly here is what made every link in a
                // cloud-hosted project report "No external file reference" — it throws rather
                // than returning null when the link is an external RESOURCE reference.
                var reference = LinkReference.Resolve(type);

                info.Kind    = reference.Kind;
                info.Path    = reference.Path;
                info.IsCloud = reference.Kind == LinkReferenceKind.Cloud;
                info.CloudName = reference.DisplayName;

                info.Status = reference.Status.HasValue
                    ? reference.Status.Value.ToString()
                    : AppStrings.T("replaceLink.status.unknown");
                info.IsLoaded = reference.Status == LinkedFileStatus.Loaded;

                // ReadWarning means "a value shown here is a placeholder". Being cloud-hosted is
                // a known state, not a failed read, and must never raise it.
                if (reference.ReadFailed)
                    foreach (var field in reference.FailedField.Split(','))
                        Note(info, FieldLabel(field.Trim()));

                switch (reference.Kind)
                {
                    case LinkReferenceKind.None:
                        info.Replaceable   = false;
                        info.BlockedReason = AppStrings.T("replaceLink.blocked.noReference");
                        break;

                    case LinkReferenceKind.File when string.IsNullOrEmpty(info.Path):
                        info.Replaceable   = false;
                        info.BlockedReason = AppStrings.T("replaceLink.blocked.noPath");
                        break;

                    case LinkReferenceKind.Cloud when string.IsNullOrEmpty(info.CloudName):
                        // Resolvable as cloud but unnamed — still replaceable; the name is
                        // cosmetic and the link type id is what the run acts on.
                        info.CloudName = info.Name;
                        break;
                }

                if (info.Replaceable && info.InstanceCount == 0)
                {
                    info.Replaceable   = false;
                    info.BlockedReason = AppStrings.T("replaceLink.blocked.noInstance");
                }

                if (string.IsNullOrEmpty(info.Name))
                {
                    if (!string.IsNullOrEmpty(info.Path))
                        info.Name = Path.GetFileNameWithoutExtension(info.Path);
                    else if (!string.IsNullOrEmpty(info.CloudName))
                        info.Name = info.CloudName;
                }

                list.Add(info);
            }

            return list.OrderBy(r => r.Name, NaturalOrderComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Maps a <see cref="LinkReference"/> field token onto its localized label.</summary>
        private static string FieldLabel(string token)
        {
            switch (token)
            {
                case "status":    return AppStrings.T("replaceLink.readFail.status");
                case "name":      return AppStrings.T("replaceLink.readFail.name");
                case "reference": return AppStrings.T("replaceLink.readFail.reference");
                default:          return AppStrings.T("replaceLink.readFail.reference");
            }
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
            if (string.IsNullOrEmpty(field)) return;
            if (!string.IsNullOrEmpty(info.ReadWarning) &&
                info.ReadWarning.Split(',').Any(f => f.Trim() == field)) return;   // no duplicates
            info.ReadWarning = string.IsNullOrEmpty(info.ReadWarning) ? field : info.ReadWarning + ", " + field;
        }
    }
}
