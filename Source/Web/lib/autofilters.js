/* Auto Filters library (HTML analogue of FiltersSettingsWindow / the Filters tab).
   Three-column surface: trades sidebar (templates, checkboxes, Discover/Add Trade,
   apply/remove footer), rule list for the active trade (color swatch + name + categories,
   drag-reorder, Add Rule, Apply-trade-to-view), and the rule editor (Filter Logic /
   Override Style / Appearance & Visibility) - plus in-page modal overlays for the trade
   editor, add-trade form, rule editor popup, category/parameter pickers, keyword prompt,
   history list and templates menu. Every change posts an action to C#, which owns the
   settings buffer, history and Revit application. ASCII-only (rule R13). */
(function () {
  'use strict';

  function autoFilters(opts) {
    opts = opts || {};
    var send = opts.send || function () {};
    var ui = (window.Lemoine && window.Lemoine.ui) || {};
    var el = ui.el || function (t, c, x) { var e = document.createElement(t); if (c) e.className = c; if (x != null) e.textContent = x; return e; };

    var payload = null, root = null, overlayEl = null, statusTimer = null;

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

    function render(host, p) { root = host; payload = p; draw(); if (p && p.status) flashStatus(p.status); }

    function draw() {
      root.innerHTML = '';
      root.appendChild(buildToolbar());
      var body = el('div', 'l-af-body');
      body.appendChild(buildSidebar());
      body.appendChild(buildRuleList());
      body.appendChild(buildEditor());
      root.appendChild(body);
      overlayEl = el('div', 'l-sbm-overlay'); overlayEl.style.display = 'none';
      root.appendChild(overlayEl);
      var pill = el('div', 'l-sbm-status'); pill.id = 'l-af-status'; pill.style.display = 'none';
      root.appendChild(pill);
    }

    // -- Overlay machinery (shared modal card pattern) -----------------------------
    function openOverlay(title, bodyEl, footerBtns) {
      overlayEl.innerHTML = '';
      var dim = el('div', 'l-sbm-dim');
      dim.addEventListener('click', closeOverlay);
      overlayEl.appendChild(dim);
      var card = el('div', 'l-sbm-card');
      card.appendChild(el('div', 'l-sbm-ohead', title));
      var bodyWrap = el('div', 'l-sbm-obody');
      bodyWrap.appendChild(bodyEl);
      card.appendChild(bodyWrap);
      var foot = el('div', 'l-sbm-ofoot');
      var cancel = el('button', 'l-btn', L('cancel'));
      cancel.addEventListener('click', closeOverlay);
      foot.appendChild(cancel);
      foot.appendChild(el('div', 'l-sbm-ospacer'));
      (footerBtns || []).forEach(function (b) {
        var btn = el('button', 'l-btn' + (b.variant ? ' ' + b.variant : ''), b.label);
        btn.addEventListener('click', function () { if (b.keepOpen !== true) closeOverlay(); b.onClick(); });
        foot.appendChild(btn);
      });
      card.appendChild(foot);
      overlayEl.appendChild(card);
      overlayEl.style.display = '';
    }
    function closeOverlay() { overlayEl.innerHTML = ''; overlayEl.style.display = 'none'; }

    function textRow(label, value, placeholder) {
      var wrap = el('div');
      wrap.appendChild(el('div', 'l-cd-flabel', label));
      var input = el('input', 'l-sbm-nameedit'); input.type = 'text';
      input.style.width = '100%'; input.style.fontWeight = 'normal';
      input.value = value || ''; if (placeholder) input.placeholder = placeholder;
      wrap.appendChild(input);
      return { el: wrap, input: input };
    }

    function colorRow(label, value) {
      var wrap = el('div');
      wrap.appendChild(el('div', 'l-cd-flabel', label));
      var input = el('input', 'l-af-colorbox'); input.type = 'color';
      input.value = /^#[0-9a-fA-F]{6}$/.test(value || '') ? value : '#569cd6';
      wrap.appendChild(input);
      return { el: wrap, input: input };
    }

    // Searchable pick-one list used by the category and parameter pickers.
    function openPicker(title, options, onPick) {
      var body = el('div');
      var search = el('input', 'l-lc-search'); search.type = 'text';
      body.appendChild(search);
      var list = el('div');
      list.style.maxHeight = '320px'; list.style.overflowY = 'auto';
      function renderList(q) {
        list.innerHTML = '';
        q = (q || '').toLowerCase();
        (options || []).forEach(function (o) {
          if (q && o.toLowerCase().indexOf(q) < 0) return;
          var row = el('div', 'l-sbm-row', o);
          row.addEventListener('click', function () { closeOverlay(); onPick(o); });
          list.appendChild(row);
        });
      }
      search.addEventListener('input', function () { renderList(search.value); });
      renderList('');
      body.appendChild(list);
      openOverlay(title, body, []);
      search.focus();
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
      hist.addEventListener('click', openHistoryOverlay);
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
      tmpl.addEventListener('click', openTemplatesOverlay);
      top.appendChild(tmpl);
      side.appendChild(top);

      var list = el('div', 'l-af-tradelist');
      if (!payload.trades || payload.trades.length === 0)
        list.appendChild(el('div', 'l-sbm-empty', L('noTrades')));
      (payload.trades || []).forEach(function (t) {
        var row = el('div', 'l-af-trade' + (t.active ? ' active' : ''));
        var cb = el('input'); cb.type = 'checkbox'; cb.checked = !!t.checked;
        cb.addEventListener('click', function (e) { e.stopPropagation(); });
        cb.addEventListener('change', function () { action('checkTrade', { id: t.id, value: cb.checked }); });
        row.appendChild(cb);
        var dot = el('span', 'dot'); dot.style.background = t.color; row.appendChild(dot);
        row.appendChild(el('span', 'nm', t.label));
        var edit = el('button', 'l-lc-gbtn'); edit.title = L('editTradeTip'); edit.appendChild(svgIcon(PENCIL));
        edit.addEventListener('click', function (e) { e.stopPropagation(); openTradeEditOverlay(t); });
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
      add.addEventListener('click', openAddTradeOverlay);
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

      if (!payload.rules || payload.rules.length === 0)
        list.appendChild(el('div', 'l-sbm-empty', L('noRules')));
      (payload.rules || []).forEach(function (r, idx) {
        var row = el('div', 'l-af-rule' + (r.active ? ' active' : '') + (r.enabled === false ? ' disabled' : ''));
        row.draggable = true;
        var sw = el('span', 'sw'); sw.style.background = r.color; row.appendChild(sw);
        var txt = el('div', 'txt');
        txt.appendChild(el('div', 'nm', r.name));
        txt.appendChild(el('div', 'cat', r.catLabel || ''));
        row.appendChild(txt);
        var edit = el('button', 'l-lc-gbtn'); edit.title = L('editRuleTip'); edit.appendChild(svgIcon(PENCIL));
        edit.addEventListener('click', function (e) { e.stopPropagation(); openRuleEditOverlay(r); });
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
        function (it) { action('removeCategory', { value: it.label }); },
        function () { openPicker(L('category'), r.categoryOptions || [], function (v) { action('addCategory', { value: v }); }); }));

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
        function () { openPicker(L('parameter'), r.parameterOptions || [], function (v) { action('setParameter', { value: v }); }); }));
      card.appendChild(el('div', 'l-af-caption',
        r.parameterBuiltIn ? '\u2713 ' + L('builtInParam') : L('notLinkSafe')));

      card.appendChild(el('div', 'l-af-lbl', L('searchString')));
      card.appendChild(chipRow(
        (r.keywords || []).map(function (k) { return { label: k }; }),
        L('addKeywordTip'),
        function (it) { action('removeKeyword', { value: it.label }); },
        function () { openKeywordOverlay(); }));

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

      // Native color input - WebView2 shows the OS picker; change posts the hex back.
      var color = el('input', 'l-af-colorbox'); color.type = 'color';
      color.value = /^#[0-9a-fA-F]{6}$/.test(o.color || '') ? o.color : '#888888';
      color.title = L('pickColorTip');
      color.addEventListener('change', function () { action('setColor', { layer: key, value: color.value }); });
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

    function visRow(card, key, on, capMain) {
      var row = el('div', 'l-af-vrow');
      var tog = el('button', 'l-af-toggle' + (on ? ' on' : ''), L(key));
      tog.addEventListener('click', function () { action('setFlag', { flag: key, value: !on }); });
      row.appendChild(tog);
      row.appendChild(el('div', 'cap', capMain));
      card.appendChild(row);
    }

    function buildAppearance(r) {
      var card = el('div', 'l-af-card');
      visRow(card, 'halftone', r.halftone, L('halftoneDesc'));
      visRow(card, 'visible', r.visible, L('visibleDesc'));
      visRow(card, 'filterOn', r.filterOn, L('filterOnDesc'));
      return card;
    }

    // -- Overlays ------------------------------------------------------------------
    function openTradeEditOverlay(t) {
      var body = el('div');
      var name = textRow(L('tradeNameLabel'), t.label);
      body.appendChild(name.el);
      body.appendChild(el('div', 'l-af-caption', L('tradeIdLabel') + ' ' + t.id));
      var color = colorRow(L('addTradeColorLabel'), t.color);
      body.appendChild(color.el);
      openOverlay(t.label, body, [
        { label: L('tradeDelete'), variant: 'danger', onClick: function () { action('deleteTrade', { id: t.id }); } },
        { label: L('tradeDuplicate'), onClick: function () { action('duplicateTrade', { id: t.id }); } },
        { label: L('save'), variant: 'primary', onClick: function () {
          action('applyTradeEdit', { id: t.id, label: name.input.value, color: color.input.value }); } }
      ]);
    }

    function openAddTradeOverlay() {
      var body = el('div');
      var name = textRow(L('addTradeNameLabel'), '');
      body.appendChild(name.el);
      var color = colorRow(L('addTradeColorLabel'), '#569cd6');
      body.appendChild(color.el);
      openOverlay('+ ' + L('addTrade'), body, [
        { label: L('addTradeApply'), variant: 'primary', onClick: function () {
          action('addTrade', { label: name.input.value, color: color.input.value }); } }
      ]);
    }

    function openRuleEditOverlay(r) {
      var body = el('div');
      var name = textRow(L('ruleNameLabel'), r.name);
      body.appendChild(name.el);
      openOverlay(r.name, body, [
        { label: L('ruleDelete'), variant: 'danger', onClick: function () { action('deleteRule', { id: r.id }); } },
        { label: L('ruleCopy'), onClick: function () { action('duplicateRule', { id: r.id }); } },
        { label: L('save'), variant: 'primary', onClick: function () {
          action('applyRuleEdit', { id: r.id, name: name.input.value }); } }
      ]);
    }

    function openKeywordOverlay() {
      var body = el('div');
      var kw = textRow(L('searchString'), '', L('addKeywordTip'));
      body.appendChild(kw.el);
      openOverlay(L('searchString'), body, [
        { label: L('save'), variant: 'primary', onClick: function () {
          if (kw.input.value.trim()) action('addKeyword', { value: kw.input.value.trim() }); } }
      ]);
      kw.input.focus();
    }

    function openHistoryOverlay() {
      var body = el('div');
      body.style.maxHeight = '380px'; body.style.overflowY = 'auto';
      (payload.history || []).slice().reverse().forEach(function (h) {
        var row = el('div', 'l-sbm-row' + (h.current ? ' active' : ''));
        row.appendChild(el('span', 'nm', h.label + (h.current ? '  ' + L('historyNow') : '')));
        row.addEventListener('click', function () { closeOverlay(); action('historyJump', { index: h.index }); });
        body.appendChild(row);
      });
      openOverlay(L('historyHeader'), body, []);
    }

    function openTemplatesOverlay() {
      var body = el('div');

      body.appendChild(el('div', 'l-lc-filterhint', L('tmplSavedHeader')));
      if (!payload.templates || payload.templates.length === 0)
        body.appendChild(el('div', 'l-af-caption', L('tmplNone')));
      (payload.templates || []).forEach(function (name) {
        var row = el('div', 'l-sbm-row');
        row.appendChild(el('span', 'nm', name));
        var del = el('button', 'l-lc-gbtn', '\u00d7'); del.title = L('tmplDeleteTip');
        del.addEventListener('click', function (e) { e.stopPropagation(); closeOverlay(); action('templateDelete', { name: name }); });
        row.appendChild(del);
        row.addEventListener('click', function () { closeOverlay(); action('templateLoad', { name: name }); });
        body.appendChild(row);
      });

      body.appendChild(el('div', 'l-lc-filterhint', L('tmplSaveAs')));
      var saveRow = el('div', 'l-sbm-namerow');
      var nameIn = el('input', 'l-sbm-nameedit'); nameIn.type = 'text'; nameIn.style.fontWeight = 'normal'; nameIn.style.flex = '1';
      saveRow.appendChild(nameIn);
      var saveBtn = el('button', 'l-btn', L('save'));
      saveBtn.addEventListener('click', function () {
        if (nameIn.value.trim()) { closeOverlay(); action('templateSave', { name: nameIn.value.trim() }); }
      });
      saveRow.appendChild(saveBtn);
      body.appendChild(saveRow);

      body.appendChild(el('div', 'l-lc-filterhint', L('tmplFileHeader')));
      var fileRow = el('div', 'l-sbm-geomrow');
      var imp = el('button', 'l-btn', L('tmplImport'));
      imp.addEventListener('click', function () { closeOverlay(); action('importFile'); });
      fileRow.appendChild(imp);
      var exp = el('button', 'l-btn', L('tmplExport'));
      exp.addEventListener('click', function () { closeOverlay(); action('exportFile'); });
      fileRow.appendChild(exp);
      body.appendChild(fileRow);

      // Restore defaults - inline confirm (second click within the same overlay applies).
      var restore = el('button', 'l-btn danger wide', L('tmplRestore'));
      restore.style.width = '100%'; restore.style.marginTop = '12px';
      var armed = false;
      restore.addEventListener('click', function () {
        if (!armed) { armed = true; restore.textContent = L('tmplRestoreYes'); return; }
        closeOverlay(); action('restoreDefaults');
      });
      body.appendChild(restore);

      openOverlay(L('templates'), body, []);
    }

    // -- Status pill -------------------------------------------------------------
    function flashStatus(msg) {
      var pill = document.getElementById('l-af-status');
      if (!pill) return;
      pill.textContent = msg;
      pill.style.display = '';
      if (statusTimer) clearTimeout(statusTimer);
      statusTimer = setTimeout(function () { pill.style.display = 'none'; pill.textContent = ''; }, 3500);
    }

    return { render: render };
  }

  window.Lemoine = window.Lemoine || {};
  window.Lemoine.autoFilters = autoFilters;
})();
