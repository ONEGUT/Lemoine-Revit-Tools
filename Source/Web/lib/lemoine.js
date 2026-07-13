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

  // ── SingleSelect (radio list) ─────────────────────────────────────────────
  // opts: { options:[{value,label,disabled}], value, onChange(value) }
  function singleSelect(opts) {
    opts = opts || {};
    var root = el('div', 'l-single');
    var value = opts.value != null ? opts.value : null;
    (opts.options || []).forEach(function (o) {
      var row = el('div', 'l-single opt'); row.className = 'opt' + (o.disabled ? ' disabled' : '');
      row.appendChild(el('span', 'dot'));
      row.appendChild(el('span', 'l-label', o.label));
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

  function num(v, dflt) { var n = parseFloat(v); return isNaN(n) ? dflt : n; }

  return {
    el: el, button: button, stepper: stepper, textField: textField,
    singleSelect: singleSelect, toggle: toggle, sectionCard: sectionCard,
    warnBanner: warnBanner, multiSelectTabs: multiSelectTabs
  };
})();
