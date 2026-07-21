// =============================================================================
// lemoine.js - the shared WebView2 component library (Phase 1, rule R22).
//
// Dependency-free vanilla factories, one per house input, seeded from the proven
// InlineStepper / MultiSelectTabs recreations. Each factory returns an object
// whose `el` is the root DOM node plus a small get/set API and fires an onChange
// callback. The WPF behavioural contracts (CLAUDE.md) are ported verbatim, not
// just the look (rule R23) - see multiSelectTabs for SetGroups-fires-once,
// SingleSelect, Hierarchy carets/indeterminate, and DisabledItems.
//
// Styling is entirely via lemoine.css (--l-* variables). No inline colours here.
// =============================================================================
window.Lemoine = window.Lemoine || {};
Lemoine.ui = (function () {

  // ── DOM helper ────────────────────────────────────────────────────────────
  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  // ── Button ────────────────────────────────────────────────────────────────
  // opts: { label, variant('primary'|'danger'|'ghost'|null), disabled, onClick }
  function button(opts) {
    opts = opts || {};
    var b = el('button', 'l-btn' + (opts.variant ? ' ' + opts.variant : ''), opts.label || '');
    b.type = 'button';
    if (opts.disabled) b.disabled = true;
    if (opts.onClick) b.addEventListener('click', function () { opts.onClick(); });
    return { el: b, setDisabled: function (d) { b.disabled = !!d; } };
  }

  // ── InlineStepper ─────────────────────────────────────────────────────────
  // opts: { min, max, step, decimals, value, onChange(value) }
  function stepper(opts) {
    opts = opts || {};
    var min = num(opts.min, 0), max = num(opts.max, 100), step = num(opts.step, 1),
        dec = opts.decimals != null ? opts.decimals : 0, val = num(opts.value, min);

    var root  = el('span', 'l-stepper');
    var minus = el('button', null, '\u2212'); minus.type = 'button';   // MINUS SIGN
    var s1    = el('span', 'sep');
    var input = el('input');
    var s2    = el('span', 'sep');
    var plus  = el('button', null, '+'); plus.type = 'button';
    input.setAttribute('spellcheck', 'false');
    root.appendChild(minus); root.appendChild(s1); root.appendChild(input);
    root.appendChild(s2); root.appendChild(plus);

    function fmt(v) { return v.toFixed(Math.max(0, dec)); }
    function commit(v) {
      if (isNaN(v)) { input.value = fmt(val); return; }               // revert unparseable input
      var c = Math.min(max, Math.max(min, v));
      c = parseFloat(c.toFixed(Math.max(0, dec)));
      var changed = Math.abs(c - val) > 1e-9;
      val = c; input.value = fmt(val);
      if (changed && opts.onChange) opts.onChange(val);
    }
    input.value = fmt(val);
    minus.addEventListener('click', function () { commit(val - step); });
    plus .addEventListener('click', function () { commit(val + step); });
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter')  { commit(parseFloat(input.value)); input.blur(); }
      if (e.key === 'Escape') { input.value = fmt(val); input.blur(); }
    });
    input.addEventListener('blur', function () { commit(parseFloat(input.value)); });

    return { el: root, getValue: function () { return val; },
             setValue: function (v) { commit(num(v, val)); } };
  }

  // ── TextField ─────────────────────────────────────────────────────────────
  // opts: { label, value, placeholder, multiline, onChange(value) }
  function textField(opts) {
    opts = opts || {};
    var root = el('label', 'l-field');
    if (opts.label) root.appendChild(el('span', 'l-sublabel', opts.label));
    var input = opts.multiline ? el('textarea') : el('input');
    if (opts.placeholder) input.setAttribute('placeholder', opts.placeholder);
    input.value = opts.value != null ? opts.value : '';
    input.addEventListener('input', function () { if (opts.onChange) opts.onChange(input.value); });
    root.appendChild(input);
    return { el: root, getValue: function () { return input.value; },
             setValue: function (v) { input.value = v == null ? '' : v; },
             setInvalid: function (bad) { root.classList.toggle('invalid', !!bad); } };
  }

  // ── ColorPicker (native swatch + hex text) ───────────────────────────────────────
  // opts: { label, value('#RRGGBB'), onChange(hex) }
  function colorPicker(opts) {
    opts = opts || {};
    var root = el('label', 'l-color');
    if (opts.label) root.appendChild(el('span', 'l-sublabel', opts.label));
    var row = el('span', 'l-color-row');
    var swatch = el('input'); swatch.type = 'color';
    var hex = el('input'); hex.type = 'text'; hex.className = 'hex'; hex.maxLength = 7;
    function norm(v) {
      v = (v || '').trim();
      if (v && v.charAt(0) !== '#') v = '#' + v;
      return /^#[0-9a-fA-F]{6}$/.test(v) ? v.toLowerCase() : null;
    }
    var value = norm(opts.value) || '#000000';
    swatch.value = value; hex.value = value;
    function commit(v) {
      value = v; swatch.value = v; hex.value = v;
      if (opts.onChange) opts.onChange(v);
    }
    swatch.addEventListener('input', function () { commit(swatch.value); });
    hex.addEventListener('change', function () {
      var v = norm(hex.value);
      if (v) commit(v); else hex.value = value;
    });
    row.appendChild(swatch); row.appendChild(hex);
    root.appendChild(row);
    return { el: root, getValue: function () { return value; },
             setValue: function (v) { var n = norm(v); if (n) { value = n; swatch.value = n; hex.value = n; } } };
  }

  // ── SingleSelect (radio list) ─────────────────────────────────────────────
  // opts: { options:[{value,label,desc,disabled}], value, onChange(value) }
  // A `desc` renders as a dim second line on the option card (always visible, so the
  // user can compare choices before picking - mirrors the WPF destination cards).
  function singleSelect(opts) {
    opts = opts || {};
    var root = el('div', 'l-single');
    var value = opts.value != null ? opts.value : null;
    (opts.options || []).forEach(function (o) {
      var row = el('div', 'l-single opt'); row.className = 'opt' + (o.disabled ? ' disabled' : '');
      row.appendChild(el('span', 'dot'));
      if (o.desc) {
        var col = el('span', 'optcol');
        col.appendChild(el('span', 'l-label', o.label));
        col.appendChild(el('div', 'desc', o.desc));
        row.appendChild(col);
      } else {
        row.appendChild(el('span', 'l-label', o.label));
      }
      function paint() { row.classList.toggle('sel', value === o.value); }
      paint();
      if (!o.disabled) row.addEventListener('click', function () {
        if (value === o.value) return;
        value = o.value;
        Array.prototype.forEach.call(root.children, function (c) { c.classList.remove('sel'); });
        row.classList.add('sel');
        if (opts.onChange) opts.onChange(value);
      });
      row._paint = paint;
      root.appendChild(row);
    });
    return { el: root, getValue: function () { return value; },
             setValue: function (v) { value = v;
               Array.prototype.forEach.call(root.children, function (c) { if (c._paint) c._paint(); }); } };
  }

  // ── ToggleSwitch ──────────────────────────────────────────────────────────
  // opts: { label, checked, disabled, onChange(checked) }
  function toggle(opts) {
    opts = opts || {};
    var root = el('span', 'l-toggle' + (opts.disabled ? ' disabled' : ''));
    var track = el('span', 'track'); track.appendChild(el('span', 'knob'));
    root.appendChild(track);
    if (opts.label) root.appendChild(el('span', 'l-label', opts.label));
    var on = !!opts.checked;
    function paint() { root.classList.toggle('on', on); }
    paint();
    if (!opts.disabled) root.addEventListener('click', function () {
      on = !on; paint(); if (opts.onChange) opts.onChange(on);
    });
    return { el: root, getChecked: function () { return on; },
             setChecked: function (v) { on = !!v; paint(); } };
  }

  // ── SectionCard ───────────────────────────────────────────────────────────
  // opts: { title, collapsible, collapsed }  -> mount content into .body
  function sectionCard(opts) {
    opts = opts || {};
    var root = el('div', 'l-card' + (opts.collapsible ? ' collapsible' : '') + (opts.collapsed ? ' collapsed' : ''));
    var head = el('div', 'head');
    if (opts.collapsible) head.appendChild(el('span', 'caret', '\u25BE')); // BLACK DOWN-POINTING SMALL TRIANGLE
    head.appendChild(el('span', 'title', opts.title || ''));
    var body = el('div', 'body');
    root.appendChild(head); root.appendChild(body);
    if (opts.collapsible) head.addEventListener('click', function () { root.classList.toggle('collapsed'); });
    return { el: root, body: body,
             setCollapsed: function (c) { root.classList.toggle('collapsed', !!c); } };
  }

  // ── WarnBanner ────────────────────────────────────────────────────────────
  function warnBanner(text) {
    var root = el('div', 'l-warn');
    root.appendChild(el('span', 'ico', '!'));
    var t = el('span', null, text || '');
    root.appendChild(t);
    return { el: root, setText: function (v) { t.textContent = v; } };
  }

  // ── MultiSelectTabs ───────────────────────────────────────────────────────
  // opts: { groups:{group:[items]}, selected:[], singleSelect, hierarchy:{parent:[kids]},
  //         disabledItems:[], onChange(selectedArray) }
  // Contract (ported from WPF MultiSelectTabs, R23): onChange fires once at the end of
  // setGroups; SingleSelect hides the "All" row and clears prior selection on pick; a
  // pinned "Selected" tab; tabs alphabetical with "Other" last; Hierarchy renders kids
  // indented under a parent caret with an indeterminate parent; DisabledItems are shown
  // dimmed, excluded from "All" and from results (flat path only).
  function multiSelectTabs(opts) {
    opts = opts || {};
    var SELECTED_KEY = '__selected__';
    var root = el('div', 'l-mst');
    var tabsEl = el('div', 'tabs'), listEl = el('div', 'list');
    root.appendChild(tabsEl); root.appendChild(listEl);
    if (opts.height) root.style.height = opts.height;

    var groups = {}, orderedKeys = [], selected = {}, expanded = {}, active = null;
    var single = !!opts.singleSelect;
    var hierarchy = opts.hierarchy || null;
    var disabled = {};
    (opts.disabledItems || []).forEach(function (d) { disabled[d] = true; });

    function isDisabled(item) { return disabled[item] === true; }
    function selCount() { return Object.keys(selected).length; }
    function isSel(item) { return selected[item] === true; }
    function fire() { if (opts.onChange) opts.onChange(Object.keys(selected)); }

    function setGroups(g, initialSelected) {
      groups = g || {};
      selected = {}; expanded = {};
      (initialSelected || []).forEach(function (s) { selected[s] = true; });
      orderedKeys = Object.keys(groups).sort(function (a, b) {
        var ao = a === 'Other' ? 1 : 0, bo = b === 'Other' ? 1 : 0;
        if (ao !== bo) return ao - bo;
        return a.toLowerCase() < b.toLowerCase() ? -1 : a.toLowerCase() > b.toLowerCase() ? 1 : 0;
      });
      active = orderedKeys[0] || null;
      render();
      fire(); // fires once at end of setup (contract)
    }

    function toggleItem(item, on) {
      if (on) { if (single) selected = {}; selected[item] = true; } else delete selected[item];
      render(); fire();
    }

    function makeTab(key, label, count, total) {
      var tab = el('div', 'tab' + (key === active ? ' active' : '') + (count > 0 ? ' has' : ''));
      tab.appendChild(el('span', 'lbl', label));
      tab.appendChild(el('span', 'badge', total < 0 ? String(count) : count + '/' + total));
      tab.addEventListener('click', function () { active = key; render(); });
      return tab;
    }

    function checkRow(label, checked, indeterminate, bold, isDisabledRow, onToggle) {
      var row = el('label', 'l-check' + (bold ? ' bold' : '') + (isDisabledRow ? ' disabled' : ''));
      var cb = el('input'); cb.type = 'checkbox'; cb.checked = checked;
      cb.indeterminate = !!indeterminate; if (isDisabledRow) cb.disabled = true;
      if (!isDisabledRow) cb.addEventListener('change', function () { onToggle(cb.checked); });
      row.appendChild(cb); row.appendChild(el('span', null, label));
      return row;
    }

    function render() {
      // Tabs
      tabsEl.innerHTML = '';
      tabsEl.appendChild(makeTab(SELECTED_KEY, 'Selected', selCount(), -1));
      tabsEl.appendChild(el('div', 'tabsep'));
      orderedKeys.forEach(function (key) {
        var items = groups[key] || [];
        tabsEl.appendChild(makeTab(key, key, items.filter(isSel).length, items.length));
      });

      // List
      listEl.innerHTML = '';
      if (active === SELECTED_KEY) {
        var sel = Object.keys(selected);
        if (sel.length === 0) { listEl.appendChild(el('div', 'empty', 'No items selected yet.')); return; }
        sel.forEach(function (item) {
          listEl.appendChild(checkRow(item, true, false, false, false, function (on) { toggleItem(item, on); }));
        });
        return;
      }
      if (active == null) return;
      var groupItems = groups[active] || [];

      if (!single) {
        var toggleable = groupItems.filter(function (x) { return !isDisabled(x); });
        var allChecked  = toggleable.length > 0 && toggleable.every(isSel);
        var someChecked = toggleable.some(isSel) && !allChecked;
        listEl.appendChild(checkRow('All ' + active, allChecked, someChecked, true, false, function (on) {
          toggleable.forEach(function (it) { if (on) selected[it] = true; else delete selected[it]; });
          render(); fire();
        }));
        listEl.appendChild(el('div', 'divider'));
      }

      if (hierarchy) {
        var childSet = {};
        var groupSet = {}; groupItems.forEach(function (i) { groupSet[i] = true; });
        Object.keys(hierarchy).forEach(function (parent) {
          if (!groupSet[parent]) return;
          (hierarchy[parent] || []).forEach(function (kid) { if (groupSet[kid]) childSet[kid] = true; });
        });
        groupItems.forEach(function (item) {
          if (childSet[item]) return; // rendered under its parent
          var kids = (hierarchy[item] || []).filter(function (k) { return groupSet[k]; });
          var hasKids = kids.length > 0, isOpen = expanded[item] === true;
          var checked = isSel(item);
          var indeterminate = !checked && hasKids && kids.some(isSel);
          var row = checkRow(item, checked, indeterminate, false, false, function (on) { toggleItem(item, on); });
          if (hasKids) {
            var caret = el('span', 'kids-caret', isOpen ? '\u25BE' : '\u25B8');
            caret.addEventListener('click', function (e) {
              e.preventDefault(); e.stopPropagation();
              if (expanded[item]) delete expanded[item]; else expanded[item] = true; render();
            });
            row.insertBefore(caret, row.firstChild);
          } else {
            row.insertBefore(el('span', 'kids-caret', ''), row.firstChild);
          }
          listEl.appendChild(row);
          if (hasKids && isOpen) kids.forEach(function (kid) {
            var krow = checkRow(kid, isSel(kid), false, false, false, function (on) { toggleItem(kid, on); });
            krow.classList.add('indent');
            listEl.appendChild(krow);
          });
        });
      } else {
        groupItems.forEach(function (item) {
          var dis = isDisabled(item);
          listEl.appendChild(checkRow(item, isSel(item), false, false, dis, function (on) { toggleItem(item, on); }));
        });
      }
    }

    if (opts.groups) setGroups(opts.groups, opts.selected);

    return { el: root, setGroups: setGroups,
             getSelected: function () { return Object.keys(selected); },
             setSelected: function (arr) { selected = {}; (arr || []).forEach(function (s) { selected[s] = true; }); render(); } };
  }

  // ── CheckList (flat list of checkboxes) ─────────────────────────────────────
  // opts: { items:[{value,label,checked,disabled}], onChange(selectedValuesArray) }
  // The house "pick which of these" list (no tabs). onChange reports the checked values.
  function checkList(opts) {
    opts = opts || {};
    var root = el('div', 'l-checklist');
    var sel = {};
    (opts.items || []).forEach(function (it) { if (it.checked) sel[it.value] = true; });
    function fire() { if (opts.onChange) opts.onChange(Object.keys(sel)); }
    (opts.items || []).forEach(function (it) {
      var row = el('label', 'l-check' + (it.disabled ? ' disabled' : ''));
      var cb = el('input'); cb.type = 'checkbox'; cb.checked = !!sel[it.value];
      if (it.disabled) cb.disabled = true;
      if (!it.disabled) cb.addEventListener('change', function () {
        if (cb.checked) sel[it.value] = true; else delete sel[it.value];
        fire();
      });
      row.appendChild(cb); row.appendChild(el('span', null, it.label));
      root.appendChild(row);
    });
    return { el: root, getSelected: function () { return Object.keys(sel); } };
  }

  // ── Review (read-only summary) ──────────────────────────────────────────────
  // opts: { items:[{label,value}], note, warning, chips:[string], chipsLabel }  - mirrors the WPF ReviewSummary:
  // an optional warning banner above, a 2-column grid of label/value cards (UPPERCASE dim
  // label + mono value), and an optional italic note below.
  function review(opts) {
    opts = opts || {};
    var root = el('div', 'l-review');
    if (opts.warning) {
      var wb = el('div', 'rwarn');
      wb.appendChild(el('span', null, opts.warning));
      root.appendChild(wb);
    }
    var cards = el('div', 'cards');
    (opts.items || []).forEach(function (it) {
      var card = el('div', 'card');
      card.appendChild(el('div', 'k', (it.label || '').toUpperCase()));
      card.appendChild(el('div', 'v', it.value));
      cards.appendChild(card);
    });
    root.appendChild(cards);
    // Optional chip row below the cards (WPF ReviewSummary's ITEMS box).
    if (opts.chips && opts.chips.length) {
      var cb = el('div', 'chipbox');
      cb.appendChild(el('div', 'k', (opts.chipsLabel || 'Items').toUpperCase()));
      var crow = el('div', 'chips');
      opts.chips.forEach(function (c) { crow.appendChild(el('span', 'chip', c)); });
      cb.appendChild(crow);
      root.appendChild(cb);
    }
    if (opts.note) root.appendChild(el('div', 'note', opts.note));
    return { el: root };
  }

  // ── Folder / File browser (read-only path + Browse button) ──────────────────
  // opts: { value, placeholder, onBrowse() }. The actual OS dialog is opened by C#
  // (rule R26 - JS never touches the filesystem); onBrowse posts the request and the
  // result comes back via setValue.
  function browseRow(opts, kind) {
    opts = opts || {};
    var root = el('div', 'l-browse');
    var input = el('input');
    input.readOnly = true;
    input.value = opts.value || '';
    input.setAttribute('placeholder', opts.placeholder || (kind === 'file' ? 'No file selected' : 'No folder selected'));
    var btn = el('button', 'l-btn', 'Browse...'); btn.type = 'button';
    btn.addEventListener('click', function () { if (opts.onBrowse) opts.onBrowse(); });
    root.appendChild(input); root.appendChild(btn);
    return { el: root, getValue: function () { return input.value; }, setValue: function (v) { input.value = v || ''; } };
  }
  function folderBrowser(opts) { return browseRow(opts, 'folder'); }
  function fileBrowser(opts)   { return browseRow(opts, 'file'); }

  // ── TokenInput (naming-pattern editor) ──────────────────────────────────────
  // opts: { value, defaultPattern, groups:[{header, chips:[{key,label,braced,user,tooltip}]}],
  //         sample:{key:value}, onChange(pattern) }. Chips insert {Token} at the caret; the
  //         preview substitutes the sample map (unknown tokens stay literal, like the resolver).
  //         Mirrors the WPF TokenInput (grouped Target/Source/Project/Date/User chips).
  function tokenInput(opts) {
    opts = opts || {};
    var root = el('div', 'l-token');
    var sample = opts.sample || {};
    var def = opts.defaultPattern || '';

    var chips = el('div', 'chips');
    (opts.groups || []).forEach(function (g) {
      if (!g.chips || !g.chips.length) return;
      chips.appendChild(el('div', 'ghead', (g.header || '').toUpperCase()));
      var row = el('div', 'crow');
      g.chips.forEach(function (c) {
        var chip = el('button', 'chip' + (c.user ? ' user' : ''), c.label); chip.type = 'button';
        if (c.tooltip) chip.title = c.tooltip;
        chip.addEventListener('click', function () { insertAtCursor(c.braced || ('{' + c.key + '}')); });
        row.appendChild(chip);
      });
      chips.appendChild(row);
    });
    root.appendChild(chips);

    var irow = el('div', 'irow');
    var input = el('input', 'pat'); input.type = 'text'; input.spellcheck = false; input.value = opts.value || '';
    if (def) input.title = def;
    var reset = el('button', 'l-btn reset', '\u21BA Reset'); reset.type = 'button'; reset.style.display = 'none';
    reset.addEventListener('click', function () { input.value = def; onInput(); input.focus(); });
    irow.appendChild(input); irow.appendChild(reset);
    root.appendChild(irow);

    var preview = el('div', 'preview');
    root.appendChild(preview);

    function insertAtCursor(s) {
      var a = input.selectionStart || 0, b = input.selectionEnd || 0;
      input.value = input.value.slice(0, a) + s + input.value.slice(b);
      input.selectionStart = input.selectionEnd = a + s.length;
      input.focus(); onInput();
    }
    function resolve(pat) {
      return (pat || '').replace(/\{([^}]+)\}/g, function (m, k) { return (k in sample) ? sample[k] : m; });
    }
    function onInput() {
      reset.style.display = (input.value.trim() === def.trim()) ? 'none' : '';
      var r = resolve(input.value);
      preview.textContent = 'Preview: ' + (r.trim() ? r : '(empty)');
      if (opts.onChange) opts.onChange(input.value);
    }
    input.addEventListener('input', onInput);
    onInput();
    return { el: root, getValue: function () { return input.value; },
             setValue: function (v) { input.value = v == null ? '' : v; onInput(); } };
  }

  // ── BrowserTreePicker (Project Browser view/sheet tree) ─────────────────────
  // opts: { roots:[node], selected:[id], singleSelect, onChange(idsArray) }
  // node: { title, id (string|null; null=org folder), isSheet, children:[node] }
  // Contract (ported from WPF BrowserTreePicker, R23): a leaf's checkbox selects only that
  // leaf (a parent view's dependents are separate child leaves); RIGHT-CLICK any row selects
  // only the descendant leaves beneath it (dependents), additive, leaving the clicked node
  // unchecked; a no-op in singleSelect. SingleSelect makes checking clear all prior selection.
  function browserTree(opts) {
    opts = opts || {};
    var root = el('div', 'l-tree');
    var single = !!opts.singleSelect;
    var selected = {};
    (opts.selected || []).forEach(function (id) { selected[String(id)] = true; });
    var expanded = {}; // node key -> false when collapsed (default expanded)
    var seq = 0;

    function descendantLeaves(node, includeSelf) {
      var out = [];
      if (node.id != null && includeSelf) out.push(String(node.id));
      (node.children || []).forEach(function (c) { out = out.concat(descendantLeaves(c, true)); });
      return out;
    }
    function fire() { if (opts.onChange) opts.onChange(Object.keys(selected)); }
    function setLeaf(id, on) {
      if (single) { selected = {}; if (on) selected[id] = true; }
      else { if (on) selected[id] = true; else delete selected[id]; }
      render(); fire();
    }
    function selectDescendants(node) {
      if (single) return; // right-click is a no-op in single-select mode
      descendantLeaves(node, false).forEach(function (id) { selected[id] = true; });
      render(); fire();
    }

    function renderNode(node, depth, container) {
      if (!node.__k) node.__k = 'n' + (seq++);
      var isFolder = node.id == null;
      var kids = node.children || [];
      var hasKids = kids.length > 0;
      var open = expanded[node.__k] !== false;

      var row = el('div', 'row'); row.style.paddingLeft = (depth * 16 + 6) + 'px';
      if (hasKids) {
        var caret = el('span', 'caret', open ? '\u25BE' : '\u25B8');
        caret.addEventListener('click', function (e) { e.stopPropagation(); expanded[node.__k] = !open; render(); });
        row.appendChild(caret);
      } else { row.appendChild(el('span', 'caret')); }

      if (!isFolder) {
        var cb = el('input'); cb.type = 'checkbox'; cb.checked = selected[String(node.id)] === true;
        cb.addEventListener('change', function () { setLeaf(String(node.id), cb.checked); });
        row.appendChild(cb);
      } else { row.appendChild(el('span', 'nocb')); }

      row.appendChild(el('span', 'lbl' + (isFolder ? ' folder' : ''), node.title));
      row.addEventListener('contextmenu', function (e) { e.preventDefault(); selectDescendants(node); });
      container.appendChild(row);

      if (hasKids && open) kids.forEach(function (c) { renderNode(c, depth + 1, container); });
    }
    function render() { root.innerHTML = ''; seq = 0; (opts.roots || []).forEach(function (n) { renderNode(n, 0, root); }); }

    render();
    fire(); // fires once at end of setup (contract)
    return { el: root, getSelected: function () { return Object.keys(selected); } };
  }

  // ── NumberRange (min/max stepper pair) ──────────────────────────────────────
  // opts: { min, max, lo, hi, step, decimals, onChange({min,max}) } - lo/hi are the
  // current values; commit keeps lo <= hi (mirrors the WPF NumberRange contract).
  function numberRange(opts) {
    opts = opts || {};
    var root = el('div', 'l-range');
    var lo = num(opts.lo, num(opts.min, 0)), hi = num(opts.hi, num(opts.max, 100));
    function fire() { if (opts.onChange) opts.onChange({ min: lo, max: hi }); }
    var loS = stepper({ min: opts.min, max: opts.max, step: opts.step, decimals: opts.decimals, value: lo,
      onChange: function (v) { lo = v; if (lo > hi) { hi = lo; hiS.setValue(hi); } fire(); } });
    var hiS = stepper({ min: opts.min, max: opts.max, step: opts.step, decimals: opts.decimals, value: hi,
      onChange: function (v) { hi = v; if (hi < lo) { lo = hi; loS.setValue(lo); } fire(); } });
    root.appendChild(loS.el);
    root.appendChild(el('span', 'to', 'to'));
    root.appendChild(hiS.el);
    return { el: root, getValue: function () { return { min: lo, max: hi }; } };
  }

  // ── SearchSelect (filterable single-pick dropdown) ──────────────────────────
  // opts: { options:[string], value, placeholder, onChange(value) }. In-page list popup
  // (never a native/WPF popup - R27); commits on click or Enter, filters as you type.
  // -- Dropdown (native select, themed) ---------------------------------------
  // opts: { value, options:[{value,label}], onChange(value) } - a compact closed
  // picker for dense editor rows, where the radio-list singleSelect is too tall.
  function dropdown(opts) {
    opts = opts || {};
    var sel = el('select', 'l-dropdown');
    (opts.options || []).forEach(function (o) {
      var op = el('option', null, o.label != null ? o.label : String(o.value));
      op.value = o.value;
      if (o.value === opts.value) op.selected = true;
      sel.appendChild(op);
    });
    sel.addEventListener('change', function () { if (opts.onChange) opts.onChange(sel.value); });
    return { el: sel, getValue: function () { return sel.value; },
             setValue: function (v) { sel.value = v; } };
  }

  function searchSelect(opts) {
    opts = opts || {};
    var root = el('div', 'l-search');
    var input = el('input'); input.type = 'text'; input.spellcheck = false;
    input.value = opts.value || '';
    if (opts.placeholder) input.setAttribute('placeholder', opts.placeholder);
    var list = el('div', 'list'); list.style.display = 'none';
    root.appendChild(input); root.appendChild(list);
    var all = (opts.options || []).slice();
    var committed = input.value;

    function commit(v) {
      committed = v; input.value = v; hide();
      if (opts.onChange) opts.onChange(v);
    }
    function hide() { list.style.display = 'none'; }
    function show(items) {
      list.innerHTML = '';
      if (!items.length) { hide(); return; }
      items.slice(0, 50).forEach(function (o) {
        var row = el('div', 'opt', o);
        row.addEventListener('mousedown', function (e) { e.preventDefault(); commit(o); });
        list.appendChild(row);
      });
      list.style.display = '';
    }
    input.addEventListener('input', function () {
      var q = input.value.toLowerCase();
      show(all.filter(function (o) { return o.toLowerCase().indexOf(q) >= 0; }));
    });
    input.addEventListener('focus', function () { show(all); });
    input.addEventListener('blur', function () { setTimeout(function () { input.value = committed; hide(); }, 120); });
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') {
        var q = input.value.toLowerCase();
        var m = all.filter(function (o) { return o.toLowerCase().indexOf(q) >= 0; });
        if (m.length) commit(m[0]);
      }
      if (e.key === 'Escape') { input.value = committed; hide(); }
    });
    return { el: root, getValue: function () { return committed; },
             setValue: function (v) { committed = v || ''; input.value = committed; } };
  }

  // ── FileTable (file-queue rows) ───────────────────────────────────────────
  // opts: { headers:{file,version,placement}, rows:[{nameId,name,ext,path,badge,
  //         badgeTone('up'|'cur'|'bad'|'dim'), selectId, selectValue, options,
  //         removeId, disabled}], onCell(inputId,value), onRemove(inputId) }
  // Mirrors the WPF UpgradeLinks file table: header row + one aligned grid row per
  // file (editable save-as + extension + source path, colored version badge, compact
  // placement dropdown, per-row remove). Rebuilds arrive whole via `stepInputs`, so
  // the handle carries no per-cell setters.
  function fileTable(opts) {
    opts = opts || {};
    var root = el('div', 'l-ftable');
    var h = opts.headers || {};
    var hdr = el('div', 'frow hdr');
    hdr.appendChild(el('div', null, ''));
    hdr.appendChild(el('div', null, h.file || ''));
    hdr.appendChild(el('div', null, h.version || ''));
    hdr.appendChild(el('div', null, h.placement || ''));
    hdr.appendChild(el('div', null, ''));
    root.appendChild(hdr);
    (opts.rows || []).forEach(function (r) {
      var row = el('div', 'frow');
      row.appendChild(el('div', null, ''));
      var names = el('div', 'names');
      var nb = el('div', 'nbox');
      var input = el('input');
      input.type = 'text'; input.spellcheck = false;
      input.value = r.name || '';
      input.disabled = !!r.disabled;
      input.addEventListener('input', function () { if (opts.onCell) opts.onCell(r.nameId, input.value); });
      nb.appendChild(input);
      nb.appendChild(el('span', 'ext', r.ext || ''));
      names.appendChild(nb);
      names.appendChild(el('div', 'fpath', r.path || ''));
      row.appendChild(names);
      var bwrap = el('div');
      bwrap.appendChild(el('span', 'badge ' + (r.badgeTone || 'dim'), r.badge || ''));
      row.appendChild(bwrap);
      var dd = dropdown({ options: r.options, value: r.selectValue,
                          onChange: function (v) { if (opts.onCell) opts.onCell(r.selectId, v); } });
      dd.el.disabled = !!r.disabled;
      row.appendChild(dd.el);
      var rm = el('button', 'rm', '\u00D7'); rm.type = 'button'; // MULTIPLICATION SIGN
      rm.addEventListener('click', function () { if (opts.onRemove) opts.onRemove(r.removeId); });
      row.appendChild(rm);
      root.appendChild(row);
    });
    return { el: root };
  }

  function num(v, dflt) { var n = parseFloat(v); return isNaN(n) ? dflt : n; }

  return {
    el: el, button: button, stepper: stepper, textField: textField,
    colorPicker: colorPicker,
    singleSelect: singleSelect, toggle: toggle, sectionCard: sectionCard,
    warnBanner: warnBanner, multiSelectTabs: multiSelectTabs,
    checkList: checkList, review: review,
    folderBrowser: folderBrowser, fileBrowser: fileBrowser,
    tokenInput: tokenInput, browserTree: browserTree,
    numberRange: numberRange, searchSelect: searchSelect,
    dropdown: dropdown, fileTable: fileTable
  };
})();

