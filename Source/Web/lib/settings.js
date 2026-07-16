/* Global Settings shell (settings.html). Renders the tab nav + General-tab content and
   reports every change back to C# as an `action` message. ASCII-only (rule R13): non-ASCII
   glyphs are \uXXXX escapes. Only the General tab is web-backed; other tabs show a placeholder. */
(function () {
  'use strict';

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  function settings(root, opts) {
    opts = opts || {};
    var send = opts.send || function () {};
    var state = null;

    // Naming tab is a bespoke master/detail editor (lib/naming.js). It owns its own state; a
    // failed save comes back as a namingError message which the controller shows inline.
    var naming = (window.Lemoine && window.Lemoine.namingTab) ? window.Lemoine.namingTab({ send: send }) : null;
    if (naming && window.Lemoine && window.Lemoine.on)
      window.Lemoine.on('namingError', function (p) { naming.showError(p && p.message); });

    function action(a, extra) {
      var payload = { action: a };
      if (extra) for (var k in extra) payload[k] = extra[k];
      send('action', payload);
    }

    function init(payload) {
      state = payload;
      render();
    }

    function render() {
      root.innerHTML = '';
      root.appendChild(buildToolbar());
      root.appendChild(buildTabNav());
      var content = el('div', 'l-set-content');
      if (state.active === 'general')                          buildGeneral(content);
      else if (state.active === 'naming' && state.naming && naming) naming.render(content, state.naming, state.namingSelectKey);
      else if (state.tab && state.tab.id === state.active)     buildSpecTab(content);
      else                                                     buildPlaceholder(content);
      root.appendChild(content);
      root.appendChild(buildFooter());
    }

    // ── Spec-driven tabs (WebInput rows built by WebSettings.BuildTab) ───────────
    function buildSpecTab(content) {
      var t = state.tab;
      if (t.header) content.appendChild(el('div', 'l-set-header', t.header));
      (t.rows || []).forEach(function (row) {
        var node = buildField(row);
        if (!node) return;
        if (row.kind === 'settingsSection' || row.kind === 'settingsSep' || row.kind === 'hint') {
          content.appendChild(node);                     // structural rows render bare
        } else {
          var w = el('div', 'l-set-row'); w.appendChild(node); content.appendChild(w);
        }
      });
      if (t.note) content.appendChild(el('div', 'l-set-hint', t.note));
    }

    // Maps one spec row to a lemoine.js factory; every change auto-saves via a setField action.
    function buildField(f) {
      var ui = (window.Lemoine && window.Lemoine.ui) || {};
      function change(value) { action('setField', { tabId: state.active, fieldId: f.id, value: value }); }
      switch (f.kind) {
        case 'settingsSection': return el('div', 'l-set-sub', f.text);
        case 'settingsSep':     return el('div', 'l-set-hr');
        case 'hint':            return el('div', 'l-set-hint', f.text);
        case 'toggle':          return ui.toggle({ label: f.label, checked: f.checked, disabled: f.disabled, onChange: change }).el;
        case 'textField':       return ui.textField({ label: f.label, value: f.value, placeholder: f.placeholder, onChange: change }).el;
        case 'colorPicker':     return ui.colorPicker({ label: f.label, value: f.value, onChange: change }).el;
        case 'stepper':         return labeledField(f.label, ui.stepper({ min: f.min, max: f.max, step: f.step, decimals: f.decimals, value: f.value, onChange: change }).el);
        case 'singleSelect':    return labeledField(f.label, ui.singleSelect({ options: f.options, value: f.value, onChange: change }).el);
        default:                return el('div', 'l-set-hint', 'Unknown field: ' + f.kind);
      }
    }

    function labeledField(label, inner) {
      var wrap = el('div', 'l-set-fieldwrap');
      if (label) wrap.appendChild(el('div', 'l-set-fieldlabel', label));
      wrap.appendChild(inner);
      return wrap;
    }

    // ── Toolbar ──────────────────────────────────────────────────────────────
    function buildToolbar() {
      var bar = el('div', 'l-set-toolbar');
      var title = el('div', 'title', state.title || 'Settings');
      title.addEventListener('mousedown', function (e) {
        if (e.button === 0) action('drag');
      });
      bar.appendChild(title);

      var tools = el('div', 'tools');
      var min = el('button', 'win-btn', '\u2013');
      min.title = 'Minimize';
      min.addEventListener('click', function () { action('minimize'); });
      var close = el('button', 'win-btn', '\u00d7');
      close.title = 'Close';
      close.addEventListener('click', function () { action('close'); });
      tools.appendChild(min);
      tools.appendChild(close);
      bar.appendChild(tools);
      return bar;
    }

    // ── Tab nav ──────────────────────────────────────────────────────────────
    function buildTabNav() {
      var nav = el('div', 'l-set-nav');
      (state.tabs || []).forEach(function (t) {
        var tab = el('button', 'tab' + (t.id === state.active ? ' active' : '') + (t.migrated ? '' : ' soft'), t.label);
        tab.addEventListener('click', function () { action('switchTab', { tabId: t.id }); });
        nav.appendChild(tab);
      });
      return nav;
    }

    // ── Placeholder (unmigrated tabs) ────────────────────────────────────────
    function buildPlaceholder(content) {
      var box = el('div', 'l-set-placeholder', state.notMigrated || '');
      content.appendChild(box);
    }

    // ── General tab ──────────────────────────────────────────────────────────
    function buildGeneral(content) {
      var g = state.general || {};
      content.appendChild(el('div', 'l-set-header', g.header));

      // Theme
      content.appendChild(el('div', 'l-set-sub', g.themeLabel));
      content.appendChild(el('div', 'l-set-tag', g.darkLabel));
      content.appendChild(themeGrid((g.themes || []).filter(function (t) { return t.dark; }), g));
      content.appendChild(el('div', 'l-set-tag', g.lightLabel));
      content.appendChild(themeGrid((g.themes || []).filter(function (t) { return !t.dark; }), g));

      content.appendChild(hr());

      // UI size
      content.appendChild(el('div', 'l-set-sub', g.sizeLabel));
      (g.sizes || []).forEach(function (s) {
        content.appendChild(choiceRow(s.name, s.desc, s.active, function () { action('setSize', { id: s.id }); }));
      });

      content.appendChild(hr());

      // Language
      content.appendChild(el('div', 'l-set-sub', g.langLabel));
      (g.languages || []).forEach(function (l) {
        content.appendChild(choiceRow(l.name, l.culture, l.active, function () { action('setLanguage', { culture: l.culture }); }));
      });
      content.appendChild(el('div', 'l-set-hint', g.langHint));

      content.appendChild(hr());

      // Diagnostics
      content.appendChild(el('div', 'l-set-sub', g.diagLabel));
      content.appendChild(el('div', 'l-set-hint', g.diagDesc));
      var path = el('div', 'l-set-path', g.logPath);
      content.appendChild(path);
      var openBtn = el('button', 'l-btn', g.openLog);
      openBtn.addEventListener('click', function () { action('openLog'); });
      content.appendChild(openBtn);
    }

    function themeGrid(themes, g) {
      var grid = el('div', 'l-set-themes');
      themes.forEach(function (t) {
        var card = el('div', 'l-set-theme' + (t.active ? ' active' : ''));
        card.style.background = t.bg;
        card.style.borderColor = t.active ? t.accent : t.border;

        var preview = el('div', 'preview');
        preview.style.background = t.raised;
        var bar = el('div', 'bar'); bar.style.background = t.border;
        var sw = el('div', 'swatches');
        [ [t.accent, 'a'], [t.green, 's'], [t.red, 's'] ].forEach(function (pair) {
          var s = el('div', 'sw ' + pair[1]); s.style.background = pair[0]; sw.appendChild(s);
        });
        preview.appendChild(bar); preview.appendChild(sw);
        card.appendChild(preview);

        var name = el('div', 'name', t.name); name.style.color = t.text;
        card.appendChild(name);
        if (t.active) {
          var badge = el('div', 'badge', g.activeBadge);
          badge.style.color = t.accent; badge.style.borderColor = t.accent;
          card.appendChild(badge);
        }
        card.addEventListener('click', function () { action('setTheme', { name: t.name }); });
        grid.appendChild(card);
      });
      return grid;
    }

    function choiceRow(name, desc, active, onClick) {
      var row = el('div', 'l-set-choice' + (active ? ' active' : ''));
      var text = el('div', 'text');
      text.appendChild(el('div', 'name', name));
      if (desc) text.appendChild(el('div', 'desc', desc));
      row.appendChild(text);
      var pill = el('div', 'pill');
      row.appendChild(pill);
      row.addEventListener('click', onClick);
      return row;
    }

    function hr() { return el('div', 'l-set-hr'); }

    function buildFooter() {
      var f = el('div', 'l-set-footer');
      f.appendChild(el('div', 'build', state.build || ''));
      var close = el('button', 'l-btn', state.close || 'Close');
      close.addEventListener('click', function () { action('close'); });
      f.appendChild(close);
      return f;
    }

    return { init: init };
  }

  window.Lemoine = window.Lemoine || {};
  window.Lemoine.settings = settings;
})();
