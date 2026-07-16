using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.Tools.Setup;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// HTML analogue of <see cref="LinkAuditWindow"/> — a read-only link report. One card per link
    /// with status / positioning / display-mode / pinned / workset / attachment / last-saved rows,
    /// flagged fields shown in red (same warning logic as the WPF window). Data is the snapshot
    /// captured on the Revit main thread by <c>LinkAuditCommand</c>; this window never touches Revit.
    /// </summary>
    public sealed class WebLinkAuditWindow : WebWindowBase
    {
        private readonly LinkAuditData _data;

        public WebLinkAuditWindow(LinkAuditData? data)
            : base(AppStrings.T("setup.linkAudit.window.title"), 560, 640)
        {
            _data = data ?? new LinkAuditData();
        }

        protected override string PageFileName => "linkaudit.html";

        protected override Dictionary<string, object?> BuildInitPayload()
        {
            int flagged = _data.Rows.Count(r => r.IsWarning);
            var rows = _data.Rows.Select(r => new Dictionary<string, object?>
            {
                ["name"]   = r.Name,
                ["warn"]   = r.IsWarning,
                ["fields"] = new List<Dictionary<string, object?>>
                {
                    Field(AppStrings.T("setup.linkAudit.labels.fieldStatus"), r.LoadStatus,
                        !r.LoadStatus.Equals("Loaded", StringComparison.OrdinalIgnoreCase)),
                    Field(AppStrings.T("setup.linkAudit.labels.fieldPositioning"), r.Positioning, false),
                    Field(AppStrings.T("setup.linkAudit.labels.fieldDisplay"), r.DisplayMode,
                        !(r.DisplayMode.Equals("By Host View", StringComparison.OrdinalIgnoreCase)
                          || r.DisplayMode.Equals("n/a", StringComparison.OrdinalIgnoreCase))),
                    Field(AppStrings.T("setup.linkAudit.labels.fieldPinned"),
                        r.Pinned ? AppStrings.T("setup.linkAudit.labels.yes") : AppStrings.T("setup.linkAudit.labels.no"), false),
                    Field(AppStrings.T("setup.linkAudit.labels.fieldWorkset"), r.WorksetName, false),
                    Field(AppStrings.T("setup.linkAudit.labels.fieldAttachment"), r.AttachmentType, false),
                    Field(AppStrings.T("setup.linkAudit.labels.fieldLastSaved"), r.LastSaved, false),
                },
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["title"]      = AppStrings.T("setup.linkAudit.window.title"),
                ["close"]      = AppStrings.T("setup.linkAudit.window.close"),
                ["footerHint"] = AppStrings.T("setup.linkAudit.window.footerHint"),
                ["summary"]    = _data.Rows.Count == 0
                    ? null : AppStrings.T("setup.linkAudit.labels.summary", _data.Rows.Count, flagged),
                ["empty"]      = _data.Rows.Count == 0 ? AppStrings.T("setup.linkAudit.labels.noneFound") : null,
                ["rows"]       = rows,
            };
        }

        private static Dictionary<string, object?> Field(string label, string value, bool warn) =>
            new Dictionary<string, object?> { ["label"] = label, ["value"] = value ?? "", ["warn"] = warn };
    }
}
