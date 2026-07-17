/* Legend Creator library (HTML analogue of LegendSettingsWindow).
   Three-column surface: legend sidebar (templates, legend tabs, Add Legend, Preview /
   Update Legend), the builder (rows of group cards holding legend-entry blocks, with a
   single live insertion marker for drags - never sliver drop zones), and the settings
   rail (Sizing, Text Styles, Palette of Auto Filters rules to drag into groups).
   Every change posts an action to C#, which owns LegendCreatorSettings and the Revit
   create/update run. ASCII-only (rule R13). */
(function () {
  'use strict';

  function legendCreator(opts) {
    opts = opts || {};
    var send = opts.send || function () {};
    var ui = (window.Lemoine && window.Lemoine.ui) || {};
    var el = ui.el || function (t, c, x) { var e = document.createElement(t); if (c) e.className = c; if (x != null) e.textContent = x; return e; };

    var payload = null, root = null;

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

    function render(host, p) { root = host; payload = p; draw(); }

    function draw() {
      root.innerHTML = '';
      root.appendChild(buildToolbar());
      var body = el('div', 'l-lc-body');
      body.appendChild(buildSidebar());
      body.appendChild(buildBuilder());
      body.appendChild(buildRail());
      root.appendChild(body);
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
      tmpl.addEventListener('click', function () { action('templates'); });
      top.appendChild(tmpl);
      side.appendChild(top);

      var list = el('div', 'l-lc-tablist');
      (payload.legends || []).forEach(function (t) {
        var row = el('div', 'l-lc-tab' + (t.active ? ' active' : ''));
        row.appendChild(el('span', 'nm', t.name));
        var edit = el('button', 'l-lc-gbtn'); edit.title = L('editLegendTip'); edit.appendChild(svgIcon(PENCIL));
        edit.addEventListener('click', function (e) { e.stopPropagation(); action('editLegend', { id: t.id }); });
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
      preview.addEventListener('click', function () { action('preview'); });
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

      (payload.rows || []).forEach(function (row, rowIdx) {
        var lane = el('div', 'l-lc-rowlane');
        (row.groups || []).forEach(function (g) { lane.appendChild(buildGroup(g, rowIdx)); });
        scroll.appendChild(lane);
      });
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

    function tint(hex) {
      // Group headers are tinted by the trade colour like the WPF cards - a translucent
      // wash so the header text stays readable on both themes.
      return /^#[0-9a-fA-F]{6}$/.test(hex || '') ? hex + '2e' : 'transparent';
    }

    function buildGroup(g, rowIdx) {
      var card = el('div', 'l-lc-group');

      var head = el('div', 'l-lc-ghead');
      head.style.background = tint(g.color);
      var caret = el('span', 'caret', g.collapsed ? '\u25B8' : '\u25BE');
      caret.addEventListener('click', function () { action('toggleGroup', { id: g.id, value: !g.collapsed }); });
      head.appendChild(caret);
      var dot = el('span', 'dot'); dot.style.background = g.color || 'transparent'; head.appendChild(dot);
      head.appendChild(el('span', 'ttl', g.title));
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
      row.appendChild(el('span', 'nm', b.name));
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
            value: s[f[0]], min: 0.01, max: 5, step: 0.01, decimals: 2,
            onChange: function (v) { action('setSizing', { field: f[1], value: v }); }
          }).el);
        });
      return card;
    }

    function buildTextStyles() {
      var card = el('div', 'l-lc-card');
      var t = payload.textStyles || {};
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
      trade.addEventListener('click', function () { action('pickPaletteTrade'); });
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
        (p.chips || []).forEach(function (c) {
          if (q && c.name.toLowerCase().indexOf(q) < 0 && (c.tradeLabel || '').toLowerCase().indexOf(q) < 0) return;
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
      }
      search.addEventListener('input', function () { renderChips(search.value); });
      renderChips('');
      card.appendChild(listWrap);

      return card;
    }

    return { render: render };
  }

  window.Lemoine = window.Lemoine || {};
  window.Lemoine.legendCreator = legendCreator;
})();
