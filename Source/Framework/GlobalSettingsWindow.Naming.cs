using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LemoineTools.Framework.Controls;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Framework
{
    // ═════════════════════════════════════════════════════════════════════
    // Naming tab — define, edit, and delete user naming tokens (bound to
    // Revit parameters) that appear in every tool's TokenInput chip picker.
    // Everything here reads from the ParameterCatalogSnapshot captured once
    // by OpenSettingsCommand on the Revit main thread — this window never
    // touches Revit itself.
    // ═════════════════════════════════════════════════════════════════════
    public partial class GlobalSettingsWindow
    {
        // ── Editor state ─────────────────────────────────────────────────────
        private bool    _namingBuiltInExpanded;
        private string? _namingEditOriginalKey;      // null = creating a new token
        private string  _namingEditLabel        = "";
        private string  _namingEditKeyText      = ""; // without the "u:" prefix
        private bool    _namingEditKeyManual;          // true once the user edits the key directly
        private string  _namingEditEntity       = nameof(TokenEntity.Sheet);
        private string  _namingEditSubject      = nameof(TokenSubject.Target);
        private string  _namingEditParamName    = "";
        private string  _namingEditParamGuid    = "";
        private string  _namingEditFallback     = "";
        private string  _namingEditDescription  = "";
        private string  _namingParamSearch      = "";
        private string? _namingError;
        private readonly HashSet<string> _namingPendingDelete =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private StackPanel? _namingListHost;
        private Border?     _namingEditorCard;

        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildNamingContent()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            var panel = new StackPanel { Margin = new Thickness(4, 4, 4, 16) };

            BuildNamingIntro(panel);
            BuildNamingBuiltInAccordion(panel);

            var yourHeader = NamingSectionLabel(AppStrings.T("naming.settings.yourTokensHeader"));
            yourHeader.Margin = new Thickness(0, 4, 0, 10);
            panel.Children.Add(yourHeader);

            var cols = new Grid();
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _namingListHost = new StackPanel();
            Grid.SetColumn(_namingListHost, 0);
            cols.Children.Add(_namingListHost);

            _namingEditorCard = new Border
            {
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(16),
                VerticalAlignment = VerticalAlignment.Top,
            };
            _namingEditorCard.SetResourceReference(Border.BackgroundProperty,     "LemoineSurface");
            _namingEditorCard.SetResourceReference(Border.BorderBrushProperty,    "LemoineBorder");
            _namingEditorCard.SetResourceReference(Border.CornerRadiusProperty,   "LemoineRadius_Card");
            Grid.SetColumn(_namingEditorCard, 2);
            cols.Children.Add(_namingEditorCard);

            panel.Children.Add(cols);

            RefreshNamingTokenList();
            ResetNamingEditorForNew();

            scroll.Content = panel;
            return scroll;
        }

        // ── Intro ────────────────────────────────────────────────────────────
        private void BuildNamingIntro(StackPanel panel)
        {
            var intro = new TextBlock
            {
                Text         = AppStrings.T("naming.settings.intro"),
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 0, 0, 4),
            };
            intro.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            intro.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            intro.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            panel.Children.Add(intro);

            bool hasDoc = !string.IsNullOrEmpty(_namingSnapshot.DocTitle);
            var src = new TextBlock
            {
                Text         = hasDoc
                    ? AppStrings.T("naming.settings.introSource", _namingSnapshot.DocTitle)
                    : AppStrings.T("naming.settings.introNoDoc"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 16),
            };
            src.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            src.SetResourceReference(TextBlock.ForegroundProperty, hasDoc ? "LemoineTextSub" : "LemoineRed");
            src.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            panel.Children.Add(src);
        }

        // ── Built-in reference (collapsed by default) ───────────────────────
        private void BuildNamingBuiltInAccordion(StackPanel panel)
        {
            var builtIns = NamingTokenRegistry.BuiltIns;

            var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 16) };
            toggleRow.Background = System.Windows.Media.Brushes.Transparent; // hit-testable across the whole row, not just the glyphs

            var caret = new TextBlock { Text = _namingBuiltInExpanded ? "▾" : "▸", Margin = new Thickness(0, 0, 6, 0) };
            caret.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            caret.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            caret.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            toggleRow.Children.Add(caret);

            var label = new TextBlock { Text = AppStrings.T("naming.settings.builtInHeader", builtIns.Count) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            label.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            toggleRow.Children.Add(label);

            toggleRow.MouseLeftButtonUp += (s, e) =>
            {
                _namingBuiltInExpanded = !_namingBuiltInExpanded;
                RefreshNamingTab();
            };
            panel.Children.Add(toggleRow);

            if (!_namingBuiltInExpanded) return;

            var grid = new Grid { Margin = new Thickness(0, -8, 0, 16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < builtIns.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var def = builtIns[i];

                var key = new TextBlock { Text = def.Braced, Margin = new Thickness(0, 3, 8, 3) };
                key.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                key.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                key.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                Grid.SetRow(key, i); Grid.SetColumn(key, 0);
                grid.Children.Add(key);

                string entityWord = def.Entity == TokenEntity.Any
                    ? AppStrings.T("naming.settings.builtInEntityAny")
                    : (def.Entity == TokenEntity.Sheet
                        ? AppStrings.T("naming.settings.appliesToSheets")
                        : AppStrings.T("naming.settings.appliesToViews"));
                var desc = new TextBlock
                {
                    Text = $"{def.Label} — {def.Description}  ·  {entityWord}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 3),
                };
                desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                Grid.SetRow(desc, i); Grid.SetColumn(desc, 1);
                grid.Children.Add(desc);
            }

            panel.Children.Add(grid);
        }

        // Rebuilds the whole tab in place (simplest correct approach for a settings page —
        // matches BulkRenameViewModel.BuildModeInputs' swap-content pattern).
        private void RefreshNamingTab()
        {
            if (_activeTabId != "naming") return;
            _contentBorder.Child = BuildNamingContent();
        }

        // ── Your tokens list ────────────────────────────────────────────────
        private void RefreshNamingTokenList()
        {
            if (_namingListHost == null) return;
            _namingListHost.Children.Clear();

            var tokens = UserTokenStore.Instance.Raw
                .OrderBy(d => d.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (tokens.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = AppStrings.T("naming.settings.emptyState"),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 0, 0, 12),
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                _namingListHost.Children.Add(empty);
            }
            else
            {
                foreach (var dto in tokens)
                    _namingListHost.Children.Add(BuildNamingTokenRow(dto));
            }

            var addBtn = new Button
            {
                Content         = AppStrings.T("naming.settings.addNew"),
                Cursor          = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding         = new Thickness(0, 9, 0, 9),
                BorderThickness = new Thickness(1),
                Template        = ControlStyles.BuildFlatButtonTemplate(),
                Margin          = new Thickness(0, 4, 0, 0),
            };
            addBtn.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_SM");
            addBtn.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            addBtn.Background = System.Windows.Media.Brushes.Transparent;
            addBtn.SetResourceReference(Button.BackgroundProperty,  "LemoineAccentDim");
            addBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
            addBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineAccent");
            addBtn.Click += (s, e) => ResetNamingEditorForNew();
            _namingListHost.Children.Add(addBtn);
        }

        private Border BuildNamingTokenRow(UserTokenDto dto)
        {
            var row = new Border
            {
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(12, 10, 12, 10),
                Margin          = new Thickness(0, 0, 0, 8),
            };
            row.SetResourceReference(Border.BackgroundProperty,   "LemoineRaised");
            row.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
            row.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_MD");

            bool confirmingDelete = _namingPendingDelete.Contains(dto.Key);

            var outer = new DockPanel();

            if (confirmingDelete)
            {
                string? usedBy = NamingUsedByText(dto.Key);
                var confirmStack = new StackPanel();

                var msg = new TextBlock
                {
                    Text = AppStrings.T("naming.settings.rowDeleteConfirm") + " " + dto.Label,
                    TextWrapping = TextWrapping.Wrap,
                };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                confirmStack.Children.Add(msg);

                if (usedBy != null)
                {
                    var warn = new TextBlock { Text = usedBy, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontStyle = FontStyles.Italic };
                    warn.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
                    warn.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    warn.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    confirmStack.Children.Add(warn);
                }

                var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var yes = NamingRowButton(AppStrings.T("naming.settings.rowDeleteYes"), isDanger: true);
                yes.Click += (s, e) => { _namingPendingDelete.Remove(dto.Key); DeleteNamingToken(dto); };
                var no = NamingRowButton(AppStrings.T("naming.settings.rowDeleteNo"), isDanger: false);
                no.Click += (s, e) => { _namingPendingDelete.Remove(dto.Key); RefreshNamingTokenList(); };
                btnRow.Children.Add(yes);
                btnRow.Children.Add(no);
                confirmStack.Children.Add(btnRow);

                outer.Children.Add(confirmStack);
                row.Child = outer;
                return row;
            }

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var editBtn = NamingRowButton(AppStrings.T("naming.settings.rowEdit"), isDanger: false);
            editBtn.Click += (s, e) => LoadNamingEditorFor(dto);
            var delBtn = NamingRowButton(AppStrings.T("naming.settings.rowDelete"), isDanger: true);
            delBtn.Click += (s, e) => { _namingPendingDelete.Add(dto.Key); RefreshNamingTokenList(); };
            actions.Children.Add(editBtn);
            actions.Children.Add(delBtn);
            DockPanel.SetDock(actions, Dock.Right);
            outer.Children.Add(actions);

            var meta = new StackPanel();
            var key = new TextBlock { Text = "{u:" + dto.Key + "}" };
            key.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            key.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            key.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            meta.Children.Add(key);

            var lbl = new TextBlock { Text = dto.Label, Margin = new Thickness(0, 2, 0, 0) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            meta.Children.Add(lbl);

            string entityWord = dto.Entity == nameof(TokenEntity.Sheet)
                ? AppStrings.T("naming.settings.appliesToSheets") : AppStrings.T("naming.settings.appliesToViews");
            string paramWord  = !string.IsNullOrEmpty(dto.ParameterGuid)
                ? AppStrings.T("naming.settings.paramOrigin.shared") : AppStrings.T("naming.settings.paramOrigin.project");
            var sub = new TextBlock
            {
                Text = $"{entityWord} · {dto.ParameterName} ({paramWord})",
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            sub.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            sub.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            meta.Children.Add(sub);

            outer.Children.Add(meta);
            row.Child = outer;
            return row;
        }

        private Button NamingRowButton(string text, bool isDanger)
        {
            var btn = new Button
            {
                Content         = text,
                Cursor          = Cursors.Hand,
                Margin          = new Thickness(6, 0, 0, 0),
                Padding         = new Thickness(9, 4, 9, 4),
                BorderThickness = new Thickness(1),
                Template        = ControlStyles.BuildFlatButtonTemplate(),
            };
            btn.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_SM");
            btn.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            btn.Background = System.Windows.Media.Brushes.Transparent;
            btn.SetResourceReference(Button.BackgroundProperty,  "LemoineRaised");
            btn.SetResourceReference(Button.BorderBrushProperty, isDanger ? "LemoineRed" : "LemoineBorder");
            btn.SetResourceReference(Button.ForegroundProperty,  isDanger ? "LemoineRed" : "LemoineText");
            return btn;
        }

        // Scans every persisted pattern (per-tool + Bulk Export's own settings) for the
        // token's braced form and lists which tools would be left with a stale literal.
        private string? NamingUsedByText(string key)
        {
            string braced = "{u:" + key + "}";
            var users = new List<string>();

            foreach (var (toolId, pattern) in NamingPatternStore.Instance.AllPatterns())
                if (!string.IsNullOrEmpty(pattern) && pattern.Contains(braced))
                    users.Add(toolId);

            var exp = LemoineTools.Tools.BulkExport.BulkExportSettings.Instance;
            if (exp.FilenamePattern?.Contains(braced) == true)     users.Add("export.bulkExport (Sheets)");
            if (exp.ViewFilenamePattern?.Contains(braced) == true) users.Add("export.bulkExport (Views)");

            if (users.Count == 0) return null;
            return AppStrings.T("naming.settings.rowUsedBy", string.Join(", ", users));
        }

        private void DeleteNamingToken(UserTokenDto dto)
        {
            UserTokenStore.Instance.Delete(dto.Key);
            FlashNamingStatus(AppStrings.T("naming.settings.deleted", dto.Label));
            RefreshNamingTokenList();
            if (_namingEditOriginalKey == dto.Key) ResetNamingEditorForNew();
        }

        // ── Editor ───────────────────────────────────────────────────────────
        private void ResetNamingEditorForNew()
        {
            _namingEditOriginalKey = null;
            _namingEditLabel       = "";
            _namingEditKeyText     = "";
            _namingEditKeyManual   = false;
            _namingEditEntity      = nameof(TokenEntity.Sheet);
            _namingEditSubject     = nameof(TokenSubject.Target);
            _namingEditParamName   = "";
            _namingEditParamGuid   = "";
            _namingEditFallback    = "";
            _namingEditDescription = "";
            _namingParamSearch     = "";
            _namingError           = null;
            RenderNamingEditor();
        }

        private void LoadNamingEditorFor(UserTokenDto dto)
        {
            _namingEditOriginalKey = dto.Key;
            _namingEditLabel       = dto.Label;
            _namingEditKeyText     = dto.Key;
            _namingEditKeyManual   = true; // editing an existing token never re-derives its key
            _namingEditEntity      = dto.Entity;
            _namingEditSubject     = dto.Subject;
            _namingEditParamName   = dto.ParameterName;
            _namingEditParamGuid   = dto.ParameterGuid;
            _namingEditFallback    = dto.FallbackText;
            _namingEditDescription = dto.Description;
            _namingParamSearch     = "";
            _namingError           = null;
            RenderNamingEditor();
        }

        private List<ParameterCatalogEntry> CurrentNamingParameterCatalog()
        {
            if (_namingEditSubject == nameof(TokenSubject.ProjectInfo))
                return _namingSnapshot.ProjectInfoParameters;
            return _namingEditEntity == nameof(TokenEntity.Sheet)
                ? _namingSnapshot.SheetParameters
                : _namingSnapshot.ViewParameters;
        }

        private void RenderNamingEditor()
        {
            if (_namingEditorCard == null) return;

            var body = new StackPanel();

            var title = new TextBlock
            {
                Text   = _namingEditOriginalKey == null
                    ? AppStrings.T("naming.settings.editorTitleNew")
                    : AppStrings.T("naming.settings.editorTitleEdit", _namingEditLabel),
                Margin = new Thickness(0, 0, 0, 14),
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            title.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            title.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            body.Children.Add(title);

            // Name
            var nameField = new TextField
            {
                Label = AppStrings.T("naming.settings.fieldName"),
                Text  = _namingEditLabel,
                Margin = new Thickness(0, 0, 0, 14),
            };
            nameField.TextChanged += t =>
            {
                _namingEditLabel = t ?? "";
                if (!_namingEditKeyManual) _namingEditKeyText = DeriveNamingKey(_namingEditLabel);
                ValidateNamingEditor();
                RenderNamingEditor();
            };
            body.Children.Add(nameField);

            // Key (read-only unless "edit" clicked)
            body.Children.Add(BuildNamingKeyRow());

            // Applies to
            body.Children.Add(NamingFieldLabel(AppStrings.T("naming.settings.fieldAppliesTo")));
            var appliesTo = new SingleSelect
            {
                Width = 260, HorizontalAlignment = HorizontalAlignment.Left,
                Items = new List<string> { AppStrings.T("naming.settings.appliesToSheets"), AppStrings.T("naming.settings.appliesToViews") },
                SelectedItem = _namingEditEntity == nameof(TokenEntity.Sheet)
                    ? AppStrings.T("naming.settings.appliesToSheets") : AppStrings.T("naming.settings.appliesToViews"),
            };
            appliesTo.SelectionChanged += v =>
            {
                _namingEditEntity = v == AppStrings.T("naming.settings.appliesToViews") ? nameof(TokenEntity.View) : nameof(TokenEntity.Sheet);
                RenderNamingEditor();
            };
            var appliesToWrap = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            appliesToWrap.Children.Add(appliesTo);
            body.Children.Add(appliesToWrap);

            // Reads from
            body.Children.Add(NamingFieldLabel(AppStrings.T("naming.settings.fieldReadsFrom")));
            string targetLbl  = AppStrings.T("naming.settings.readsFromTarget");
            string sourceLbl  = AppStrings.T("naming.settings.readsFromSource");
            string projectLbl = AppStrings.T("naming.settings.readsFromProject");
            var readsFrom = new SingleSelect
            {
                Width = 260, HorizontalAlignment = HorizontalAlignment.Left,
                Items = new List<string> { targetLbl, sourceLbl, projectLbl },
                SelectedItem = _namingEditSubject switch
                {
                    nameof(TokenSubject.Source)      => sourceLbl,
                    nameof(TokenSubject.ProjectInfo)  => projectLbl,
                    _                                  => targetLbl,
                },
            };
            readsFrom.SelectionChanged += v =>
            {
                _namingEditSubject = v == sourceLbl ? nameof(TokenSubject.Source)
                                    : v == projectLbl ? nameof(TokenSubject.ProjectInfo)
                                    : nameof(TokenSubject.Target);
                RenderNamingEditor();
            };
            var readsFromWrap = new StackPanel();
            readsFromWrap.Children.Add(readsFrom);
            readsFromWrap.Children.Add(NamingCaption(AppStrings.T("naming.settings.readsFromCaption"), new Thickness(0, 6, 0, 14)));
            body.Children.Add(readsFromWrap);

            // Parameter
            body.Children.Add(BuildNamingParameterField());

            // Fallback text
            var fallback = new TextField
            {
                Label       = AppStrings.T("naming.settings.fieldFallback"),
                Placeholder = AppStrings.T("naming.settings.fallbackPlaceholder"),
                Text        = _namingEditFallback,
                Margin      = new Thickness(0, 14, 0, 0),
            };
            fallback.TextChanged += t => { _namingEditFallback = t ?? ""; RenderNamingLiveTestOnly(); };
            body.Children.Add(fallback);
            body.Children.Add(NamingCaption(AppStrings.T("naming.settings.fallbackCaption"), new Thickness(0, 4, 0, 14)));

            // Description
            var desc = new TextField
            {
                Label = AppStrings.T("naming.settings.fieldDescription"),
                Text  = _namingEditDescription,
                Margin = new Thickness(0, 0, 0, 14),
            };
            desc.TextChanged += t => _namingEditDescription = t ?? "";
            body.Children.Add(desc);

            // Live test
            body.Children.Add(NamingFieldLabel(AppStrings.T("naming.settings.fieldLiveTest")));
            body.Children.Add(BuildNamingLiveTestBox());

            // Error
            if (!string.IsNullOrEmpty(_namingError))
            {
                var err = new TextBlock { Text = _namingError, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
                err.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
                err.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                err.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                body.Children.Add(err);
            }

            // Save / cancel
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            var save = new Button
            {
                Content = _namingEditOriginalKey == null ? AppStrings.T("naming.settings.saveNew") : AppStrings.T("naming.settings.saveUpdate"),
                Cursor = Cursors.Hand,
                Padding = new Thickness(16, 8, 16, 8),
                BorderThickness = new Thickness(1),
                Template = ControlStyles.BuildFlatButtonTemplate(),
                IsEnabled = string.IsNullOrEmpty(_namingError) && !string.IsNullOrWhiteSpace(_namingEditLabel),
            };
            save.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_MD");
            save.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            save.Background = System.Windows.Media.Brushes.Transparent;
            save.SetResourceReference(Button.BackgroundProperty,  "LemoineAccentDim");
            save.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
            save.SetResourceReference(Button.ForegroundProperty,  "LemoineAccent");
            save.Click += (s, e) => SaveNamingToken();
            footer.Children.Add(save);

            if (_namingEditOriginalKey != null)
            {
                var cancel = NamingRowButton(AppStrings.T("naming.settings.cancelEdit"), isDanger: false);
                cancel.Click += (s, e) => ResetNamingEditorForNew();
                footer.Children.Add(cancel);
            }
            body.Children.Add(footer);

            _namingEditorCard.Child = body;
        }

        private UIElement BuildNamingKeyRow()
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var stack = new StackPanel();
            stack.Children.Add(NamingFieldLabel(AppStrings.T("naming.settings.fieldKey")));

            if (_namingEditKeyManual)
            {
                var keyField = new TextField { Text = "u:" + _namingEditKeyText };
                keyField.TextChanged += t =>
                {
                    string raw = (t ?? "").Trim();
                    _namingEditKeyText = raw.StartsWith("u:", StringComparison.OrdinalIgnoreCase) ? raw.Substring(2) : raw;
                    ValidateNamingEditor();
                };
                stack.Children.Add(keyField);
            }
            else
            {
                var keyText = new TextBlock { Text = "{u:" + _namingEditKeyText + "}" };
                keyText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                keyText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                keyText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                stack.Children.Add(keyText);
            }
            Grid.SetColumn(stack, 0);
            row.Children.Add(stack);

            if (!_namingEditKeyManual)
            {
                var editKeyBtn = NamingRowButton(AppStrings.T("naming.settings.fieldKeyEdit"), isDanger: false);
                editKeyBtn.VerticalAlignment = VerticalAlignment.Bottom;
                editKeyBtn.Click += (s, e) => { _namingEditKeyManual = true; RenderNamingEditor(); };
                Grid.SetColumn(editKeyBtn, 1);
                row.Children.Add(editKeyBtn);
            }

            return row;
        }

        private UIElement BuildNamingParameterField()
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
            stack.Children.Add(NamingFieldLabel(AppStrings.T("naming.settings.fieldParameter")));

            var catalog = CurrentNamingParameterCatalog();

            var search = new TextField { Placeholder = AppStrings.T("naming.settings.parameterSearchPlaceholder"), Text = _namingParamSearch };
            search.TextChanged += t => { _namingParamSearch = t ?? ""; RenderNamingEditor(); };
            stack.Children.Add(search);

            var filtered = string.IsNullOrWhiteSpace(_namingParamSearch)
                ? catalog
                : catalog.Where(c => c.Name.IndexOf(_namingParamSearch, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var listBorder = new Border
            {
                BorderThickness = new Thickness(1, 0, 1, 1),
                MaxHeight = 160,
                Margin = new Thickness(0, -1, 0, 0),
            };
            listBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var listPanel = new StackPanel();

            if (catalog.Count == 0)
            {
                var none = new TextBlock { Text = AppStrings.T("naming.settings.parameterNoneFound"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), FontStyle = FontStyles.Italic };
                none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                listPanel.Children.Add(none);
            }
            else
            {
                foreach (var entry in filtered)
                    listPanel.Children.Add(BuildNamingParameterRow(entry));
            }

            listScroll.Content = listPanel;
            listBorder.Child = listScroll;
            stack.Children.Add(listBorder);

            var manualRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            manualRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            manualRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            manualRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });

            var manualName = new TextField { Placeholder = AppStrings.T("naming.settings.manualNamePlaceholder"), Text = _namingEditParamName };
            manualName.TextChanged += t => { _namingEditParamName = t ?? ""; ValidateNamingEditor(); RenderNamingLiveTestOnly(); };
            Grid.SetColumn(manualName, 0);
            manualRow.Children.Add(manualName);

            var manualGuid = new TextField { Placeholder = AppStrings.T("naming.settings.manualGuidPlaceholder"), Text = _namingEditParamGuid };
            manualGuid.TextChanged += t => { _namingEditParamGuid = t ?? ""; ValidateNamingEditor(); RenderNamingLiveTestOnly(); };
            Grid.SetColumn(manualGuid, 2);
            manualRow.Children.Add(manualGuid);

            stack.Children.Add(manualRow);
            stack.Children.Add(NamingCaption(AppStrings.T("naming.settings.manualCaption"), new Thickness(0, 4, 0, 0)));

            var matched = catalog.FirstOrDefault(c =>
                string.Equals(c.Name, _namingEditParamName, StringComparison.OrdinalIgnoreCase) &&
                (c.Guid?.ToString() ?? "") == _namingEditParamGuid);
            if (matched != null && matched.StorageType != "String" && matched.StorageType != "")
                stack.Children.Add(NamingCaption(AppStrings.T("naming.settings.nonStringNotice"), new Thickness(0, 6, 0, 0)));

            return stack;
        }

        private Border BuildNamingParameterRow(ParameterCatalogEntry entry)
        {
            bool selected = string.Equals(entry.Name, _namingEditParamName, StringComparison.OrdinalIgnoreCase) &&
                            (entry.Guid?.ToString() ?? "") == _namingEditParamGuid;

            var row = new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                Cursor  = Cursors.Hand,
                Background = System.Windows.Media.Brushes.Transparent,
            };
            if (selected) row.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var origin = new TextBlock { Text = entry.OriginLabel, VerticalAlignment = VerticalAlignment.Center };
            origin.SetResourceReference(TextBlock.ForegroundProperty, entry.Guid.HasValue ? "LemoineAccent" : "LemoineTextDim");
            origin.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            origin.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(origin, 1);
            grid.Children.Add(origin);

            row.Child = grid;
            row.MouseLeftButtonUp += (s, e) =>
            {
                _namingEditParamName = entry.Name;
                _namingEditParamGuid = entry.Guid?.ToString() ?? "";
                ValidateNamingEditor();
                RenderNamingEditor();
            };
            return row;
        }

        private UIElement BuildNamingLiveTestBox()
        {
            var box = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 10, 7),
            };
            box.SetResourceReference(Border.BackgroundProperty,   "LemoineRaised");
            box.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
            box.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");

            var text = new TextBlock { Text = NamingLiveTestText(), TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic };
            text.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            text.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            text.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            box.Child = text;
            return box;
        }

        // Cheap in-place refresh of just the live-test line, so typing a fallback value
        // doesn't have to rebuild the whole card (the parameter list keeps its scroll position).
        private void RenderNamingLiveTestOnly()
        {
            var body = _namingEditorCard?.Child as StackPanel;
            if (body == null) { RenderNamingEditor(); return; }
            var box = body.Children.OfType<Border>().LastOrDefault();
            if (box?.Child is TextBlock tb) tb.Text = NamingLiveTestText();
            else RenderNamingEditor();
        }

        private string NamingLiveTestText()
        {
            var catalog = CurrentNamingParameterCatalog();
            var matched = catalog.FirstOrDefault(c =>
                string.Equals(c.Name, _namingEditParamName, StringComparison.OrdinalIgnoreCase) &&
                (c.Guid?.ToString() ?? "") == _namingEditParamGuid);

            if (matched != null && !string.IsNullOrEmpty(matched.SampleValue))
                return AppStrings.T("naming.settings.liveTestSample", matched.SampleValue);

            return string.IsNullOrEmpty(_namingEditFallback)
                ? AppStrings.T("naming.settings.liveTestNoFallback")
                : AppStrings.T("naming.settings.liveTestFallback", _namingEditFallback);
        }

        private void ValidateNamingEditor()
        {
            var candidate = new UserTokenDto
            {
                Key           = _namingEditKeyText,
                Label         = _namingEditLabel,
                Subject       = _namingEditSubject,
                Entity        = _namingEditEntity,
                ParameterName = _namingEditParamName,
                ParameterGuid = _namingEditParamGuid,
                FallbackText  = _namingEditFallback,
                Description   = _namingEditDescription,
            };
            _namingError = UserTokenStore.Instance.Validate(candidate, _namingEditOriginalKey);
        }

        private void SaveNamingToken()
        {
            ValidateNamingEditor();
            if (!string.IsNullOrEmpty(_namingError)) { RenderNamingEditor(); return; }

            var dto = new UserTokenDto
            {
                Key           = _namingEditKeyText,
                Label         = _namingEditLabel,
                Subject       = _namingEditSubject,
                Entity        = _namingEditEntity,
                ParameterName = _namingEditParamName,
                ParameterGuid = _namingEditParamGuid,
                FallbackText  = _namingEditFallback,
                Description   = _namingEditDescription,
            };
            UserTokenStore.Instance.AddOrUpdate(dto, _namingEditOriginalKey);
            FlashNamingStatus(AppStrings.T("naming.settings.saved", dto.Label));
            RefreshNamingTokenList();
            LoadNamingEditorFor(dto);
        }

        // Reuses the footer status flash already wired for the rest of the settings window.
        private void FlashNamingStatus(string text)
        {
            if (_fStatusText == null) return;
            _fStatusText.Text = text;
        }

        // ── Small shared builders ────────────────────────────────────────────
        private TextBlock NamingSectionLabel(string text)
        {
            var tb = new TextBlock { Text = text };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            return tb;
        }

        private TextBlock NamingFieldLabel(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 5) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        private TextBlock NamingCaption(string text, Thickness margin)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin, FontStyle = FontStyles.Italic };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        // Derives a PascalCase key from a display label: "Sheet Series" -> "SheetSeries".
        // Non-alphanumeric characters split words; a leading digit gets a "T" prefix since
        // keys must start with a letter (UserTokenStore.Validate enforces the same rule).
        private static string DeriveNamingKey(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "";
            var words = label.Split(new[] { ' ', '-', '_', '.', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            foreach (var w in words)
            {
                var clean = new string(w.Where(char.IsLetterOrDigit).ToArray());
                if (clean.Length == 0) continue;
                sb.Append(char.ToUpperInvariant(clean[0])).Append(clean.Substring(1));
            }
            string key = sb.ToString();
            if (key.Length > 0 && char.IsDigit(key[0])) key = "T" + key;
            return key.Length > 40 ? key.Substring(0, 40) : key;
        }
    }
}
