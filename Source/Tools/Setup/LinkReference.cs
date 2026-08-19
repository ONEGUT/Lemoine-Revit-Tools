using System;
using Autodesk.Revit.DB;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>What kind of reference a <see cref="RevitLinkType"/> points through.</summary>
    public enum LinkReferenceKind
    {
        /// <summary>Neither a file nor a recognised cloud resource — nothing can be resolved.</summary>
        None,
        /// <summary>A local / network / Revit Server file, reachable as an <see cref="ExternalFileReference"/>.</summary>
        File,
        /// <summary>An Autodesk Docs (ACC / BIM 360) model, reachable only as an
        /// <see cref="ExternalResourceReference"/>.</summary>
        Cloud,
    }

    /// <summary>Everything <see cref="LinkReference.Resolve"/> could read about one link.</summary>
    public sealed class LinkReferenceInfo
    {
        public LinkReferenceKind Kind { get; set; } = LinkReferenceKind.None;

        /// <summary>User-visible absolute path — <see cref="LinkReferenceKind.File"/> only. Empty for cloud.</summary>
        public string Path { get; set; } = "";

        /// <summary>Human-readable name of the cloud resource — <see cref="LinkReferenceKind.Cloud"/> only.</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>Load status, read from the link TYPE so it is available for both kinds.</summary>
        public LinkedFileStatus? Status { get; set; }

        /// <summary>The cloud model path, when one could be built. Cloud only.</summary>
        public ModelPath? CloudPath { get; set; }

        /// <summary>True only when a read that SHOULD have worked threw. Being cloud-hosted is a
        /// known state, not a read failure, and must never set this — a false read warning tells
        /// the user their data is a placeholder when it is not.</summary>
        public bool ReadFailed { get; set; }

        /// <summary>Names the field that failed, for the caller's warning text. Empty when none did.</summary>
        public string FailedField { get; set; } = "";
    }

    /// <summary>
    /// The one place that answers "what does this link point at?".
    ///
    /// <para><b>Why this exists.</b> <see cref="Element.GetExternalFileReference"/> THROWS when
    /// <see cref="Element.IsExternalFileReference"/> is false, and that is exactly the case for
    /// every Autodesk Docs (cloud) link — a cloud link is an external <i>resource</i> reference,
    /// not an external <i>file</i> reference. Calling it unguarded made every link in a
    /// cloud-hosted project report "No external file reference" plus a bogus "couldn't be read"
    /// warning, and skipped the cloud handling entirely. Six call sites had that bug; they all
    /// route through here now.</para>
    ///
    /// <para>Never throws — a link that cannot be resolved comes back as
    /// <see cref="LinkReferenceKind.None"/> with <see cref="LinkReferenceInfo.ReadFailed"/> set
    /// only when a genuine read failed.</para>
    /// </summary>
    public static class LinkReference
    {
        public static LinkReferenceInfo Resolve(RevitLinkType? type)
        {
            var info = new LinkReferenceInfo();
            if (type == null) return info;

            try { info.Status = type.GetLinkedFileStatus(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LinkReference: read linked file status", ex);
                Fail(info, "status");
            }

            // ── File reference ────────────────────────────────────────────────
            // IsExternalFileReference() is the REQUIRED guard: GetExternalFileReference()
            // throws without it, it does not return null.
            bool isFile = false;
            try { isFile = type.IsExternalFileReference(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LinkReference: test IsExternalFileReference", ex);
                Fail(info, "reference");
            }

            if (isFile)
            {
                try
                {
                    var extRef = type.GetExternalFileReference();
                    if (extRef == null)
                    {
                        Fail(info, "reference");
                        return info;
                    }

                    var mp = extRef.GetAbsolutePath();
                    info.Path = ModelPathUtils.ConvertModelPathToUserVisiblePath(mp) ?? "";
                    info.Kind = LinkReferenceKind.File;

                    // A file reference whose path resolves to nothing is not usable, but it is
                    // still a file reference — the caller decides what to do about the empty path.
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("LinkReference: read external file reference", ex);
                    Fail(info, "reference");
                }
                return info;
            }

            // ── Cloud (external resource) reference ───────────────────────────
            // A Revit cloud link is BuiltInExternalResourceTypes.RevitLink; a cloud-hosted IFC
            // link is IFCLink. Both are ordinary cloud links to this tool.
            if (TryCloud(type, ExternalResourceTypes.BuiltInExternalResourceTypes.RevitLink, info)) return info;
            if (TryCloud(type, ExternalResourceTypes.BuiltInExternalResourceTypes.IFCLink,   info)) return info;

            return info;   // Kind stays None — genuinely nothing to resolve.
        }

        /// <summary>Reads one external-resource type off the link, filling <paramref name="info"/>
        /// and returning true when it resolved. Never throws.</summary>
        private static bool TryCloud(RevitLinkType type, ExternalResourceType ert, LinkReferenceInfo info)
        {
            try
            {
                if (!type.RefersToExternalResourceReference(ert)) return false;

                var err = type.GetExternalResourceReference(ert);
                if (err == null) return false;

                info.Kind = LinkReferenceKind.Cloud;

                try { info.DisplayName = err.GetResourceShortDisplayName() ?? ""; }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("LinkReference: read cloud display name", ex);
                    Fail(info, "name");
                }

                if (string.IsNullOrEmpty(info.DisplayName))
                {
                    try { info.DisplayName = err.InSessionPath ?? ""; }
                    catch (Exception ex)
                    { DiagnosticsLog.Swallowed("LinkReference: read cloud in-session path", ex); }
                }

                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LinkReference: read external resource reference", ex);
                Fail(info, "reference");
                return false;
            }
        }

        private static void Fail(LinkReferenceInfo info, string field)
        {
            info.ReadFailed  = true;
            info.FailedField = string.IsNullOrEmpty(info.FailedField) ? field : info.FailedField + ", " + field;
        }
    }
}
