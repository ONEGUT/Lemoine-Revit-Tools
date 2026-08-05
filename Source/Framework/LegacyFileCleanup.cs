using System;
using System.IO;

namespace LemoineTools.Framework
{
    // =========================================================================
    // LegacyFileCleanup — one-shot tidy of superseded files in
    // %AppData%\LemoineTools, run once at startup.
    //
    // Deliberately conservative: a file is deleted only when its content
    // demonstrably lives somewhere else now. Anything that might still be the
    // only copy of a user's data is reported and left alone — a few KB of dead
    // XML costs nothing next to destroying a settings library that cannot be
    // reconstructed.
    // =========================================================================
    public static class LegacyFileCleanup
    {
        private static bool _ran;

        /// <summary>
        /// Runs once per session. Never throws: this is housekeeping, and a failure here
        /// must not stop the add-in loading.
        /// </summary>
        public static void RunOnce()
        {
            if (_ran) return;
            _ran = true;

            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                if (!Directory.Exists(dir)) return;

                // BatchExportSettings.xml was migrated into BulkExportSettings.xml on first
                // load. Delete it only once the migrated file actually exists, so a failed
                // migration keeps its source.
                string legacyExport = Path.Combine(dir, "BatchExportSettings.xml");
                string currentExport = Path.Combine(dir, "BulkExportSettings.xml");
                if (File.Exists(legacyExport) && File.Exists(currentExport))
                {
                    try
                    {
                        File.Delete(legacyExport);
                        DiagnosticsLog.Info("LegacyFileCleanup",
                            "Removed BatchExportSettings.xml (already migrated into BulkExportSettings.xml).");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed("LegacyFileCleanup: delete BatchExportSettings.xml", ex);
                    }
                }

                // Trade and legend libraries became per-project. The old machine-wide lists in
                // these files are no longer read (the properties are [XmlIgnore] and the storage
                // moved to per-document buckets), so they would sit there dead until the next
                // save rewrote the file. Back each one up ONCE so the user's previous library is
                // recoverable by hand — and so it can be turned into a seed file if they want it
                // back as an office standard.
                BackupOnce(dir, "LemoineAutoFiltersV2.xml");
                BackupOnce(dir, "LegendCreatorSettings.xml");

                // LemoineAutoFilters.xml is the V1 trade library. It was never migrated to V2,
                // so it is the ONLY remaining copy of whatever the user had before the schema
                // change — deleting it is irreversible and saves nothing worth having. It also
                // shares its name with the default filename of the trade EXPORT dialog, so a
                // file called this is not unambiguously legacy. Report it; never remove it.
                string legacyFilters = Path.Combine(dir, "LemoineAutoFilters.xml");
                if (File.Exists(legacyFilters))
                    DiagnosticsLog.Info("LegacyFileCleanup",
                        $"Pre-V2 trade library still present at {legacyFilters}. Not migrated and not deleted — " +
                        "remove it by hand if it is no longer wanted.");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LegacyFileCleanup: run", ex);
            }
        }

        /// <summary>
        /// Copies a settings file aside as <c>&lt;name&gt;.pre-per-project.bak</c>, once. The
        /// original is left in place — it is still the live settings file, just with its
        /// machine-wide library section no longer read. Never overwrites an existing backup,
        /// so a second run cannot replace the good copy with an already-emptied one.
        /// </summary>
        private static void BackupOnce(string dir, string fileName)
        {
            try
            {
                string src = Path.Combine(dir, fileName);
                if (!File.Exists(src)) return;

                string bak = Path.Combine(dir, fileName + ".pre-per-project.bak");
                if (File.Exists(bak)) return;

                File.Copy(src, bak);
                DiagnosticsLog.Info("LegacyFileCleanup",
                    $"Backed up {fileName} to {Path.GetFileName(bak)} before libraries became per-project.");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"LegacyFileCleanup: back up {fileName}", ex);
            }
        }
    }
}
