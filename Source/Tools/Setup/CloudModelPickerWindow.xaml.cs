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
    /// Picks one Autodesk Docs model — hub → project → folder tree → model.
    ///
    /// <para>Opened modally from the Replace Link window's own STA thread. Every ACC read is a
    /// Revit API call, so it goes out through <see cref="CloudBrowseHandler"/> on an
    /// <see cref="ExternalEvent"/> and comes back on Revit's main thread; each callback marshals
    /// onto this window's dispatcher with a non-blocking <c>BeginInvoke</c>.</para>
    ///
    /// <para><b>This is not a StepFlowWindow, so it has no dispatcher safety net.</b> Every
    /// callback below fires without user action, and an unhandled throw on this thread would
    /// terminate Revit with no diagnostics entry — so each callback body is guarded (CLAUDE.md).</para>
    /// </summary>
    public partial class CloudModelPickerWindow : Window
    {
        private readonly CloudBrowseHandler? _handler;
        private readonly ExternalEvent?      _event;

        private readonly SingleSelect       _hubPick     = new SingleSelect();
        private readonly SingleSelect       _projectPick = new SingleSelect();
        private readonly BrowserTreePicker  _tree        = new BrowserTreePicker { SingleSelect = true };

        private List<CloudHubItem>     _hubs     = new List<CloudHubItem>();
        private List<CloudProjectItem> _projects = new List<CloudProjectItem>();
        private Dictionary<long, CloudModelItem> _models = new Dictionary<long, CloudModelItem>();

        private string _hubId = "", _projectId = "";

        // SingleSelect.Items AUTO-SELECTS index 0 as a side effect of its setter and raises
        // SelectionChanged — without this guard, populating a combo re-enters the fetch it is
        // being populated by.
        private bool _building;

        private Button? _selectBtn;

        /// <summary>The chosen model, or null when the user cancelled.</summary>
        public CloudModelItem? Result { get; private set; }

        public CloudModelPickerWindow(CloudBrowseHandler? handler, ExternalEvent? ev,
                                      Guid hostProjectGuid, string hostRegion)
        {
            InitializeComponent();
            _handler = handler;
            _event   = ev;

            if (_handler != null)
            {
                _handler.HostProjectGuid = hostProjectGuid;
                _handler.HostRegion      = hostRegion ?? "";
            }

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

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
                BuildFooter();
                StartFetch();
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
                _handler.OnHubs     = null;
                _handler.OnProjects = null;
                _handler.OnTree     = null;
                _handler.OnError    = null;
                _handler.HostProjectGuid = Guid.Empty;
                _handler.HostRegion      = "";
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
                Title        = AppStrings.T("cloudPicker.title"),
                // TitleBar renders IconGlyph in Segoe MDL2 Assets, so it must be an MDL2
                // codepoint — written as ASCII per CLAUDE.md rather than a literal glyph.
                IconGlyph    = char.ConvertFromUtf32(0xE753),   // Cloud
                RightContent = closeX,
            };
        }

        private void BuildBody()
        {
            _bodyBorder.SetResourceReference(Border.BackgroundProperty, "LemoineBg");

            _hubPick.Label     = AppStrings.T("cloudPicker.labels.hub");
            _projectPick.Label = AppStrings.T("cloudPicker.labels.project");

            // Subscribed BEFORE Items is ever set — that setter is the only signal these fire on.
            _hubPick.SelectionChanged     += OnHubChanged;
            _projectPick.SelectionChanged += OnProjectChanged;

            Grid.SetColumn(_hubPick, 0);
            Grid.SetColumn(_projectPick, 2);
            _scopeRow.Children.Add(_hubPick);
            _scopeRow.Children.Add(_projectPick);

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
        private void StartFetch()
        {
            if (_handler == null || _event == null)
            {
                SetStatus(AppStrings.T("cloudPicker.error.handlerMissing"), error: true);
                return;
            }

            _handler.OnHubs     = (hubs, projects) => Marshal(() => ApplyHubs(hubs, projects));
            _handler.OnProjects = projects         => Marshal(() => ApplyProjects(projects, refetchTree: true));
            _handler.OnTree     = result           => Marshal(() => ApplyTree(result));
            _handler.OnError    = msg              => Marshal(() => SetStatus(msg, error: true));

            SetStatus(AppStrings.T("cloudPicker.status.loadingHubs"), error: false);
            _handler.Request = CloudBrowseRequest.Hubs;
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
                        DiagnosticsLog.Error("CloudModelPicker: apply fetch result", ex);
                        SetStatus(AppStrings.T("cloudPicker.error.fetch", ex.Message), error: true);
                    }
                }));
            }
            catch (Exception ex)
            { DiagnosticsLog.Swallowed("CloudModelPicker: marshal callback", ex); }
        }

        // ══════════════════════════════════════════════════════════════════════
        private void ApplyHubs(List<CloudHubItem> hubs, List<CloudProjectItem> projects)
        {
            _hubs = hubs ?? new List<CloudHubItem>();
            if (_hubs.Count == 0)
            {
                SetStatus(AppStrings.T("cloudPicker.error.noHubs"), error: true);
                return;
            }

            // The default hub is whichever one the returned projects belong to (the handler
            // resolved the host model's own hub); fall back to the first listed.
            string defaultHubId = projects != null && projects.Count > 0 ? projects[0].HubId : _hubs[0].Id;
            var    defaultHub   = _hubs.FirstOrDefault(h => h.Id == defaultHubId) ?? _hubs[0];
            _hubId = defaultHub.Id;

            _building = true;
            try
            {
                _hubPick.Items        = _hubs.Select(h => h.Name).ToList();
                _hubPick.SelectedItem = defaultHub.Name;
            }
            finally { _building = false; }

            ApplyProjects(projects ?? new List<CloudProjectItem>(), refetchTree: true);
        }

        private void ApplyProjects(List<CloudProjectItem> projects, bool refetchTree)
        {
            _projects = projects ?? new List<CloudProjectItem>();
            if (_projects.Count == 0)
            {
                ClearTree();
                SetStatus(AppStrings.T("cloudPicker.error.noProjects"), error: true);
                return;
            }

            // Default to the host model's own project when it is in this hub.
            var hostGuid = _handler?.HostProjectGuid ?? Guid.Empty;
            var chosen   = _projects.FirstOrDefault(p => hostGuid != Guid.Empty && p.Guid == hostGuid)
                           ?? _projects[0];
            _projectId = chosen.Id;

            _building = true;
            try
            {
                _projectPick.Items        = _projects.Select(p => p.Name).ToList();
                _projectPick.SelectedItem = chosen.Name;
            }
            finally { _building = false; }

            if (refetchTree) FetchTree();
        }

        private void ApplyTree(CloudTreeResult result)
        {
            if (result == null)
            {
                ClearTree();
                SetStatus(AppStrings.T("cloudPicker.error.noModels"), error: true);
                return;
            }

            _models = result.Models ?? new Dictionary<long, CloudModelItem>();
            _tree.SetTree(result.Tree);
            UpdateSelectEnabled();

            // A zero result is stated outright — an empty tree with no message is
            // indistinguishable from a broken collector (CLAUDE.md).
            if (result.ModelCount == 0)
            {
                SetStatus(AppStrings.T("cloudPicker.status.noModels"), error: true);
                return;
            }

            string found = AppStrings.T("cloudPicker.status.found", result.FolderCount, result.ModelCount);
            if (result.Truncated)
                found += " " + AppStrings.T("cloudPicker.status.truncated");
            SetStatus(found, error: result.Truncated);
        }

        // ══════════════════════════════════════════════════════════════════════
        private void OnHubChanged(string? name)
        {
            if (_building) return;
            try
            {
                var hit = _hubs.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.Ordinal));
                if (hit == null || hit.Id == _hubId) return;
                _hubId = hit.Id;

                ClearTree();
                if (_handler == null || _event == null) return;
                SetStatus(AppStrings.T("cloudPicker.status.loadingProjects"), error: false);
                _handler.Request = CloudBrowseRequest.Projects;
                _handler.HubId   = _hubId;
                _event.Raise();
            }
            catch (Exception ex)
            { DiagnosticsLog.Swallowed("CloudModelPicker: hub changed", ex); }
        }

        private void OnProjectChanged(string? name)
        {
            if (_building) return;
            try
            {
                var hit = _projects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
                if (hit == null || hit.Id == _projectId) return;
                _projectId = hit.Id;
                FetchTree();
            }
            catch (Exception ex)
            { DiagnosticsLog.Swallowed("CloudModelPicker: project changed", ex); }
        }

        private void OnTreeSelectionChanged(IReadOnlyCollection<long> ids)
        {
            try { UpdateSelectEnabled(); }
            catch (Exception ex)
            { DiagnosticsLog.Swallowed("CloudModelPicker: tree selection changed", ex); }
        }

        private void FetchTree()
        {
            if (_handler == null || _event == null) return;
            ClearTree();
            SetStatus(AppStrings.T("cloudPicker.status.loadingTree"), error: false);
            _handler.Request   = CloudBrowseRequest.Tree;
            _handler.HubId     = _hubId;
            _handler.ProjectId = _projectId;
            _event.Raise();
        }

        private void ClearTree()
        {
            _models = new Dictionary<long, CloudModelItem>();
            _tree.SetTree(null);
            UpdateSelectEnabled();
        }

        private CloudModelItem? Selected()
        {
            var id = _tree.SelectedIds.FirstOrDefault();
            return id != 0 && _models.TryGetValue(id, out var m) ? m : null;
        }

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
        public static CloudModelItem? Pick(Window? owner, CloudBrowseHandler? handler, ExternalEvent? ev,
                                           Guid hostProjectGuid, string hostRegion)
        {
            var w = new CloudModelPickerWindow(handler, ev, hostProjectGuid, hostRegion) { Owner = owner };
            return w.ShowDialog() == true ? w.Result : null;
        }
    }
}
