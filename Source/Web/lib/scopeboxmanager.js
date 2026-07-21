/* Scope Box Manager library (HTML analogue of ScopeBoxManagerWindow).
   Sidebar of scope boxes (filter + usage + bulk rename/delete) and a per-box editor (inline
   rename, size, duplicate/delete/bind/split, views + datums sections), plus in-page modal
   overlays for assign-views (browser tree), assign-datums (tabs), bind-sides, split, rename
   (token input) and delete-confirm. Every mutation posts an action to C#, which runs it on
   Revit's main thread and re-sends the refreshed scan. ASCII-only (rule R13). */
(function () {
  'use strict';

  function scopeBoxManager(opts) {
    opts = opts || {};
    var send = opts.send || function () {};
    var ui = (window.Lemoine && window.Lemoine.ui) || {};
    var el = ui.el || function (t, c, x) { var e = document.createElement(t); if (c) e.className = c; if (x != null) e.textContent = x; return e; };

    var payload = null, root = null, overlayEl = null, statusTimer = null;

    function action(a, extra) { var p = { action: a }; if (extra) for (var k in extra) p[k] = extra[k]; send('action', p); }
    function L(k) { return (payload && payload.labels && payload.labels[k]) || ''; }

    function render(host, p) { root = host; payload = p; draw(); if (p && p.status) flashStatus(p.status); }

    function draw() {
      root.innerHTML = '';
      root.appendChild(buildToolbar());
      var body = el('div', 'l-sbm-body');
      body.appendChild(buildSidebar());
      body.appendChild(buildEditor());
      root.appendChild(body);
      overlayEl = el('div', 'l-sbm-overlay'); overlayEl.style.display = 'none';
      root.appendChild(overlayEl);
      var pill = el('div', 'l-sbm-status'); pill.id = 'l-sbm-status'; pill.style.display = 'none';
      root.appendChild(pill);
    }

    // -- Toolbar -----------------------------------------------------------------
    function buildToolbar() {
      var bar = el('div', 'l-set-toolbar');
      var title = el('div', 'title', payload.title || 'Scope Box Manager');
      title.addEventListener('mousedown', function (e) { if (e.button === 0) action('drag'); });
      bar.appendChild(title);
      var tools = el('div', 'tools');
      var refresh = el('button', 'l-btn', L('refresh')); refresh.title = L('refreshTip');
      refresh.style.marginRight = '10px';
      refresh.addEventListener('click', function () { action('refresh'); });
      tools.appendChild(refresh);
      [['\u25A1', 'maximize', 'Maximize'], ['\u00d7', 'close', 'Close']]
        .forEach(function (b) { var x = el('button', 'win-btn', b[0]); x.title = b[2]; x.addEventListener('click', function () { action(b[1]); }); tools.appendChild(x); });
      bar.appendChild(tools);
      return bar;
    }

    // -- Sidebar -----------------------------------------------------------------
    function buildSidebar() {
      var side = el('div', 'l-sbm-side');

      var top = el('div', 'l-sbm-sidehead');
      top.appendChild(el('div', 'l-sbm-sidetitle', payload.sideHeader || ''));
      var chips = el('div', 'l-sbm-chips');
      [['All', L('filterAll')], ['Used', L('filterUsed')], ['Unused', L('filterUnused')]].forEach(function (f) {
        var b = el('button', 'l-btn' + (payload.filterMode === f[0] ? ' primary' : ''), f[1]);
        b.addEventListener('click', function () { if (payload.filterMode !== f[0]) action('setFilter', { value: f[0] }); });
        chips.appendChild(b);
      });
      top.appendChild(chips);
      side.appendChild(top);

      var list = el('div', 'l-sbm-list');
      if (payload.empty) {
        list.appendChild(el('div', 'l-sbm-empty', L('emptyBoxes')));
      } else {
        (payload.boxes || []).forEach(function (b) { list.appendChild(boxRow(b)); });
      }
      side.appendChild(list);

      var foot = el('div', 'l-sbm-foot');
      var rename = el('button', 'l-btn wide', L('renameChecked'));
      rename.addEventListener('click', function () {
        if ((payload.renameCount || 0) === 0) { flashStatus(L('nothingChecked')); return; }
        openRenameOverlay();
      });
      foot.appendChild(rename);
      var delUnused = el('button', 'l-btn wide danger', L('deleteUnused'));
      delUnused.addEventListener('click', function () {
        if ((payload.unusedCount || 0) === 0) { flashStatus(L('noUnused')); return; }
        openDeleteUnusedOverlay();
      });
      foot.appendChild(delUnused);
      foot.appendChild(el('div', 'l-sbm-footnote', L('footNote')));
      side.appendChild(foot);

      return side;
    }

    function boxRow(b) {
      var row = el('div', 'l-sbm-row' + (b.active ? ' active' : ''));
      var cb = el('input'); cb.type = 'checkbox'; cb.checked = !!b.checked;
      cb.addEventListener('click', function (e) { e.stopPropagation(); });
      cb.addEventListener('change', function () { action('checkBox', { id: b.id, value: cb.checked }); });
      row.appendChild(cb);
      row.appendChild(el('span', 'nm', b.name));
      if (b.unused) { var badge = el('span', 'l-sbm-badge', L('unusedBadge')); row.appendChild(badge); }
      else { row.appendChild(el('span', 'use', b.usage)); }
      row.addEventListener('click', function () { if (!b.active) action('selectBox', { id: b.id }); });
      return row;
    }

    // -- Editor ------------------------------------------------------------------
    function buildEditor() {
      var ed = el('div', 'l-sbm-editor');
      var e = payload.editor;
      if (!e) { ed.appendChild(el('div', 'l-sbm-noselect', L('noSelection'))); return ed; }

      // Name / size / actions card
      var top = el('div', 'l-cd-card');
      var head = el('div', 'l-sbm-namerow');
      var nameIn = el('input', 'l-sbm-nameedit'); nameIn.type = 'text'; nameIn.value = e.name || '';
      nameIn.addEventListener('change', function () {
        var v = nameIn.value.trim();
        if (!v || v === e.name) { nameIn.value = e.name; return; }
        action('renameInline', { id: e.id, value: v });
      });
      head.appendChild(nameIn);
      head.appendChild(el('span', 'l-sbm-size', e.size));
      var dup = el('button', 'l-btn', L('duplicateBox'));
      dup.addEventListener('click', function () { action('duplicate', { id: e.id }); });
      head.appendChild(dup);
      var del = el('button', 'l-btn danger', L('deleteBox'));
      del.addEventListener('click', function () { openDeleteBoxOverlay(e); });
      head.appendChild(del);
      top.appendChild(head);

      var geom = el('div', 'l-sbm-geomrow');
      var bind = el('button', 'l-btn', L('bindSides'));
      bind.addEventListener('click', function () { openBindOverlay(e); });
      geom.appendChild(bind);
      var split = el('button', 'l-btn', L('splitBox'));
      split.addEventListener('click', function () { openSplitOverlay(e); });
      geom.appendChild(split);
      top.appendChild(geom);
      ed.appendChild(top);

      // Views section
      ed.appendChild(section(e.viewsHeader,
        [[L('assignViews'), 'primary', function () { openAssignViewsOverlay(e); }],
         [L('clearChecked'), '', function () { clearChecked(e, 'view'); }]],
        e.views, 'view', L('viewsEmpty'), L('viewsHint')));

      // Datums section
      ed.appendChild(section(e.datumsHeader,
        [[L('assignDatums'), 'primary', function () { openAssignDatumsOverlay(e); }],
         [L('clearChecked'), '', function () { clearChecked(e, 'datum'); }]],
        e.datums, 'datum', L('datumsEmpty'), L('datumsHint')));

      return ed;
    }

    function section(header, actions, rows, kind, emptyLabel, hint) {
      var card = el('div', 'l-cd-card');
      var hd = el('div', 'l-sbm-sechead');
      hd.appendChild(el('div', 'l-sbm-sectitle', header));
      var btns = el('div', 'l-sbm-secbtns');
      actions.forEach(function (a) {
        var b = el('button', 'l-btn' + (a[1] ? ' ' + a[1] : ''), a[0]);
        b.addEventListener('click', a[2]);
        btns.appendChild(b);
      });
      hd.appendChild(btns);
      card.appendChild(hd);

      if (!rows || rows.length === 0) {
        card.appendChild(el('div', 'l-sbm-emptynote', emptyLabel));
      } else {
        var listBox = el('div', 'l-sbm-reflist');
        rows.forEach(function (r) {
          var row = el('div', 'l-sbm-refrow');
          var cb = el('input', 'l-sbm-' + kind + 'check'); cb.type = 'checkbox'; cb.checked = !!r.checked;
          cb.setAttribute('data-id', r.id);
          cb.addEventListener('change', function () { action('check' + (kind === 'view' ? 'View' : 'Datum'), { id: r.id, value: cb.checked }); });
          row.appendChild(cb);
          row.appendChild(el('span', 'nm', r.name));
          row.appendChild(el('span', 'tag', r.tag));
          listBox.appendChild(row);
        });
        card.appendChild(listBox);
      }
      card.appendChild(el('div', 'l-sbm-emptynote', hint));
      return card;
    }

    // "Clear from checked": keep the rows whose checkbox is NOT checked, re-set the box's list.
    function clearChecked(e, kind) {
      var rows = kind === 'view' ? (e.views || []) : (e.datums || []);
      var boxSel = '.l-sbm-' + kind + 'check';
      var checkedIds = {};
      root.querySelectorAll(boxSel).forEach(function (cb) { if (cb.checked) checkedIds[cb.getAttribute('data-id')] = true; });
      if (Object.keys(checkedIds).length === 0) { flashStatus(L('nothingChecked')); return; }
      var keep = rows.filter(function (r) { return !checkedIds[r.id]; }).map(function (r) { return r.id; });
      action(kind === 'view' ? 'setViews' : 'setDatums', { boxId: e.id, ids: keep });
    }

    // -- Overlays ----------------------------------------------------------------
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
      var spacer = el('div', 'l-sbm-ospacer'); foot.appendChild(spacer);
      (footerBtns || []).forEach(function (b) {
        var btn = el('button', 'l-btn' + (b.variant ? ' ' + b.variant : ''), b.label);
        btn.addEventListener('click', function () { closeOverlay(); b.onClick(); });
        foot.appendChild(btn);
      });
      card.appendChild(foot);
      overlayEl.appendChild(card);
      overlayEl.style.display = '';
    }

    function closeOverlay() { overlayEl.innerHTML = ''; overlayEl.style.display = 'none'; }

    function openAssignViewsOverlay(e) {
      if (!e.tree) return;
      var picker = ui.browserTree({ roots: e.tree.roots, selected: e.tree.selected, singleSelect: false, onChange: function () {} });
      openOverlay(e.assignViewsTitle, picker.el, [{ label: L('applyAssign'), variant: 'primary', onClick: function () {
        action('setViews', { boxId: e.id, ids: picker.getSelected() });
      } }]);
    }

    function openAssignDatumsOverlay(e) {
      if (e.assignDatumsNone || !e.assignDatums) {
        openOverlay(e.assignDatumsTitle, el('div', 'l-sbm-help', L('assignDatumsNone')),
          [{ label: L('cancel'), variant: '', onClick: closeOverlay }]);
        return;
      }
      var body = el('div');
      body.appendChild(el('div', 'l-sbm-help', L('assignDatumsHelp')));
      var sel = (e.assignDatums.selected || []).slice();
      var tabs = ui.multiSelectTabs({ groups: e.assignDatums.groups, selected: sel, height: '300px',
        onChange: function (s) { sel = s; } });
      body.appendChild(tabs.el);
      openOverlay(e.assignDatumsTitle, body, [{ label: L('applyAssign'), variant: 'primary', onClick: function () {
        var map = e.assignDatumsMap || {};
        var ids = sel.map(function (k) { return map[k]; }).filter(function (x) { return x != null; });
        action('setDatums', { boxId: e.id, ids: ids });
      } }]);
    }

    function openBindOverlay(e) {
      var body = el('div');
      body.appendChild(el('div', 'l-sbm-help', L('bindNote')));
      var pick = { north: '', south: '', east: '', west: '' };
      function sideRow(key, label, grids) {
        body.appendChild(el('div', 'l-cd-flabel', label));
        var opts = [{ value: '', label: L('sideKeep') }].concat(grids.map(function (g) { return { value: g.id, label: g.name }; }));
        body.appendChild(ui.singleSelect({ value: '', options: opts, onChange: function (v) { pick[key] = v; } }).el);
      }
      sideRow('north', L('sideNorth'), e.bindHorizontal || []);
      sideRow('south', L('sideSouth'), e.bindHorizontal || []);
      sideRow('east', L('sideEast'), e.bindVertical || []);
      sideRow('west', L('sideWest'), e.bindVertical || []);
      openOverlay(e.bindTitle, body, [{ label: L('applyBind'), variant: 'primary', onClick: function () {
        action('bindSides', { boxId: e.id, north: pick.north, south: pick.south, east: pick.east, west: pick.west });
      } }]);
    }

    function openSplitOverlay(e) {
      var body = el('div');
      body.appendChild(el('div', 'l-sbm-help', L('splitNote')));
      var crossing = e.crossingGrids || [];
      var mode = e.hasCrossingGrids ? 'Gridline' : 'Middle';
      var gridId = crossing.length ? crossing[0].id : '';
      var axis = 'NS', overlap = 0, deleteOriginal = true;

      body.appendChild(ui.singleSelect({
        value: mode === 'Gridline' ? L('splitModeGrid') : L('splitModeMiddle'),
        options: [{ value: L('splitModeGrid'), label: L('splitModeGrid') }, { value: L('splitModeMiddle'), label: L('splitModeMiddle') }],
        onChange: function (v) { mode = v === L('splitModeMiddle') ? 'Middle' : 'Gridline'; rebuildSub(); }
      }).el);

      var sub = el('div', 'l-sbm-splitsub'); body.appendChild(sub);
      function rebuildSub() {
        sub.innerHTML = '';
        if (mode === 'Gridline') {
          if (crossing.length === 0) { sub.appendChild(el('div', 'l-sbm-emptynote', L('splitNoGrid'))); return; }
          sub.appendChild(el('div', 'l-cd-flabel', L('splitGridLabel')));
          sub.appendChild(ui.singleSelect({
            value: (crossing.filter(function (g) { return g.id === gridId; })[0] || crossing[0]).name,
            options: crossing.map(function (g) { return { value: g.id, label: g.name }; }),
            onChange: function (v) { gridId = v; }
          }).el);
        } else {
          sub.appendChild(el('div', 'l-cd-flabel', L('splitAxisLabel')));
          sub.appendChild(ui.singleSelect({
            value: axis === 'EW' ? L('splitAxisEW') : L('splitAxisNS'),
            options: [{ value: 'NS', label: L('splitAxisNS') }, { value: 'EW', label: L('splitAxisEW') }],
            onChange: function (v) { axis = v; }
          }).el);
        }
      }
      rebuildSub();

      body.appendChild(el('div', 'l-cd-flabel', L('splitOverlap')));
      body.appendChild(ui.stepper({ value: 0, min: 0, max: 100, step: 1, decimals: 1, onChange: function (v) { overlap = v; } }).el);

      var delRow = el('label', 'l-sbm-checkrow');
      var delCb = el('input'); delCb.type = 'checkbox'; delCb.checked = true;
      delCb.addEventListener('change', function () { deleteOriginal = delCb.checked; });
      delRow.appendChild(delCb); delRow.appendChild(el('span', null, L('splitDeleteOriginal')));
      body.appendChild(delRow);

      openOverlay(e.splitTitle, body, [{ label: L('applySplit'), variant: 'primary', onClick: function () {
        action('split', { boxId: e.id, mode: mode, gridId: mode === 'Gridline' ? gridId : '', axis: axis, overlap: overlap, deleteOriginal: deleteOriginal });
      } }]);
    }

    function openRenameOverlay() {
      var t = payload.renameTokenInput || { value: '', defaultPattern: '', groups: [], sample: {} };
      var body = el('div');
      var ti = ui.tokenInput({ value: t.value, defaultPattern: t.defaultPattern, groups: t.groups, sample: t.sample,
        onChange: function (v) { action('setRenamePattern', { value: v }); } });
      body.appendChild(ti.el);
      openOverlay(payload.renameTitle, body, [{ label: L('applyRename'), variant: 'primary', onClick: function () {
        action('applyRename', { value: ti.getValue() });
      } }]);
    }

    function openDeleteBoxOverlay(e) {
      openOverlay(e.deleteBoxTitle, el('div', 'l-sbm-help', e.deleteBoxLine),
        [{ label: L('applyDelete'), variant: 'danger', onClick: function () { action('delete', { ids: [e.id] }); } }]);
    }

    function openDeleteUnusedOverlay() {
      openOverlay(payload.deleteUnusedTitle, el('div', 'l-sbm-help', ''),
        [{ label: L('applyDelete'), variant: 'danger', onClick: function () { action('delete', { ids: payload.unusedIds || [] }); } }]);
    }

    // -- Status pill -------------------------------------------------------------
    function flashStatus(msg) {
      var pill = document.getElementById('l-sbm-status');
      if (!pill) return;
      pill.textContent = msg;
      pill.style.display = '';
      if (statusTimer) clearTimeout(statusTimer);
      statusTimer = setTimeout(function () { pill.style.display = 'none'; pill.textContent = ''; }, 3500);
    }

    return { render: render };
  }

  window.Lemoine = window.Lemoine || {};
  window.Lemoine.scopeBoxManager = scopeBoxManager;
})();
