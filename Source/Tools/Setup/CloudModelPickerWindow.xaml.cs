using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Picks one Autodesk Docs model to replace a cloud link with.
    ///
    /// <para><b>There is no ACC folder browser here, and cannot be.</b> Revit's
    /// <c>Autodesk.Revit.DB.ForgeDM</c> hub/project/folder/model types are <c>internal</c> in
    /// Revit 2024, so a plugin cannot enumerate Autodesk Docs. What this offers instead, all
    /// public API: the cloud models Revit already has open, the cloud links already loaded in
    /// this model, and direct entry of a model's region + project/model GUIDs.</para>
    ///
    /// <para>Opened modally from the Replace Link window's own STA thread. The scan is a Revit
    /// API call, so it goes out through <see cref="CloudBrowseHandler"/> on an
    /// <see cref="ExternalEvent"/> and comes back on Revit's main thread; the callback marshals
    /// onto this window's dispatcher with a non-blocking <c>BeginInvoke</c>.</para>
    ///
    /// <para><b>Not a StepFlowWindow, so no dispatcher safety net.</b> Every callback below fires
    /// without user action, and an unhandled throw on this thread would terminate Revit with no
    /// diagnostics entry — so each callback body is guarded (CLAUDE.md).</para>
    /// </summary>
    public partial class CloudModelPickerWindow : Window
    {
        private readonly CloudBrowseHandler? _handler;
        private readonly ExternalEvent?      _event;

        private readonly BrowserTreePicker _tree = new BrowserTreePicker { SingleSelect = true };
        private readonly SingleSelect      _regionPick  = new SingleSelect();
        private readonly TextField         _projectGuid = new TextField();
        private readonly TextField         _modelGuid   = new TextField();

        private Dictionary<long, CloudModelItem> _models = new Dictionary<long, CloudModelItem>();
        private Button? _selectBtn;

        // Region tokens come from Revit's own public constants; the display list mirrors them.
        private readonly List<string> _regions = new List<string>();

        /// <summary>The chosen model, or null when the user cancelled.</summary>
        public CloudModelItem? Result { get; private set; }

        public CloudModelPickerWindow(CloudBrowseHandler? handler, ExternalEvent? ev,
                                      string hostRegion, long excludeTypeId)
        {
            InitializeComponent();
            _handler = handler;
            _event   = ev;
            HostRegion     = hostRegion ?? "";
            _excludeTypeId = excludeTypeId;

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        /// <summary>The host model's own cloud region, used to preselect the region field.</summary>
        private string HostRegion { get; }

        /// <summary>The link being replaced — never offered as its own replacement.</summary>
        private readonly long _excludeTypeId;

        // ══════════════════════════════════════════════════════════════════════
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Inherit the owner's theme resources so dynamic references resolve.
                if (Owner != null)
                    foreach (var key in Owner.Resources.Keys)
                        Resources[key] = Owner.Resources[key];

                ApplyChrome();
                BuildBody();
                BuildManualEntry();
                BuildFooter();
                StartScan();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CloudModelPicker: build window", ex);
                SetStatus(AppStrings.T("cloudPicker.error.fetch", ex.Message), error: true);
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            // The handler is a process-wide static; leaving these attached would root this
            // closed window (and its tree) for the rest of the Revit session.
            if (_handler != null)
            {
                _handler.OnScanned = null;
                _handler.OnError   = null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        private void ApplyChrome()
        {
            this.SetResourceReference(Window.BackgroundProperty, "LemoinePageBg");
            _root.SetResourceReference(Grid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var closeX = ControlStyles.BuildButton("✕", ControlStyles.ButtonVariant.Ghost);
            closeX.Click += (s, e) => Cancel();
            closeX.ToolTip = AppStrings.T("cloudPicker.cancel");

            _toolbarBorder.BorderThickness = new Thickness(0);
            _toolbarBorder.Child = new TitleBar
            {
                Title = AppStrings.T("cloudPicker.title"),
                // TitleBar renders IconGlyph in Segoe MDL2 Assets, so it must be an MDL2
                // codepoint — written as ASCII per CLAUDE.md rather than a literal glyph.
                IconGlyph    = char.ConvertFromUtf32(0xE753),   // Cloud
                RightContent = closeX,
            };
        }

        private void BuildBody()
        {
            _bodyBorder.SetResourceReference(Border.BackgroundProperty, "LemoineBg");

            Dim(_scopeHint);
            _scopeHint.Text = AppStrings.T("cloudPicker.labels.scopeHint");

            Dim(_treeLabel);
            _treeLabel.Text = AppStrings.T("cloudPicker.labels.treeLabel");

            // Fires once at the end of SetTree — subscribe before the first SetTree call.
            _tree.SelectionChanged += OnTreeSelectionChanged;
            _tree.AccessibleName    = AppStrings.T("cloudPicker.labels.treeLabel");
            _treeSlot.Content       = _tree;

            Dim(_statusLine);
        }

        private void BuildManualEntry()
        {
            _manualBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var head = new TextBlock { Text = AppStrings.T("cloudPicker.labels.manualHead"),
                                       TextWrapping = TextWrapping.Wrap };
            Dim(head);
            _manualPanel.Children.Add(head);

            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,   GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8,   GridUnitType.Pixel) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8,   GridUnitType.Pixel) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });

            _regionPick.Label = AppStrings.T("cloudPicker.labels.region");
            _regionPick.SelectionChanged += _ => UpdateSelectEnabled();
            _regions.Clear();
            _regions.Add(AppStrings.T("cloudPicker.labels.regionUS"));
            _regions.Add(AppStrings.T("cloudPicker.labels.regionEMEA"));
            _regionPick.Items = _regions;
            // Default to the host's own region when it is one of the two Revit exposes.
            if (string.Equals(HostRegion, Autodesk.Revit.DB.ModelPathUtils.CloudRegionEMEA, StringComparison.OrdinalIgnoreCase))
                _regionPick.SelectedItem = _regions[1];

            _projectGuid.Label       = AppStrings.T("cloudPicker.labels.projectGuid");
            _projectGuid.Placeholder = AppStrings.T("cloudPicker.labels.guidPlaceholder");
            _projectGuid.TextChanged += _ => UpdateSelectEnabled();

            _modelGuid.Label       = AppStrings.T("cloudPicker.labels.modelGuid");
            _modelGuid.Placeholder = AppStrings.T("cloudPicker.labels.guidPlaceholder");
            _modelGuid.TextChanged += _ => UpdateSelectEnabled();

            Grid.SetColumn(_regionPick,  0);
            Grid.SetColumn(_projectGuid, 2);
            Grid.SetColumn(_modelGuid,   4);
            row.Children.Add(_regionPick);
            row.Children.Add(_projectGuid);
            row.Children.Add(_modelGuid);
            _manualPanel.Children.Add(row);
        }

        private void BuildFooter()
        {
            _footerBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            _footerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var cancel = ControlStyles.BuildButton(AppStrings.T("cloudPicker.cancel"), ControlStyles.ButtonVariant.Ghost);
            cancel.Click += (s, e) => Cancel();
            _cancelSlot.Content = cancel;

            _selectBtn = ControlStyles.BuildButton(AppStrings.T("cloudPicker.select"), ControlStyles.ButtonVariant.Primary);
            _selectBtn.IsEnabled = false;
            _selectBtn.Click += (s, e) => Accept();
            _selectSlot.Content = _selectBtn;
        }

        // ══════════════════════════════════════════════════════════════════════
        private void StartScan()
        {
            if (_handler == null || _event == null)
            {
                SetStatus(AppStrings.T("cloudPicker.error.handlerMissing"), error: true);
                return;
            }

            _handler.OnScanned     = result => Marshal(() => ApplyScan(result));
            _handler.OnError       = msg    => Marshal(() => SetStatus(msg, error: true));
            _handler.ExcludeTypeId = _excludeTypeId;

            SetStatus(AppStrings.T("cloudPicker.status.scanning"), error: false);
            _event.Raise();
        }

        /// <summary>Hops a handler callback (Revit's main thread) onto this window's dispatcher.
        /// Non-blocking, and guarded against a dispatcher that is already shutting down — a
        /// blocking Invoke into a closing STA thread deadlocks Revit (CLAUDE.md).</summary>
        private void Marshal(Action action)
        {
            try
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { action(); }
                    catch (Exception ex)
                    {
                        // No dispatcher safety net on this window — an escape here is a hard crash.
                        DiagnosticsLog.Error("CloudModelPicker: apply scan result", ex);
                        SetStatus(AppStrings.T("cloudPicker.error.fetch", ex.Message), error: true);
                    }
                }));
            }
            catch (Exception ex)
            { DiagnosticsLog.Swallowed("CloudModelPicker: marshal callback", ex); }
        }

        private void ApplyScan(CloudScanResult result)
        {
            if (result == null)
            {
                SetStatus(AppStrings.T("cloudPicker.status.none"), error: true);
                return;
            }

            _models = result.Models ?? new Dictionary<long, CloudModelItem>();
            _tree.SetTree(result.Tree);
            UpdateSelectEnabled();

            // A zero result is stated outright — an empty tree with no message is
            // indistinguishable from a broken collector (CLAUDE.md).
            SetStatus(result.Total == 0
                    ? AppStrings.T("cloudPicker.status.none")
                    : AppStrings.T("cloudPicker.status.found", result.OpenCount, result.LinkCount),
                error: result.Total == 0);
        }

        // ══════════════════════════════════════════════════════════════════════
        private void OnTreeSelectionChanged(IReadOnlyCollection<long> ids)
        {
            try
            {
                // Picking from the list and typing GUIDs are two ways to say the same thing —
                // a list pick clears the typed pair so the choice is never ambiguous.
                if (ids != null && ids.Count > 0)
                {
                    _projectGuid.Text = "";
                    _modelGuid.Text   = "";
                }
                UpdateSelectEnabled();
            }
            catch (Exception ex)
            { DiagnosticsLog.Swallowed("CloudModelPicker: tree selection changed", ex); }
        }

        /// <summary>The list pick, when one is active.</summary>
        private CloudModelItem? SelectedFromTree()
        {
            var id = _tree.SelectedIds.FirstOrDefault();
            return id != 0 && _models.TryGetValue(id, out var m) ? m : null;
        }

        /// <summary>The typed GUID pair, when both parse.</summary>
        private CloudModelItem? SelectedFromGuids()
        {
            if (!Guid.TryParse((_projectGuid.Text ?? "").Trim(), out var proj)) return null;
            if (!Guid.TryParse((_modelGuid.Text   ?? "").Trim(), out var model)) return null;
            if (proj == Guid.Empty || model == Guid.Empty) return null;

            bool emea = string.Equals(_regionPick.SelectedItem, _regions.Count > 1 ? _regions[1] : "",
                                      StringComparison.Ordinal);
            return new CloudModelItem
            {
                Source      = CloudModelSource.Manual,
                Name        = model.ToString(),
                Region      = emea ? Autodesk.Revit.DB.ModelPathUtils.CloudRegionEMEA
                                   : Autodesk.Revit.DB.ModelPathUtils.CloudRegionUS,
                ProjectGuid = proj,
                ModelGuid   = model,
                Detail      = AppStrings.T("cloudPicker.detail.manual"),
            };
        }

        private CloudModelItem? Selected() => SelectedFromTree() ?? SelectedFromGuids();

        private void UpdateSelectEnabled()
        {
            if (_selectBtn != null) _selectBtn.IsEnabled = Selected() != null;
        }

        // ══════════════════════════════════════════════════════════════════════
        private void Accept()
        {
            try
            {
                var pick = Selected();
                if (pick == null) return;
                Result = pick;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CloudModelPicker: accept", ex);
                SetStatus(AppStrings.T("cloudPicker.error.fetch", ex.Message), error: true);
            }
        }

        private void Cancel()
        {
            try { Result = null; DialogResult = false; Close(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CloudModelPicker: cancel", ex); }
        }

        private void SetStatus(string text, bool error)
        {
            _statusLine.Text = text ?? "";
            _statusLine.Visibility = string.IsNullOrEmpty(text) ? WpfVisibility.Collapsed : WpfVisibility.Visible;
            _statusLine.SetResourceReference(TextBlock.ForegroundProperty, error ? "LemoineRed" : "LemoineTextDim");
        }

        private void Dim(TextBlock tb)
        {
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
        }

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Show modally and return the chosen model, or null on cancel.</summary>
        public static CloudModelItem? Pick(Window? owner, CloudBrowseHandler? handler,
                                           ExternalEvent? ev, string hostRegion, long excludeTypeId)
        {
            var w = new CloudModelPickerWindow(handler, ev, hostRegion, excludeTypeId) { Owner = owner };
            return w.ShowDialog() == true ? w.Result : null;
        }
    }
}
