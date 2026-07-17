/* Legend Creator library (HTML analogue of LegendSettingsWindow).
   Three-column surface: legend sidebar (templates, legend tabs, Add Legend, Preview /
   Update Legend), the builder (rows of group cards holding legend-entry blocks with
   inline renaming and whole-card drop targets - never sliver drop zones), and the
   settings rail (Sizing, Text Styles, Palette of Auto Filters rules to drag into groups).
   In-page overlays: legend edit popup (title/subtitle/duplicate/delete), templates menu,
   palette trade picker, and a client-side paper preview. Every change posts an action to
   C#, which owns LegendCreatorSettings and the Revit create/update run.
   ASCII-only (rule R13). */
(function () {
  'use strict';

  function legendCreator(opts) {
    opts = opts || {};
    var send = opts.send || function () {};
    var ui = (window.Lemoine && window.Lemoine.ui) || {};
    var el = ui.el || function (t, c, x) { var e = document.createElement(t); if (c) e.className = c; if (x != null) e.textContent = x; return e; };

    var payload = null, root = null, overlayEl = null, statusTimer = null;

    function action(a, extra) { var p = { action: a }; if (extra) for (var k in extra) p[k] = extra[k]; send('action', p); }
    function L(k) { return (payload && payload.labels && payload.labels[k]) || ''; }

    function svgIcon(paths, size) {
      var s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      s.setAttribute('width', size || 13); s.setAttribute('height', size || 13);
      s.setAttribute('viewBox', '0 0 16 16'); s.setAttribute('fill', 'none');
      (Array.isArray(paths) ? paths : [paths]).forEach(function (d) {
        var p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        p.setAttribute('d', d); p.setAttribute('stroke', 'currentColor');
        p.setAttribute('stroke-width', '1.3'); p.setAttribute('stroke-linecap', 'round');
        p.setAttribute('stroke-linejoin', 'round');
        s.appendChild(p);
      });
      return s;
    }
    var PENCIL = 'M11.5 2.5l2 2L5 13l-2.6.6L3 11l8.5-8.5z';
    var EYE = ['M1.5 8s2.5-4.5 6.5-4.5S14.5 8 14.5 8 12 12.5 8 12.5 1.5 8 1.5 8z', 'M8 9.8a1.8 1.8 0 1 0 0-3.6 1.8 1.8 0 0 0 0 3.6z'];

    function render(host, p) { root = host; payload = p; draw(); if (p && p.status) flashStatus(p.status); }

    function draw() {
      root.innerHTML = '';
      root.appendChild(buildToolbar());
      var body = el('div', 'l-lc-body');
      body.appendChild(buildSidebar());
      body.appendChild(buildBuilder());
      body.appendChild(buildRail());
      root.appendChild(body);
      overlayEl = el('div', 'l-sbm-overlay'); overlayEl.style.display = 'none';
      root.appendChild(overlayEl);
      var pill = el('div', 'l-sbm-status'); pill.id = 'l-lc-status'; pill.style.display = 'none';
      root.appendChild(pill);
    }

    // -- Overlay machinery ----------------------------------------------------------
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
        btn.addEventListener('click', function () { closeOverlay(); b.onClick(); });
        foot.appendChild(btn);
      });
      card.appendChild(foot);
      overlayEl.appendChild(card);
      overlayEl.style.display = '';
    }
    function closeOverlay() { overlayEl.innerHTML = ''; overlayEl.style.display = 'none'; }

    function textRow(label, value) {
      var wrap = el('div');
      wrap.appendChild(el('div', 'l-cd-flabel', label));
      var input = el('input', 'l-sbm-nameedit'); input.type = 'text';
      input.style.width = '100%'; input.style.fontWeight = 'normal';
      input.value = value || '';
      wrap.appendChild(input);
      return { el: wrap, input: input };
    }

    // -- Toolbar -----------------------------------------------------------------
    function buildToolbar() {
      var bar = el('div', 'l-set-toolbar');
      var title = el('div', 'title', payload.title || 'Legend Creation');
      title.addEventListener('mousedown', function (e) { if (e.button === 0) action('drag'); });
      bar.appendChild(title);
      var tools = el('div', 'tools');
      var close = el('button', 'win-btn', '\u00d7'); close.title = 'Close';
      close.addEventListener('click', function () { action('close'); });
      tools.appendChild(close);
      bar.appendChild(tools);
      return bar;
    }

    // -- Legend sidebar ------------------------------------------------------------
    function buildSidebar() {
      var side = el('div', 'l-lc-side');

      var top = el('div', 'l-lc-side-top');
      var tmpl = el('button', 'l-btn l-af-templates', L('templates') + ' \u25BE');
      tmpl.addEventListener('click', openTemplatesOverlay);
      top.appendChild(tmpl);
      side.appendChild(top);

      var list = el('div', 'l-lc-tablist');
      (payload.legends || []).forEach(function (t) {
        var row = el('div', 'l-lc-tab' + (t.active ? ' active' : ''));
        row.appendChild(el('span', 'nm', t.name));
        var edit = el('button', 'l-lc-gbtn'); edit.title = L('editLegendTip'); edit.appendChild(svgIcon(PENCIL));
        edit.addEventListener('click', function (e) { e.stopPropagation(); openLegendEditOverlay(t); });
        row.appendChild(edit);
        row.addEventListener('click', function () { if (!t.active) action('selectLegend', { id: t.id }); });
        list.appendChild(row);
      });
      side.appendChild(list);

      var addWrap = el('div', 'l-lc-addtab');
      var add = el('button', 'l-btn', '+  ' + L('addLegend'));
      add.addEventListener('click', function () { action('addLegend'); });
      addWrap.appendChild(add);
      side.appendChild(addWrap);

      var foot = el('div', 'l-lc-side-foot');
      var preview = el('button', 'l-btn', L('preview'));
      preview.addEventListener('click', openPreviewOverlay);
      foot.appendChild(preview);
      var update = el('button', 'l-btn update', (payload.hasRevitView ? L('updateLegend') : L('createLegend')) + ' \u2192');
      update.addEventListener('click', function () { action('createOrUpdate'); });
      foot.appendChild(update);
      side.appendChild(foot);

      return side;
    }

    // -- Builder (centre) ------------------------------------------------------------
    function buildBuilder() {
      var mid = el('div', 'l-lc-mid');
      var scroll = el('div', 'l-lc-builder');

      (payload.rows || []).forEach(function (row) {
        var lane = el('div', 'l-lc-rowlane');
        (row.groups || []).forEach(function (g) { lane.appendChild(buildGroup(g)); });
        scroll.appendChild(lane);
      });
      wireGroupDrag(scroll);
      mid.appendChild(scroll);

      var addWrap = el('div', 'l-lc-addgroup');
      var add = el('button', 'l-btn', '+  ' + L('addGroup'));
      add.addEventListener('click', function () { action('addGroup'); });
      addWrap.appendChild(add);
      mid.appendChild(addWrap);

      var status = el('div', 'l-lc-statusbar');
      status.appendChild(el('span', 'dot', '\u25CF '));
      status.appendChild(el('span', null, payload.statusLine || ''));
      mid.appendChild(status);

      return mid;
    }

    // -- Group drag: ONE live insertion marker over the lane grid (house rule: never
    // sliver drop zones). Aiming inside a lane snaps a vertical marker to the nearest
    // column gutter; aiming at a gap between rows (or above/below all rows) shows a
    // full-width lane marker meaning "new row here". The marker is non-hit-testable,
    // so cards never reflow while aiming.
    var groupMarker = null, groupDropTarget = null;
    var ROW_BAND = 14; // px band at a lane's top/bottom edge that reads as "between rows"

    function hideGroupMarker() {
      if (groupMarker) groupMarker.style.display = 'none';
      groupDropTarget = null;
    }

    function wireGroupDrag(scroll) {
      groupMarker = el('div', 'l-lc-marker');
      groupMarker.style.display = 'none';
      scroll.appendChild(groupMarker);

      function isGroupDrag(ev) {
        var t = ev.dataTransfer && ev.dataTransfer.types;
        return t && Array.prototype.indexOf.call(t, 'text/l-lc-group') >= 0;
      }

      scroll.addEventListener('dragover', function (ev) {
        if (!isGroupDrag(ev)) return;
        ev.preventDefault();
        ev.dataTransfer.dropEffect = 'move';
        var target = computeGroupTarget(scroll, ev.clientX, ev.clientY);
        groupDropTarget = target;
        positionGroupMarker(scroll, target);
      });
      scroll.addEventListener('dragleave', function (ev) {
        if (ev.target === scroll) hideGroupMarker();
      });
      scroll.addEventListener('drop', function (ev) {
        if (!isGroupDrag(ev)) return;
        ev.preventDefault();
        var id = ev.dataTransfer.getData('text/l-lc-group');
        var target = groupDropTarget;
        hideGroupMarker();
        if (id && target)
          action('moveGroup', { id: id, rowIndex: target.rowIndex, colIndex: target.colIndex || 0, newRow: !!target.newRow });
      });
    }

    function computeGroupTarget(scroll, cx, cy) {
      var lanes = scroll.querySelectorAll(':scope > .l-lc-rowlane');
      if (lanes.length === 0) return { newRow: true, rowIndex: 0 };

      for (var i = 0; i < lanes.length; i++) {
        var r = lanes[i].getBoundingClientRect();
        if (cy < r.top + ROW_BAND) return { newRow: true, rowIndex: i };
        if (cy <= r.bottom - ROW_BAND) {
          // Inside lane i: snap to the nearest column gutter.
          var cards = lanes[i].querySelectorAll(':scope > .l-lc-group');
          var best = 0, bestDist = Infinity;
          for (var j = 0; j <= cards.length; j++) {
            var gx = j < cards.length
              ? cards[j].getBoundingClientRect().left
              : (cards.length ? cards[cards.length - 1].getBoundingClientRect().right : r.left);
            var dist = Math.abs(cx - gx);
            if (dist < bestDist) { bestDist = dist; best = j; }
          }
          return { newRow: false, rowIndex: i, colIndex: best };
        }
        if (i === lanes.length - 1 || cy < lanes[i + 1].getBoundingClientRect().top + ROW_BAND) {
          if (cy <= r.bottom + ROW_BAND || i === lanes.length - 1)
            return { newRow: true, rowIndex: i + 1 };
        }
      }
      return { newRow: true, rowIndex: lanes.length };
    }

    function positionGroupMarker(scroll, target) {
      if (!groupMarker || !target) return;
      var sRect = scroll.getBoundingClientRect();
      var lanes = scroll.querySelectorAll(':scope > .l-lc-rowlane');
      function relY(clientY) { return clientY - sRect.top + scroll.scrollTop; }
      function relX(clientX) { return clientX - sRect.left + scroll.scrollLeft; }

      if (target.newRow) {
        var y;
        if (lanes.length === 0) y = 8;
        else if (target.rowIndex <= 0) y = relY(lanes[0].getBoundingClientRect().top) - 8;
        else if (target.rowIndex >= lanes.length) y = relY(lanes[lanes.length - 1].getBoundingClientRect().bottom) + 6;
        else {
          var above = lanes[target.rowIndex - 1].getBoundingClientRect().bottom;
          var below = lanes[target.rowIndex].getBoundingClientRect().top;
          y = relY((above + below) / 2) - 2;
        }
        groupMarker.style.left = '4px';
        groupMarker.style.width = (scroll.clientWidth - 8) + 'px';
        groupMarker.style.top = Math.max(0, y) + 'px';
        groupMarker.style.height = '4px';
      } else {
        var lane = lanes[target.rowIndex];
        if (!lane) { hideGroupMarker(); return; }
        var lr = lane.getBoundingClientRect();
        var cards = lane.querySelectorAll(':scope > .l-lc-group');
        var x;
        if (cards.length === 0) x = relX(lr.left);
        else if (target.colIndex >= cards.length) x = relX(cards[cards.length - 1].getBoundingClientRect().right) + 5;
        else x = relX(cards[target.colIndex].getBoundingClientRect().left) - 9;
        groupMarker.style.left = Math.max(0, x) + 'px';
        groupMarker.style.width = '4px';
        groupMarker.style.top = relY(lr.top) + 'px';
        groupMarker.style.height = lr.height + 'px';
      }
      groupMarker.style.display = '';
    }

    function tint(hex) {
      // Group headers are tinted by the trade colour like the WPF cards - a translucent
      // wash so the header text stays readable on both themes.
      return /^#[0-9a-fA-F]{6}$/.test(hex || '') ? hex + '2e' : 'transparent';
    }

    function buildGroup(g) {
      var card = el('div', 'l-lc-group');

      var head = el('div', 'l-lc-ghead');
      head.style.background = tint(g.color);
      head.draggable = true;
      head.addEventListener('dragstart', function (ev) {
        if (ev.target && (ev.target.tagName === 'INPUT' || (ev.target.closest && ev.target.closest('button')))) {
          ev.preventDefault(); return;
        }
        ev.dataTransfer.setData('text/l-lc-group', g.id);
        ev.dataTransfer.effectAllowed = 'move';
      });
      head.addEventListener('dragend', hideGroupMarker);
      var caret = el('span', 'caret', g.collapsed ? '\u25B8' : '\u25BE');
      caret.title = g.collapsed ? L('expandTip') : L('collapseTip');
      caret.addEventListener('click', function () { action('toggleGroup', { id: g.id, value: !g.collapsed }); });
      head.appendChild(caret);
      var dot = el('span', 'dot'); dot.style.background = g.color || 'transparent'; head.appendChild(dot);

      // Inline-editable group title (change commits, Esc reverts via blur re-render).
      var ttl = el('input', 'ttl l-lc-inline'); ttl.type = 'text'; ttl.value = g.title || '';
      ttl.addEventListener('change', function () {
        if (ttl.value.trim() && ttl.value !== g.title) action('renameGroup', { id: g.id, value: ttl.value.trim() });
      });
      head.appendChild(ttl);

      head.appendChild(el('span', 'cnt', String(g.count != null ? g.count : (g.blocks || []).length)));
      var eye = el('button', 'l-lc-gbtn'); eye.title = L('toggleGroupVisTip'); eye.appendChild(svgIcon(EYE));
      eye.addEventListener('click', function () { action('toggleGroupVisibility', { id: g.id }); });
      head.appendChild(eye);
      var add = el('button', 'l-lc-gbtn', '+'); add.title = L('addEntryTip');
      add.addEventListener('click', function () { action('addBlock', { groupId: g.id }); });
      head.appendChild(add);
      var del = el('button', 'l-lc-gbtn', '\u00d7'); del.title = L('deleteGroupTip');
      del.addEventListener('click', function () { action('deleteGroup', { id: g.id }); });
      head.appendChild(del);
      card.appendChild(head);

      if (!g.collapsed) {
        var body = el('div', 'l-lc-gbody');
        if (!g.blocks || g.blocks.length === 0)
          body.appendChild(el('div', 'l-sbm-emptynote', L('dropHint')));
        (g.blocks || []).forEach(function (b) { body.appendChild(buildBlock(g, b)); });
        card.appendChild(body);
      }

      // Palette-chip drop target: the whole card highlights while a filter drag hovers it.
      card.addEventListener('dragover', function (ev) {
        if (ev.dataTransfer.types.indexOf('text/l-lc-filter') >= 0) {
          ev.preventDefault(); ev.dataTransfer.dropEffect = 'copy';
          card.classList.add('drop-hint');
        }
      });
      card.addEventListener('dragleave', function () { card.classList.remove('drop-hint'); });
      card.addEventListener('drop', function (ev) {
        ev.preventDefault(); card.classList.remove('drop-hint');
        var key = ev.dataTransfer.getData('text/l-lc-filter');
        if (key) action('dropFilter', { groupId: g.id, key: key });
      });

      return card;
    }

    function buildBlock(g, b) {
      var row = el('div', 'l-lc-block' + (b.visible === false ? ' hidden-entry' : ''));
      var sw = el('span', 'sw'); sw.style.background = b.color; row.appendChild(sw);

      var nm = el('input', 'nm l-lc-inline'); nm.type = 'text'; nm.value = b.name || '';
      nm.addEventListener('change', function () {
        if (nm.value.trim() && nm.value !== b.name) action('renameBlock', { groupId: g.id, id: b.id, value: nm.value.trim() });
      });
      row.appendChild(nm);

      var eye = el('button', 'l-lc-gbtn'); eye.title = L('toggleEntryVisTip'); eye.appendChild(svgIcon(EYE));
      eye.addEventListener('click', function () { action('toggleBlockVisibility', { groupId: g.id, id: b.id }); });
      row.appendChild(eye);
      var del = el('button', 'l-lc-gbtn', '\u00d7'); del.title = L('deleteEntryTip');
      del.addEventListener('click', function () { action('deleteBlock', { groupId: g.id, id: b.id }); });
      row.appendChild(del);
      return row;
    }

    // -- Settings rail (right) ------------------------------------------------------
    function buildRail() {
      var rail = el('div', 'l-lc-rail');
      rail.appendChild(el('div', 'l-lc-sechead', L('sizing')));
      rail.appendChild(buildSizing());
      rail.appendChild(el('div', 'l-lc-sechead', L('textStyles')));
      rail.appendChild(buildTextStyles());
      rail.appendChild(el('div', 'l-lc-sechead', L('palette')));
      rail.appendChild(buildPalette());
      return rail;
    }

    function frow(grid, label, control) {
      var row = el('div', 'l-lc-frow');
      row.appendChild(el('span', 'lbl', label));
      row.appendChild(control);
      grid.appendChild(row);
    }

    function buildSizing() {
      var card = el('div', 'l-lc-card');
      var s = payload.sizing || {};

      frow(card, L('scale'), ui.dropdown({
        value: s.scale, options: (s.scaleOptions || []).map(function (o) { return { value: o, label: o }; }),
        onChange: function (v) { action('setSizing', { field: 'scale', value: v }); }
      }).el);

      [['swatchW', 'SwatchW'], ['swatchH', 'SwatchH'], ['rowGap', 'RowGap'], ['colGap', 'ColGap'], ['swatchLabel', 'SwatchLabelGap']]
        .forEach(function (f) {
          frow(card, L(f[0]), ui.stepper({
            value: s[f[0]], min: 0, max: 5, step: 0.01, decimals: 2,
            onChange: function (v) { action('setSizing', { field: f[1], value: v }); }
          }).el);
        });
      return card;
    }

    function buildTextStyles() {
      var card = el('div', 'l-lc-card');
      var t = payload.textStyles || {};
      if (!t.options || t.options.length === 0) {
        card.appendChild(el('div', 'l-sbm-help', L('noTypes')));
        return card;
      }
      [['title', 'Title'], ['subtitle', 'Subtitle'], ['groupHeader', 'GroupHeader'], ['label', 'Label']]
        .forEach(function (f) {
          frow(card, L(f[0]), ui.dropdown({
            value: t[f[0]], options: (t.options || []).map(function (o) { return { value: o, label: o }; }),
            onChange: function (v) { action('setTextStyle', { role: f[1], value: v }); }
          }).el);
        });
      return card;
    }

    function buildPalette() {
      var card = el('div', 'l-lc-card');
      var p = payload.palette || {};

      var head = el('div', 'l-lc-palettehead');
      var all = el('button', 'l-btn primary', L('paletteAll'));
      all.addEventListener('click', function () { action('setPaletteTrade', { value: '' }); });
      head.appendChild(all);
      var trade = el('button', 'l-btn', (p.tradeLabel || L('allTrades')) + ' \u25BE');
      trade.addEventListener('click', function () { openTradePickerOverlay(p); });
      head.appendChild(trade);
      card.appendChild(head);

      card.appendChild(el('div', 'l-lc-filterhint', L('filtersHint')));

      // Improvement over WPF: a search box narrows the palette (large projects carry
      // dozens of filter rules; scrolling the whole list to find one was the pain point).
      var search = el('input', 'l-lc-search'); search.type = 'text'; search.placeholder = L('searchFilters');
      card.appendChild(search);

      var listWrap = el('div');
      function renderChips(filterText) {
        listWrap.innerHTML = '';
        var q = (filterText || '').toLowerCase();
        var shown = 0;
        (p.chips || []).forEach(function (c) {
          if (q && c.name.toLowerCase().indexOf(q) < 0 && (c.tradeLabel || '').toLowerCase().indexOf(q) < 0) return;
          shown++;
          var chip = el('div', 'l-lc-fchip');
          chip.draggable = true;
          var sw = el('span', 'sw'); sw.style.background = c.color; chip.appendChild(sw);
          var txt = el('div', 'txt');
          txt.appendChild(el('div', 'nm', c.name));
          txt.appendChild(el('div', 'sub', c.tradeLabel || ''));
          chip.appendChild(txt);
          chip.addEventListener('dragstart', function (ev) {
            ev.dataTransfer.setData('text/l-lc-filter', c.key);
            ev.dataTransfer.effectAllowed = 'copy';
            chip.classList.add('dragging');
          });
          chip.addEventListener('dragend', function () { chip.classList.remove('dragging'); });
          listWrap.appendChild(chip);
        });
        if (shown === 0) listWrap.appendChild(el('div', 'l-sbm-emptynote', L('noMatches')));
      }
      search.addEventListener('input', function () { renderChips(search.value); });
      renderChips('');
      card.appendChild(listWrap);

      return card;
    }

    // -- Overlays ------------------------------------------------------------------
    function openLegendEditOverlay(t) {
      var body = el('div');
      var title = textRow(L('editTitleLabel'), t.legendTitle || t.name);
      body.appendChild(title.el);
      var sub = textRow(L('editSubtitleLabel'), t.legendSubtitle || '');
      body.appendChild(sub.el);
      openOverlay(t.name, body, [
        { label: L('editDelete'), variant: 'danger', onClick: function () { action('deleteLegend', { id: t.id }); } },
        { label: L('editDuplicate'), onClick: function () { action('duplicateLegend', { id: t.id }); } },
        { label: L('editSave'), variant: 'primary', onClick: function () {
          action('applyLegendEdit', { id: t.id, title: title.input.value, subtitle: sub.input.value }); } }
      ]);
    }

    function openTradePickerOverlay(p) {
      var body = el('div');
      var allRow = el('div', 'l-sbm-row');
      allRow.appendChild(el('span', 'nm', L('allTrades')));
      allRow.addEventListener('click', function () { closeOverlay(); action('setPaletteTrade', { value: '' }); });
      body.appendChild(allRow);
      (p.trades || []).forEach(function (t) {
        var row = el('div', 'l-sbm-row');
        var dot = el('span', 'dot'); dot.style.cssText = 'width:11px;height:11px;border-radius:3px;background:' + t.color + ';flex-shrink:0;';
        row.appendChild(dot);
        row.appendChild(el('span', 'nm', t.label));
        row.addEventListener('click', function () { closeOverlay(); action('setPaletteTrade', { value: t.id }); });
        body.appendChild(row);
      });
      openOverlay(L('palette'), body, []);
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
      var saveBtn = el('button', 'l-btn', L('tmplSave'));
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

      openOverlay(L('templates'), body, []);
    }

    // Client-side paper preview: a white sheet with the title/subtitle and each row's
    // groups side by side (header + swatch/label lines). An approximation of the WPF
    // preview - the authoritative output is the Revit legend itself.
    function openPreviewOverlay() {
      var page = el('div', 'l-lc-previewpage');
      var active = null;
      (payload.legends || []).forEach(function (t) { if (t.active) active = t; });
      page.appendChild(el('div', 'pv-title', (active && active.legendTitle) || (active && active.name) || ''));
      if (active && active.legendSubtitle) page.appendChild(el('div', 'pv-sub', active.legendSubtitle));

      (payload.rows || []).forEach(function (row) {
        var lane = el('div', 'pv-lane');
        (row.groups || []).forEach(function (g) {
          var col = el('div', 'pv-group');
          col.appendChild(el('div', 'pv-ghead', g.title));
          (g.blocks || []).forEach(function (b) {
            if (b.visible === false) return;
            var line = el('div', 'pv-line');
            var sw = el('span', 'pv-sw'); sw.style.background = b.color;
            line.appendChild(sw);
            line.appendChild(el('span', 'pv-lbl', b.name));
            col.appendChild(line);
          });
          lane.appendChild(col);
        });
        page.appendChild(lane);
      });

      openOverlay(L('preview'), page, []);
    }

    // -- Status pill -------------------------------------------------------------
    function flashStatus(msg) {
      var pill = document.getElementById('l-lc-status');
      if (!pill) return;
      pill.textContent = msg;
      pill.style.display = '';
      if (statusTimer) clearTimeout(statusTimer);
      statusTimer = setTimeout(function () { pill.style.display = 'none'; pill.textContent = ''; }, 3500);
    }

    return { render: render };
  }

  window.Lemoine = window.Lemoine || {};
  window.Lemoine.legendCreator = legendCreator;
})();
