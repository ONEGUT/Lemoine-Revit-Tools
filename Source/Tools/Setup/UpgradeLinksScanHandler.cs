using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Read-only pass that reads each queued file's saved-in Revit version and worksharing flag via
    /// <see cref="BasicFileInfo.Extract(string)"/> — without opening the document. Runs on the Revit
    /// main thread (the API is single-threaded) and hands results back to the ViewModel, which paints
    /// the version badges. Never mutates anything.
    /// </summary>
    public sealed class UpgradeLinksScanHandler : IExternalEventHandler
    {
        public List<string> Paths { get; set; } = new List<string>();

        /// <summary>The current Revit's saved-file version year, captured during the last scan
        /// (e.g. "2026") — used by the ViewModel's "files newer than this cannot be opened" note.</summary>
        public string CurrentVersionNumber { get; private set; } = "";

        public Action<List<UpgradeFileScan>>? OnScanned { get; set; }
        public Action<string>?                OnError   { get; set; }

        public string GetName() => "LemoineTools.Tools.Setup.UpgradeLinksScanHandler";

        public void Execute(UIApplication app)
        {
            try
            {
                string current = "";
                try { current = app.Application.VersionNumber ?? ""; }
                catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: read current version", ex); }
                CurrentVersionNumber = current;
                bool currentParsed = int.TryParse(current, out int currentYear);

                var results = new List<UpgradeFileScan>();
                foreach (var path in Paths)
                {
                    var scan = new UpgradeFileScan { Path = path };
                    try
                    {
                        var bfi = BasicFileInfo.Extract(path);
                        if (bfi == null)
                        {
                            scan.Readable = false;
                            scan.Error    = "BasicFileInfo.Extract returned null";
                        }
                        else
                        {
                            scan.IsWorkshared = bfi.IsWorkshared;
                            scan.Version      = ReadVersion(bfi);
                            scan.IsCurrent    = current.Length > 0 && scan.Version == current;
                            if (currentParsed && int.TryParse(scan.Version, out int fileYear))
                                scan.IsFutureVersion = fileYear > currentYear;
                        }
                    }
                    catch (Exception ex)
                    {
                        scan.Readable = false;
                        scan.Error    = ex.Message;
                        DiagnosticsLog.Swallowed($"UpgradeLinks: scan {path}", ex);
                    }
                    results.Add(scan);
                }

                OnScanned?.Invoke(results);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("UpgradeLinksScanHandler.Execute", ex);
                OnError?.Invoke(ex.Message);
            }
            finally
            {
                Paths = new List<string>();
            }
        }

        // BasicFileInfo.Format carries the saved-in Revit release; different builds have formatted it
        // as a bare "2021" or an "Autodesk Revit 2021 (Build …)" string, so pull the first 4-digit year.
        private static string ReadVersion(BasicFileInfo bfi)
        {
            string raw;
            try { raw = bfi.Format ?? ""; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: read Format", ex); return "?"; }

            if (string.IsNullOrWhiteSpace(raw)) return "?";
            var m = Regex.Match(raw, @"\b(19|20)\d{2}\b");
            return m.Success ? m.Value : raw.Trim();
        }
    }
}
