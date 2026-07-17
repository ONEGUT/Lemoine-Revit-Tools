/* Auto Filters library (HTML analogue of FiltersSettingsWindow / the Filters tab).
   Three-column surface: trades sidebar (templates, checkboxes, Discover/Add Trade,
   apply/remove footer), rule list for the active trade (color swatch + name + categories,
   drag-reorder, Add Rule, Apply-trade-to-view), and the rule editor (Filter Logic /
   Override Style / Appearance & Visibility). Every change posts an action to C#, which
   owns the settings buffer, history and Revit application. ASCII-only (rule R13). */
(function () {
  'use strict';

  function autoFilters(opts) {
    opts = opts || {};
    var send = opts.send || function () {};
    var ui = (window.Lemoine && window.Lemoine.ui) || {};
    var el = ui.el || function (t, c, x) { var e = document.createElement(t); if (c) e.className = c; if (x != null) e.textContent = x; return e; };

    var payload = null, root = null;

    function action(a, extra) { var p = { action: a }; if (extra) for (var k in extra) p[k] = extra[k]; send('action', p); }
    function L(k) { return (payload && payload.labels && payload.labels[k]) || ''; }

    function svgIcon(path, size) {
      var s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      s.setAttribute('width', size || 13); s.setAttribute('height', size || 13);
      s.setAttribute('viewBox', '0 0 16 16'); s.setAttribute('fill', 'none');
      var p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
      p.setAttribute('d', path); p.setAttribute('stroke', 'currentColor');
      p.setAttribute('stroke-width', '1.3'); p.setAttribute('stroke-linecap', 'round');
      p.setAttribute('stroke-linejoin', 'round');
      s.appendChild(p);
      return s;
    }
    var PENCIL = 'M11.5 2.5l2 2L5 13l-2.6.6L3 11l8.5-8.5z';

    function render(host, p) { root = host; payload = p; draw(); }

    function draw() {
      root.innerHTML = '';
      root.appendChild(buildToolbar());
      var body = el('div', 'l-af-body');
      body.appendChild(buildSidebar());
      body.appendChild(buildRuleList());
      body.appendChild(buildEditor());
      root.appendChild(body);
    }

    // -- Toolbar -----------------------------------------------------------------
    function buildToolbar() {
      var bar = el('div', 'l-set-toolbar');
      var title = el('div', 'title', payload.title || 'Auto Filters');
      title.addEventListener('mousedown', function (e) { if (e.button === 0) action('drag'); });
      bar.appendChild(title);
      var tools = el('div', 'tools');
      tools.style.gap = '8px';

      var undo = el('button', 'l-btn', '\u21B6'); undo.title = L('undoTip'); undo.disabled = !payload.canUndo;
      undo.addEventListener('click', function () { action('undo'); });
      tools.appendChild(undo);
      var redo = el('button', 'l-btn', '\u21B7'); redo.title = L('redoTip'); redo.disabled = !payload.canRedo;
      redo.addEventListener('click', function () { action('redo'); });
      tools.appendChild(redo);

      var hist = el('button', 'l-btn', L('history') + ' \u25BE');
      hist.addEventListener('click', function () { action('history'); });
      tools.appendChild(hist);

      var del = el('button', 'l-btn danger-pill', L('deleteFromProject'));
      del.addEventListener('click', function () { action('deleteFromProject'); });
      tools.appendChild(del);

      var close = el('button', 'win-btn', '\u00d7'); close.title = 'Close';
      close.addEventListener('click', function () { action('close'); });
      tools.appendChild(close);
      bar.appendChild(tools);
      return bar;
    }

    // -- Trades sidebar ------------------------------------------------------------
    function buildSidebar() {
      var side = el('div', 'l-af-side');

      var top = el('div', 'l-af-side-top');
      var tmpl = el('button', 'l-btn l-af-templates', L('templates') + ' \u25BE');
      tmpl.addEventListener('click', function () { action('templates'); });
      top.appendChild(tmpl);
      side.appendChild(top);

      var list = el('div', 'l-af-tradelist');
      (payload.trades || []).forEach(function (t) {
        var row = el('div', 'l-af-trade' + (t.active ? ' active' : ''));
        var cb = el('input'); cb.type = 'checkbox'; cb.checked = !!t.checked;
        cb.addEventListener('click', function (e) { e.stopPropagation(); });
        cb.addEventListener('change', function () { action('checkTrade', { id: t.id, value: cb.checked }); });
        row.appendChild(cb);
        var dot = el('span', 'dot'); dot.style.background = t.color; row.appendChild(dot);
        row.appendChild(el('span', 'nm', t.label));
        var edit = el('button', 'l-lc-gbtn'); edit.title = L('editTradeTip'); edit.appendChild(svgIcon(PENCIL));
        edit.addEventListener('click', function (e) { e.stopPropagation(); action('editTrade', { id: t.id }); });
        row.appendChild(edit);
        row.addEventListener('click', function () { if (!t.active) action('selectTrade', { id: t.id }); });
        list.appendChild(row);
      });
      side.appendChild(list);

      var acts = el('div', 'l-af-side-actions');
      var disc = el('button', 'l-btn', L('discover'));
      disc.addEventListener('click', function () { action('discover'); });
      acts.appendChild(disc);
      var add = el('button', 'l-btn', '+ ' + L('addTrade'));
      add.addEventListener('click', function () { action('addTrade'); });
      acts.appendChild(add);
      side.appendChild(acts);

      var foot = el('div', 'l-af-side-foot');
      var apply = el('button', 'l-btn apply', L('applyToView'));
      apply.addEventListener('click', function () { action('applyToView'); });
      foot.appendChild(apply);
      var remove = el('button', 'l-btn', L('removeFromView'));
      remove.addEventListener('click', function () { action('removeFromView'); });
      foot.appendChild(remove);
      side.appendChild(foot);

      return side;
    }

    // -- Rule list -----------------------------------------------------------------
    function buildRuleList() {
      var mid = el('div', 'l-af-mid');
      var list = el('div', 'l-af-rules');

      (payload.rules || []).forEach(function (r, idx) {
        var row = el('div', 'l-af-rule' + (r.active ? ' active' : '') + (r.enabled === false ? ' disabled' : ''));
        row.draggable = true;
        var sw = el('span', 'sw'); sw.style.background = r.color; row.appendChild(sw);
        var txt = el('div', 'txt');
        txt.appendChild(el('div', 'nm', r.name));
        txt.appendChild(el('div', 'cat', r.catLabel || ''));
        row.appendChild(txt);
        var edit = el('button', 'l-lc-gbtn'); edit.title = L('editRuleTip'); edit.appendChild(svgIcon(PENCIL));
        edit.addEventListener('click', function (e) { e.stopPropagation(); action('editRulePopup', { id: r.id }); });
        row.appendChild(edit);
        row.addEventListener('click', function () { if (!r.active) action('selectRule', { id: r.id }); });

        // HTML5 drag reorder: the row being dragged carries its index; drop targets report theirs.
        row.addEventListener('dragstart', function (ev) { ev.dataTransfer.setData('text/l-af-rule', String(idx)); ev.dataTransfer.effectAllowed = 'move'; });
        row.addEventListener('dragover', function (ev) { if (ev.dataTransfer.types.indexOf('text/l-af-rule') >= 0) { ev.preventDefault(); ev.dataTransfer.dropEffect = 'move'; } });
        row.addEventListener('drop', function (ev) {
          ev.preventDefault();
          var from = parseInt(ev.dataTransfer.getData('text/l-af-rule'), 10);
          if (!isNaN(from) && from !== idx) action('reorderRule', { from: from, to: idx });
        });
        list.appendChild(row);
      });
      mid.appendChild(list);

      var addWrap = el('div', 'l-af-addrule');
      var add = el('button', 'l-btn', '+  ' + L('addRule'));
      add.addEventListener('click', function () { action('addRule'); });
      addWrap.appendChild(add);
      mid.appendChild(addWrap);

      var applyWrap = el('div', 'l-af-applywrap');
      var apply = el('button', 'l-btn l-af-apply', L('applyTradeToView'));
      apply.addEventListener('click', function () { action('applyTradeToView'); });
      applyWrap.appendChild(apply);
      mid.appendChild(applyWrap);

      return mid;
    }

    // -- Rule editor -----------------------------------------------------------------
    function buildEditor() {
      var ed = el('div', 'l-af-editor');
      var r = payload.editor;
      if (!r) { ed.appendChild(el('div', 'l-af-noselect', L('noRule'))); return ed; }

      ed.appendChild(el('div', 'l-af-sechead', L('filterLogic')));
      ed.appendChild(buildFilterLogic(r));
      ed.appendChild(el('div', 'l-af-sechead', L('overrideStyle')));
      ed.appendChild(buildOverrideStyle(r));
      ed.appendChild(el('div', 'l-af-sechead', L('appearance')));
      ed.appendChild(buildAppearance(r));
      return ed;
    }

    function chipRow(items, addTip, onRemove, onAdd) {
      var row = el('div', 'l-af-chiprow');
      items.forEach(function (it) {
        var chip = el('span', 'l-af-chip');
        chip.appendChild(el('span', null, it.label));
        var x = el('span', 'x', '\u00d7'); x.title = L('removeTip');
        x.addEventListener('click', function () { onRemove(it); });
        chip.appendChild(x);
        row.appendChild(chip);
      });
      var add = el('button', 'l-btn l-af-chipadd', '+'); add.title = addTip;
      add.addEventListener('click', onAdd);
      row.appendChild(add);
      return row;
    }

    function buildFilterLogic(r) {
      var card = el('div', 'l-af-card');

      card.appendChild(el('div', 'l-af-lbl', L('category')));
      card.appendChild(chipRow(
        (r.categories || []).map(function (c) { return { label: c.label, ost: c.ost }; }),
        L('addCategoryTip'),
        function (it) { action('removeCategory', { ost: it.ost }); },
        function () { action('addCategory'); }));

      var allRow = el('div', 'l-af-allrow');
      var allBtn = el('button', 'l-af-toggle' + (r.matchType === 'all' ? ' on' : ''), L('all'));
      allBtn.style.minWidth = '86px';
      allBtn.addEventListener('click', function () { action('setMatchAll', { value: r.matchType !== 'all' }); });
      allRow.appendChild(allBtn);
      allRow.appendChild(el('div', 'cap', L('allDesc')));
      card.appendChild(allRow);

      card.appendChild(el('div', 'l-af-lbl', L('parameter')));
      card.appendChild(chipRow(
        r.parameter ? [{ label: r.parameter }] : [],
        L('pickParameterTip'),
        function () { action('clearParameter'); },
        function () { action('pickParameter'); }));
      if (r.parameterBuiltIn) card.appendChild(el('div', 'l-af-caption', '\u2713 ' + L('builtInParam')));

      card.appendChild(el('div', 'l-af-lbl', L('searchString')));
      card.appendChild(chipRow(
        (r.keywords || []).map(function (k) { return { label: k }; }),
        L('addKeywordTip'),
        function (it) { action('removeKeyword', { value: it.label }); },
        function () { action('addKeyword'); }));

      var match = ui.dropdown({
        value: r.matchType === 'equals' ? 'equals' : 'contains',
        options: [{ value: 'contains', label: L('matchContains') }, { value: 'equals', label: L('matchEquals') }],
        onChange: function (v) { action('setMatchType', { value: v }); }
      });
      match.el.style.marginTop = '10px';
      card.appendChild(match.el);

      return card;
    }

    function overrideRow(card, key, o, hasLayers, patterns) {
      var row = el('div', 'l-af-orow');

      var tog = el('button', 'l-af-toggle' + (o.on ? ' on' : ''), L(key));
      tog.addEventListener('click', function () { action('setOverrideOn', { layer: key, value: !o.on }); });
      row.appendChild(tog);

      if (hasLayers) {
        var sw = el('span', 'l-af-fgbg');
        ['FG', 'BG'].forEach(function (lay) {
          var b = el('button', o.layer === lay ? 'on' : null, lay);
          b.addEventListener('click', function () { action('setLayer', { layer: key, value: lay }); });
          sw.appendChild(b);
        });
        row.appendChild(sw);
      } else {
        row.appendChild(el('span'));
      }

      var color = el('button', 'l-af-colorbox'); color.style.background = o.color;
      color.title = L('pickColorTip');
      color.addEventListener('click', function () { action('pickColor', { layer: key }); });
      row.appendChild(color);

      if (key === 'lines') {
        var st = ui.stepper({ value: o.weight, min: 1, max: 14, step: 1, decimals: 0,
          onChange: function (v) { action('setLineWeight', { value: v }); } });
        row.appendChild(st.el);
      } else {
        var pat = ui.dropdown({
          value: o.pattern || '',
          options: [{ value: '', label: L('solidFill') }].concat((patterns || []).map(function (p) { return { value: p, label: p }; })),
          onChange: function (v) { action('setPattern', { layer: key, value: v }); }
        });
        row.appendChild(pat.el);
      }
      card.appendChild(row);
    }

    function buildOverrideStyle(r) {
      var card = el('div', 'l-af-card');
      card.appendChild(el('div', 'l-af-lbl', L('colors')));
      overrideRow(card, 'surface', r.surface, true, r.patternOptions);
      overrideRow(card, 'cut', r.cut, true, r.patternOptions);
      overrideRow(card, 'lines', r.lines, false, null);

      card.appendChild(el('div', 'l-af-lbl', L('transparency')));
      var trow = el('div', 'l-af-transrow');
      var slider = el('input'); slider.type = 'range'; slider.min = 0; slider.max = 100; slider.value = r.transparency || 0;
      var badge = el('span', 'l-af-transbadge', (r.transparency || 0) + '%');
      slider.addEventListener('input', function () { badge.textContent = slider.value + '%'; });
      slider.addEventListener('change', function () { action('setTransparency', { value: parseInt(slider.value, 10) }); });
      trow.appendChild(slider); trow.appendChild(badge);
      card.appendChild(trow);
      return card;
    }

    function visRow(card, key, on, capMain, capDim) {
      var row = el('div', 'l-af-vrow');
      var tog = el('button', 'l-af-toggle' + (on ? ' on' : ''), L(key));
      tog.addEventListener('click', function () { action('setFlag', { flag: key, value: !on }); });
      row.appendChild(tog);
      var cap = el('div', 'cap');
      cap.appendChild(el('span', null, capMain));
      if (capDim) { cap.appendChild(el('span', 'dim', ' ' + capDim)); }
      row.appendChild(cap);
      card.appendChild(row);
    }

    function buildAppearance(r) {
      var card = el('div', 'l-af-card');
      visRow(card, 'halftone', r.halftone, L('halftoneDesc'));
      visRow(card, 'visible', r.visible, L('visibleDesc'));
      visRow(card, 'filterOn', r.filterOn, L('filterOnDesc'));
      return card;
    }

    return { render: render };
  }

  window.Lemoine = window.Lemoine || {};
  window.Lemoine.autoFilters = autoFilters;
})();
