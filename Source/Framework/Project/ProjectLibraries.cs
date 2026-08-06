using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LemoineTools.Framework.Project
{
    // =========================================================================
    // ProjectLibraries — the one place tools go to load and save the libraries
    // that belong to a project.
    //
    // Load runs at command launch on the Revit main thread; save is staged and
    // committed by ProjectLibrarySaveHandler. This type also remembers what was
    // loaded, so a save can tell "the user changed it" from "the window merely
    // opened" and skip the transaction entirely in the second case.
    // =========================================================================
    public static class ProjectLibraries
    {
        private static readonly object _gate = new object();
        private static readonly Dictionary<string, string> _loaded =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Delegates that push a section's XML into the owning settings type.</summary>
        private static readonly List<(string Section, Action<string?> Apply)> _appliers =
            new List<(string, Action<string?>)>();

        /// <summary>
        /// Registers a settings type's loader. Called once at startup so this framework type
        /// never has to reference the tool assemblies' settings classes directly.
        /// </summary>
        public static void Register(string section, Action<string?> apply)
        {
            if (string.IsNullOrEmpty(section) || apply == null) return;
            lock (_gate)
            {
                _appliers.RemoveAll(a => string.Equals(a.Section, section, StringComparison.Ordinal));
                _appliers.Add((section, apply));
            }
        }

        /// <summary>
        /// Reads every library out of the document and pushes it into the settings types.
        /// Call on the Revit main thread at the start of a command, AFTER
        /// <see cref="DocumentKey.SetCurrent"/> — the settings buckets resolve by that key.
        /// </summary>
        public static void LoadForDocument(Document? doc)
        {
            var map = ProjectLibraryStore.Read(doc);

            List<(string Section, Action<string?> Apply)> appliers;
            lock (_gate)
            {
                _loaded.Clear();
                foreach (var kv in map) _loaded[kv.Key] = kv.Value;
                appliers = new List<(string, Action<string?>)>(_appliers);
            }

            foreach (var a in appliers)
            {
                try
                {
                    map.TryGetValue(a.Section, out var xml);
                    a.Apply(xml);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error($"ProjectLibraries: apply section '{a.Section}'", ex);
                }
            }
        }

        /// <summary>
        /// Saves a section into the document, but only when it differs from what was loaded.
        /// Safe to call from a tool window's OnClosed on its own STA thread.
        /// </summary>
        public static void Save(string section, string xml)
        {
            string? loaded;
            lock (_gate) _loaded.TryGetValue(section, out loaded);

            ProjectLibraryStore.SaveSection(section, xml, loaded);

            // Track what we just sent so a second close in the same session does not
            // re-stage an identical payload.
            lock (_gate) _loaded[section] = xml ?? "";
        }
    }
}
