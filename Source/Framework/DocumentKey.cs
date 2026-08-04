using System;
using Autodesk.Revit.DB;

namespace LemoineTools.Framework
{
    // =========================================================================
    // DocumentKey — stable per-project identity for settings that must not leak
    // between projects but have no created element to stamp.
    //
    // Element picks and output folders are project-scoped, but unlike a legend
    // view or a generated filter there is nothing in the model to hang an
    // extensible-storage stamp on. So they stay in %AppData% and become keyed
    // BY DOCUMENT instead of shared by all of them.
    //
    // This is NOT a sidecar file: nothing is written next to the .rvt, so cloud
    // and BIM360 models — whose PathName points at a local cache — are unaffected.
    // Nothing is written into the document either, so there is no dirty-document
    // prompt, no undo entry, and no workset checkout.
    //
    // Threading: Current must be set on the Revit main thread (where a Document
    // exists) by the launching command. Tool windows run on their own STA threads
    // and never touch the Revit API, so they read Current instead.
    // =========================================================================
    public static class DocumentKey
    {
        private static readonly object _gate = new object();
        private static string? _current;

        /// <summary>
        /// Identity of the document the user is currently working in, or null when no
        /// document is open or it has never been saved. Null means "no project scope":
        /// callers fall back to their machine-wide default rather than inventing a key.
        /// </summary>
        public static string? Current
        {
            get { lock (_gate) return _current; }
        }

        /// <summary>
        /// Records the active document's identity. Call at the start of every command's
        /// Execute — those run on the Revit main thread with the document in hand, so
        /// this stays fresh as the user moves between projects.
        /// </summary>
        public static void SetCurrent(Document? doc)
        {
            string? key = For(doc);
            lock (_gate) _current = key;
        }

        /// <summary>
        /// Stable identity for a document, or null when it has none yet.
        ///
        /// Ordering matters. A cloud model's PathName is a LOCAL CACHE path, different on
        /// every machine, so the cloud path is asked for first; a workshared model is
        /// identified by its CENTRAL path so every collaborator agrees on one key. Only a
        /// plain single-user file falls through to PathName.
        ///
        /// An unsaved document returns null rather than a guessed key — writing settings
        /// under a key that changes the moment the user saves would silently lose them.
        /// </summary>
        public static string? For(Document? doc)
        {
            if (doc == null) return null;
            try
            {
                if (doc.IsFamilyDocument) return null;

                if (doc.IsModelInCloud)
                {
                    var mp = doc.GetCloudModelPath();
                    if (mp != null)
                        return "cloud:" + ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                }

                if (doc.IsWorkshared)
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    if (central != null)
                    {
                        string s = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                        if (!string.IsNullOrEmpty(s)) return "central:" + s;
                    }
                }

                return string.IsNullOrEmpty(doc.PathName) ? null : "file:" + doc.PathName;
            }
            catch (Exception ex)
            {
                // Never let an identity probe take down a command. No key means the caller
                // uses its machine-wide default, which is the pre-existing behaviour.
                DiagnosticsLog.Swallowed("DocumentKey: resolve document identity", ex);
                return null;
            }
        }
    }
}