// ── Window resize handles ─────────────────────────────────────────────────────
// The WebView2 child HWND covers the WPF WindowChrome resize border, so edge-drag
// resize silently fails. These thin fixed-position handles at each edge/corner post
// a `resize` action to C#, which drives the native OS resize loop (same technique as
// the title-bar drag). Attached once per page, only when a bridge is present (so
// headless screenshot renders are untouched). Idempotent.
Lemoine.attachResizeHandles = function () {
  if (!Lemoine.hasBridge || !Lemoine.hasBridge()) return;
  if (document.getElementById('l-resize-layer')) return;
  var layer = document.createElement('div');
  layer.id = 'l-resize-layer';
  var dirs = ['top', 'bottom', 'left', 'right', 'topleft', 'topright', 'bottomleft', 'bottomright'];
  dirs.forEach(function (dir) {
    var h = document.createElement('div');
    h.className = 'l-resize l-resize-' + dir;
    h.addEventListener('mousedown', function (e) {
      if (e.button !== 0) return;
      e.preventDefault();
      Lemoine.send('action', { action: 'resize', dir: dir });
    });
    layer.appendChild(h);
  });
  document.body.appendChild(layer);
};

if (document.readyState === 'loading')
  document.addEventListener('DOMContentLoaded', Lemoine.attachResizeHandles);
else
  Lemoine.attachResizeHandles();
