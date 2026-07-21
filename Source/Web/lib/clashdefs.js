/* Clash Definitions library (HTML analogue of ClashDefinitionsWindow + ClashGroupEditor).
   Sidebar of saved definitions + a per-definition editor: name, Group 1 / Group 2 (mode selector,
   source-document tree, rules/categories tabs or element-pick), and marking settings. Every change
   posts an action to C#, which owns the buffer and auto-saves on close. ASCII-only (rule R13). */
(function () {
  'use strict';

  function clashDefs(opts) {
    opts = opts || {};
    var send = opts.send || function () {};
    var ui = (window.Lemoine && window.Lemoine.ui) || {};
    var el = ui.el || function (t, c, x) { var e = document.createElement(t); if (c) e.className = c; if (x != null) e.textContent = x; return e; };

    var payload = null, root = null;
    var expanded = {};   // doctree expand state, key "group:linkId" -> true (preserved across renders)

    function action(a, extra) { var p = { action: a }; if (extra) for (var k in extra) p[k] = extra[k]; send('action', p); }
    function L(k) { return (payload && payload.labels && payload.labels[k]) || ''; }

    function render(host, p) { root = host; payload = p; draw(); }

    function draw() {
      root.innerHTML = '';
      root.appendChild(buildToolbar());
      var body = el('div', 'l-cd-body');
      body.appendChild(buildSidebar());
      body.appendChild(buildEditor());
      root.appendChild(body);
    }

    // -- Toolbar -----------------------------------------------------------------
    function buildToolbar() {
      var bar = el('div', 'l-set-toolbar');
      var title = el('div', 'title', payload.title || 'Clash Definitions');
      title.addEventListener('mousedown', function (e) { if (e.button === 0) action('drag'); });
      bar.appendChild(title);
      var tools = el('div', 'tools');
      [['\u25A1', 'maximize', 'Maximize'], ['\u00d7', 'close', 'Close']]
        .forEach(function (b) { var x = el('button', 'win-btn', b[0]); x.title = b[2]; x.addEventListener('click', function () { action(b[1]); }); tools.appendChild(x); });
      bar.appendChild(tools);
      return bar;
    }

    // -- Sidebar -----------------------------------------------------------------
    function glyphBtn(cp, tip, onClick) {
      var b = el('button', 'l-cd-glyph'); b.title = tip;
      var s = el('span', null, cp); s.style.fontFamily = "'Segoe MDL2 Assets'"; b.appendChild(s);
      b.addEventListener('click', function (e) { e.stopPropagation(); onClick(); });
      return b;
    }

    function buildSidebar() {
      var side = el('div', 'l-cd-side');
      (payload.definitions || []).forEach(function (d) {
        var row = el('div', 'l-cd-defrow' + (d.id === payload.activeId ? ' active' : ''));
        row.appendChild(el('span', 'nm', d.name));
        var acts = el('span', 'acts');
        acts.appendChild(glyphBtn(L('dupGlyph'), L('dupTooltip'), function () { action('dupDef', { id: d.id }); }));
        acts.appendChild(glyphBtn(L('delGlyph'), L('delTooltip'), function () { action('delDef', { id: d.id }); }));
        row.appendChild(acts);
        row.addEventListener('click', function () { if (d.id !== payload.activeId) action('selectDef', { id: d.id }); });
        side.appendChild(row);
      });
      var add = el('button', 'l-btn l-cd-add', payload.addPill || '+ Add');
      add.addEventListener('click', function () { action('addDef'); });
      side.appendChild(add);
      return side;
    }

    // -- Editor ------------------------------------------------------------------
    function buildEditor() {
      var ed = el('div', 'l-cd-editor');
      var e = payload.editor;
      if (!e) { ed.appendChild(el('div', 'l-cd-empty', L('noSelection'))); return ed; }

      ed.appendChild(el('div', 'l-cd-flabel', L('name')));
      var name = el('input', 'l-cd-input'); name.type = 'text'; name.value = e.name || '';
      name.addEventListener('change', function () { action('setName', { value: name.value }); });
      ed.appendChild(name);

      ed.appendChild(card(L('group1Header'), buildGroup(1, e.group1)));
      ed.appendChild(card(L('group2Header'), buildGroup(2, e.group2)));
      ed.appendChild(card(L('markingSettings'), buildMarking(e.marking)));
      return ed;
    }

    function card(header, contentEl) {
      var c = el('div', 'l-cd-card');
      c.appendChild(el('div', 'l-cd-cardhead', header));
      c.appendChild(contentEl);
      return c;
    }

    // -- Group editor --------------------------------------------------------------
    function buildGroup(group, g) {
      var wrap = el('div');
      wrap.appendChild(el('div', 'l-cd-flabel', L('modeLabel')));
      wrap.appendChild(ui.singleSelect({
        options: [{ value: 'Rules', label: L('modeRules') }, { value: 'Categories', label: L('modeCategories') }, { value: 'Elements', label: L('modeElements') }],
        value: g.mode,
        onChange: function (v) { action('setMode', { group: group, value: v }); }
      }).el);

      divider(wrap);
      wrap.appendChild(el('div', 'l-cd-flabel', L('sourceDocs')));
      wrap.appendChild(el('div', 'l-cd-caption', L('sourceDocsDesc')));
      wrap.appendChild(buildDocTree(group, g.docs));

      divider(wrap);
      if (g.mode === 'Categories')      wrap.appendChild(buildCategories(group, g.categories));
      else if (g.mode === 'Elements')   wrap.appendChild(buildElements(group, g.elements));
      else                              wrap.appendChild(buildRules(group, g.rules));
      return wrap;
    }

    function buildDocTree(group, docs) {
      var tree = el('div', 'l-cd-doctree');
      if (!docs || docs.length === 0) { tree.appendChild(el('div', 'l-cd-caption', L('noDocs'))); return tree; }
      docs.forEach(function (d) {
        var key = group + ':' + d.linkId;
        var isOpen = expanded[key] === true;

        var head = el('div', 'l-cd-docrow');
        var caret = el('span', 'caret', d.hasWorksets ? (isOpen ? '\u25BE' : '\u25B8') : '');
        if (d.hasWorksets) caret.addEventListener('click', function (e) { e.stopPropagation(); expanded[key] = !isOpen; draw(); });
        head.appendChild(caret);
        var cb = el('input'); cb.type = 'checkbox'; cb.checked = !!d.selected;
        cb.addEventListener('change', function () { action('toggleDoc', { group: group, linkId: d.linkId, value: cb.checked }); });
        head.appendChild(cb);
        head.appendChild(el('span', 'nm', d.name));
        tree.appendChild(head);

        if (d.hasWorksets && isOpen) {
          (d.worksets || []).forEach(function (ws) {
            var wr = el('div', 'l-cd-wsrow');
            var wcb = el('input'); wcb.type = 'checkbox'; wcb.checked = !!ws.included; wcb.disabled = !!ws.disabled;
            wcb.addEventListener('change', function () { action('toggleWorkset', { group: group, linkId: d.linkId, wsId: ws.id, value: wcb.checked }); });
            wr.appendChild(wcb);
            wr.appendChild(el('span', 'nm' + (ws.disabled ? ' dim' : ''), ws.name));
            tree.appendChild(wr);
          });
        }
      });
      return tree;
    }

    function buildRules(group, rules) {
      if (!rules || !rules.groups || Object.keys(rules.groups).length === 0) return el('div', 'l-cd-caption', L('rulesEmpty'));
      return ui.multiSelectTabs({
        groups: rules.groups, selected: rules.selected, height: '210px',
        onChange: function (sel) { action('setRules', { group: group, values: sel }); }
      }).el;
    }

    function buildCategories(group, cats) {
      if (!cats || !cats.groups || Object.keys(cats.groups).length === 0) return el('div', 'l-cd-caption', L('catsEmpty'));
      return ui.multiSelectTabs({
        groups: cats.groups, selected: cats.selected, hierarchy: cats.hierarchy, height: '210px',
        onChange: function (sel) { action('setCategories', { group: group, values: sel }); }
      }).el;
    }

    function buildElements(group, elements) {
      var wrap = el('div');
      var btns = el('div', 'l-cd-elembtns');
      var host = el('button', 'l-btn', L('pickHost'));
      host.addEventListener('click', function () { action('pickElements', { group: group, inLinks: false }); });
      var links = el('button', 'l-btn', L('pickLinks'));
      links.addEventListener('click', function () { action('pickElements', { group: group, inLinks: true }); });
      var clear = el('button', 'l-btn', L('clear'));
      clear.addEventListener('click', function () { action('clearElements', { group: group }); });
      btns.appendChild(host); btns.appendChild(links); btns.appendChild(clear);
      wrap.appendChild(btns);
      var n = (elements && elements.count) || 0;
      wrap.appendChild(el('div', 'l-cd-caption', n === 0 ? L('elemNone') : L('elemCount').replace('{0}', n)));
      return wrap;
    }

    // -- Marking settings ----------------------------------------------------------
    function buildMarking(m) {
      var wrap = el('div');

      wrap.appendChild(el('div', 'l-cd-flabel', L('tolerance')));
      wrap.appendChild(ui.stepper({ value: m.tolerance, min: 0, max: 100, step: 0.5, decimals: 1,
        onChange: function (v) { action('setMarking', { field: 'tolerance', value: v }); } }).el);
      wrap.appendChild(el('div', 'l-cd-caption', L('toleranceDesc')));

      wrap.appendChild(el('div', 'l-cd-flabel', L('maxClashes')));
      wrap.appendChild(ui.stepper({ value: m.maxClashes, min: 1, max: 100000, step: 1, decimals: 0,
        onChange: function (v) { action('setMarking', { field: 'maxClashes', value: v }); } }).el);
      wrap.appendChild(el('div', 'l-cd-caption', L('maxClashesDesc')));

      wrap.appendChild(el('div', 'l-cd-flabel', L('fillStyle')));
      wrap.appendChild(ui.singleSelect({ value: m.fillStyle,
        options: [{ value: 'Solid', label: L('fillSolid') }, { value: 'Outline', label: L('fillOutline') }],
        onChange: function (v) { action('setMarking', { field: 'fillStyle', value: v }); } }).el);

      divider(wrap);
      wrap.appendChild(el('div', 'l-cd-flabel', L('markerReference')));
      wrap.appendChild(ui.singleSelect({ value: m.dimTarget,
        options: [{ value: 'Edge', label: L('targetEdge') }, { value: 'Centre', label: L('targetCentre') }],
        onChange: function (v) { action('setMarking', { field: 'dimTarget', value: v }); } }).el);

      divider(wrap);
      wrap.appendChild(el('div', 'l-cd-flabel', L('phase')));
      wrap.appendChild(ui.singleSelect({ value: m.phaseMode,
        options: [{ value: 'All', label: L('phaseAll') }, { value: 'MatchView', label: L('phaseMatch') }, { value: 'Specific', label: L('phaseSpecific') }],
        onChange: function (v) { action('setMarking', { field: 'phaseMode', value: v }); } }).el);
      wrap.appendChild(el('div', 'l-cd-caption', L('phaseDesc')));
      if (m.phaseMode === 'Specific') {
        if (m.hostPhases && m.hostPhases.length > 0) {
          wrap.appendChild(el('div', 'l-cd-flabel', L('hostPhase')));
          wrap.appendChild(ui.singleSelect({ value: m.specificPhase,
            options: m.hostPhases.map(function (n) { return { value: n, label: n }; }),
            onChange: function (v) { action('setMarking', { field: 'specificPhase', value: v }); } }).el);
        } else {
          wrap.appendChild(el('div', 'l-cd-caption', L('noHostPhases')));
        }
      }

      divider(wrap);
      wrap.appendChild(el('div', 'l-cd-flabel', L('crossLineStyle')));
      var lineOpts = [{ value: '', label: L('lineDefault') }].concat((m.lineStyles || []).map(function (n) { return { value: n, label: n }; }));
      wrap.appendChild(ui.singleSelect({ value: m.crossLine, options: lineOpts,
        onChange: function (v) { action('setMarking', { field: 'crossLine', value: v }); } }).el);

      divider(wrap);
      wrap.appendChild(el('div', 'l-cd-flabel', L('fallbackColor')));
      wrap.appendChild(ui.colorPicker({ value: m.fallbackColor,
        onChange: function (v) { action('setMarking', { field: 'fallbackColor', value: v }); } }).el);
      return wrap;
    }

    function divider(parent) { parent.appendChild(el('div', 'l-cd-divider')); }

    return { render: render };
  }

  window.Lemoine = window.Lemoine || {};
  window.Lemoine.clashDefs = clashDefs;
})();
