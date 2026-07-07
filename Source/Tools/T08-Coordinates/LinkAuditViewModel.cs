using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Coordinates
{
    /// <summary>
    /// Link Audit — a read-only health report on every loaded Revit link: positioning mode,
    /// display mode, pinned state, workset, load status, attachment type, and last-saved time.
    /// Makes no model changes. The "day one" tool for a new coordination project — check the
    /// links are set up right before aligning, copying from them, or filtering by them.
    /// </summary>
    public class LinkAuditViewModel : ILemoineTool, ILemoineReviewable, ILemoineToolCleanup
    {
        public string Title    => LemoineStrings.T("setup.linkAudit.title");
        public string RunLabel => LemoineStrings.T("setup.linkAudit.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("report", LemoineStrings.T("setup.linkAudit.steps.report"), required: false),
            new StepDefinition("run",    LemoineStrings.T("setup.linkAudit.steps.run"),    required: false),
        };

        private readonly LinkAuditRunHandler? _runHandler;
        private readonly ExternalEvent?       _runEvent;
        private readonly List<LinkAuditRow>   _rows;

        public event EventHandler? ValidationChanged;

        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog    = null;
            _runHandler.OnProgress = null;
            _runHandler.OnComplete = null;
        }

        public LinkAuditViewModel(LinkAuditRunHandler? runHandler, ExternalEvent? runEvent, LinkAuditData? data)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _rows       = data?.Rows ?? new List<LinkAuditRow>();
        }

        public FrameworkElement? GetStepContent(string stepId)
            => stepId == "report" ? BuildReportStep() : null;   // "run" rendered by ILemoineReviewable

        private FrameworkElement BuildReportStep()
        {
            var outer = new StackPanel();
            if (_rows.Count == 0)
            {
                outer.Children.Add(Dim(LemoineStrings.T("setup.linkAudit.labels.noneFound")));
                return outer;
            }

            int flagged = _rows.Count(r => r.IsWarning);
            outer.Children.Add(Label(LemoineStrings.T("setup.linkAudit.labels.summary", _rows.Count, flagged)));

            foreach (var row in _rows)
            {
                var content = new StackPanel();
                content.Children.Add(FieldRow(LemoineStrings.T("setup.linkAudit.labels.fieldStatus"), row.LoadStatus,
                    !row.LoadStatus.Equals("Loaded", StringComparison.OrdinalIgnoreCase)));
                content.Children.Add(FieldRow(LemoineStrings.T("setup.linkAudit.labels.fieldPositioning"), row.Positioning, false));
                content.Children.Add(FieldRow(LemoineStrings.T("setup.linkAudit.labels.fieldDisplay"), row.DisplayMode,
                    !(row.DisplayMode.Equals("By Host View", StringComparison.OrdinalIgnoreCase)
                      || row.DisplayMode.Equals("n/a", StringComparison.OrdinalIgnoreCase))));
                content.Children.Add(FieldRow(LemoineStrings.T("setup.linkAudit.labels.fieldPinned"),
                    row.Pinned ? LemoineStrings.T("setup.linkAudit.labels.yes") : LemoineStrings.T("setup.linkAudit.labels.no"), false));
                content.Children.Add(FieldRow(LemoineStrings.T("setup.linkAudit.labels.fieldWorkset"), row.WorksetName, false));
                content.Children.Add(FieldRow(LemoineStrings.T("setup.linkAudit.labels.fieldAttachment"), row.AttachmentType, false));
                content.Children.Add(FieldRow(LemoineStrings.T("setup.linkAudit.labels.fieldLastSaved"), row.LastSaved, false));

                var card = new LemoineSectionCard { Header = row.Name, CardContent = content, Margin = new Thickness(0, 0, 0, 10) };
                outer.Children.Add(card);
            }

            return outer;
        }

        // ── Review ──────────────────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("links", LemoineStrings.T("setup.linkAudit.review.itemLinks")),
            ("flagged", LemoineStrings.T("setup.linkAudit.review.itemFlagged")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["links"]   = _rows.Count.ToString(),
            ["flagged"] = _rows.Count(r => r.IsWarning).ToString(),
        };

        public IList<string>? ReviewChips => null;
        public string?        ReviewNote  => LemoineStrings.T("setup.linkAudit.review.note");
        public string?        ReviewWarning
        {
            get
            {
                int flagged = _rows.Count(r => r.IsWarning);
                return flagged > 0 ? LemoineStrings.T("setup.linkAudit.review.warning", flagged) : null;
            }
        }

        public bool IsValid(string stepId) => true;   // purely informational — nothing to validate

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "report":
                    return _rows.Count == 0 ? "—" : LemoineStrings.T("setup.linkAudit.summaries.report", _rows.Count, _rows.Count(r => r.IsWarning));
                case "run": return LemoineStrings.T("setup.linkAudit.summaries.run");
                default:    return "—";
            }
        }

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog(LemoineStrings.T("setup.linkAudit.log.handlerMissing"), "fail"); onComplete(0, 1, 0); return; }

            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

            pushLog(LemoineStrings.T("setup.linkAudit.log.raising"), "info");
            _runEvent.Raise();
        }

        // ── helpers ──────────────────────────────────────────────────────────────
        private static TextBlock Label(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock Dim(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static FrameworkElement FieldRow(string label, string value, bool warn)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            DockPanel.SetDock(lbl, Dock.Left);

            var val = new TextBlock
            {
                Text = value, HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.Wrap,
            };
            val.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            val.SetResourceReference(TextBlock.ForegroundProperty, warn ? "LemoineRed" : "LemoineText");
            val.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            dp.Children.Add(lbl);
            dp.Children.Add(val);
            return dp;
        }
    }
}
