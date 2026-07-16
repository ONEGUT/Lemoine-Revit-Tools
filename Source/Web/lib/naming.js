/* Naming settings tab (HTML analogue of GlobalSettingsWindow.Naming.cs). A master/detail token
   editor: the left column lists user tokens (edit / delete-with-confirm), the right column is the
   editor (name -> derived key, applies-to, reads-from, searchable parameter catalog, fallback,
   description, live test). The page owns all editor state and filters the catalog client-side;
   it round-trips to C# only to save (authoritative validation) and delete. ASCII-only (rule R13). */
(function () {
  'use strict';

  function namingTab(opts) {
    opts = opts || {};
    var send = opts.send || function () {};
    var ui = (window.Lemoine && window.Lemoine.ui) || {};
    var el = ui.el || function (tag, cls, text) {
      var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e;
    };

    var payload = null;         // last init payload (tokens, catalog, labels, built-ins)
    var ed = null;              // editor form state
    var builtInOpen = false;
    var pendingDelete = null;   // token key awaiting inline delete confirm
    var container = null;

    function L(k) { return (payload && payload.labels && payload.labels[k]) || ''; }

    // -- Public ----------------------------------------------------------------
    function render(host, p, selectKey) {
      container = host; payload = p;
      // selectKey is set only on a post-save re-init: reload the saved token (clears any error).
      if (selectKey) loadToken(selectKey);
      else if (!ed) resetForNew();
      // If the token being edited was deleted elsewhere, fall back to a new one.
      if (ed && ed.originalKey && !findToken(ed.originalKey)) resetForNew();
      draw();
    }

    function showError(message) { if (ed) { ed.error = message || ''; draw(); } }

    // -- Editor state ------------------------------------------------------------
    function resetForNew() {
      ed = { originalKey: null, label: '', key: '', keyManual: false,
             entity: payload.entitySheet, subject: payload.subjTarget,
             paramName: '', paramGuid: '', fallback: '', description: '', error: '' };
      pendingDelete = null;
    }
    function loadToken(key) {
      var t = findToken(key); if (!t) { resetForNew(); return; }
      ed = { originalKey: t.key, label: t.label, key: t.key, keyManual: true,
             entity: t.entity, subject: t.subject, paramName: t.paramName, paramGuid: t.paramGuid,
             fallback: t.fallback, description: t.description, error: '' };
      pendingDelete = null;
    }
    function findToken(key) { return (payload.tokens || []).filter(function (t) { return t.key === key; })[0]; }

    // -- Draw ------------------------------------------------------------------
    function draw() {
      container.innerHTML = '';

      // Intro + source line
      container.appendChild(el('div', 'l-nm-intro', payload.intro));
      var src = el('div', 'l-nm-source' + (payload.sourceOk ? '' : ' bad'), payload.source);
      container.appendChild(src);

      // Built-in reference accordion
      container.appendChild(buildBuiltIns());

      container.appendChild(el('div', 'l-nm-h', L('yourTokens')));

      var cols = el('div', 'l-nm-cols');
      cols.appendChild(buildList());
      cols.appendChild(buildEditor());
      container.appendChild(cols);
    }

    // -- Built-in reference ------------------------------------------------------
    function buildBuiltIns() {
      var wrap = el('div', 'l-nm-builtins');
      var head = el('div', 'l-nm-bi-head');
      head.appendChild(el('span', 'caret', builtInOpen ? '\u25BE' : '\u25B8'));
      head.appendChild(el('span', null, payload.builtInHeader));
      head.addEventListener('click', function () { builtInOpen = !builtInOpen; draw(); });
      wrap.appendChild(head);
      if (builtInOpen) {
        var grid = el('div', 'l-nm-bi-grid');
        (payload.builtIns || []).forEach(function (b) {
          grid.appendChild(el('div', 'k', b.braced));
          grid.appendChild(el('div', 'd', b.label + ' \u2014 ' + b.desc + '  \u00b7  ' + b.entity));
        });
        wrap.appendChild(grid);
      }
      return wrap;
    }

    // -- Token list --------------------------------------------------------------
    function buildList() {
      var list = el('div', 'l-nm-list');
      var tokens = payload.tokens || [];
      if (tokens.length === 0) {
        list.appendChild(el('div', 'l-nm-empty', L('empty')));
      } else {
        tokens.forEach(function (t) { list.appendChild(buildRow(t)); });
      }
      var add = el('button', 'l-btn primary l-nm-add', L('addNew'));
      add.addEventListener('click', function () { resetForNew(); draw(); });
      list.appendChild(add);
      return list;
    }

    function buildRow(t) {
      var row = el('div', 'l-nm-row');
      if (pendingDelete === t.key) {
        row.appendChild(el('div', 'l-nm-confirm', L('deleteConfirm') + ' ' + t.label));
        if (t.usedBy) row.appendChild(el('div', 'l-nm-usedby', t.usedBy));
        var btns = el('div', 'l-nm-rowbtns');
        var yes = el('button', 'l-btn danger', L('deleteYes'));
        yes.addEventListener('click', function () { pendingDelete = null; send('action', { action: 'namingDelete', key: t.key }); });
        var no = el('button', 'l-btn', L('deleteNo'));
        no.addEventListener('click', function () { pendingDelete = null; draw(); });
        btns.appendChild(yes); btns.appendChild(no);
        row.appendChild(btns);
        return row;
      }
      var actions = el('div', 'l-nm-rowactions');
      var edit = el('button', 'l-btn', L('rowEdit'));
      edit.addEventListener('click', function () { loadToken(t.key); draw(); });
      var del = el('button', 'l-btn danger', L('rowDelete'));
      del.addEventListener('click', function () { pendingDelete = t.key; draw(); });
      actions.appendChild(edit); actions.appendChild(del);
      row.appendChild(actions);

      var meta = el('div', 'l-nm-meta');
      meta.appendChild(el('div', 'key', '{u:' + t.key + '}'));
      meta.appendChild(el('div', 'lbl', t.label));
      meta.appendChild(el('div', 'sub', t.entityWord + ' \u00b7 ' + t.paramName + ' (' + t.paramWord + ')'));
      row.appendChild(meta);
      return row;
    }

    // -- Editor ------------------------------------------------------------------
    var liveEl, keyEl, paramListEl;

    function buildEditor() {
      var card = el('div', 'l-nm-editor');
      card.appendChild(el('div', 'l-nm-etitle',
        ed.originalKey == null ? L('editorTitleNew') : L('editorTitleEdit').replace('{0}', ed.label)));

      // Name
      card.appendChild(fieldLabel(L('fieldName')));
      var name = textInput(ed.label, function (v) {
        ed.label = v;
        if (!ed.keyManual) { ed.key = deriveKey(v); if (keyEl) keyEl.textContent = '{u:' + ed.key + '}'; }
      });
      card.appendChild(name);

      // Key
      card.appendChild(buildKeyRow());

      // Applies to
      card.appendChild(fieldLabel(L('fieldAppliesTo')));
      card.appendChild(enumSelect([L('appliesSheets'), L('appliesViews')],
        ed.entity === payload.entitySheet ? L('appliesSheets') : L('appliesViews'),
        function (v) { ed.entity = (v === L('appliesViews')) ? payload.entityView : payload.entitySheet; draw(); }));

      // Reads from
      card.appendChild(fieldLabel(L('fieldReadsFrom')));
      card.appendChild(enumSelect([L('readsTarget'), L('readsSource'), L('readsProject')],
        ed.subject === payload.subjSource ? L('readsSource') : ed.subject === payload.subjProject ? L('readsProject') : L('readsTarget'),
        function (v) {
          ed.subject = (v === L('readsSource')) ? payload.subjSource : (v === L('readsProject')) ? payload.subjProject : payload.subjTarget;
          draw();
        }));
      card.appendChild(el('div', 'l-nm-caption', L('readsCaption')));

      // Parameter picker
      card.appendChild(buildParameter());

      // Fallback
      card.appendChild(fieldLabel(L('fieldFallback')));
      card.appendChild(textInput(ed.fallback, function (v) { ed.fallback = v; updateLive(); }, L('fallbackPh')));
      card.appendChild(el('div', 'l-nm-caption', L('fallbackCaption')));

      // Description
      card.appendChild(fieldLabel(L('fieldDesc')));
      card.appendChild(textInput(ed.description, function (v) { ed.description = v; }));

      // Live test
      card.appendChild(fieldLabel(L('fieldLiveTest')));
      liveEl = el('div', 'l-nm-live', liveText());
      card.appendChild(liveEl);

      // Error
      if (ed.error) card.appendChild(el('div', 'l-nm-error', ed.error));

      // Footer
      var footer = el('div', 'l-nm-footer');
      var save = el('button', 'l-btn primary', ed.originalKey == null ? L('saveNew') : L('saveUpdate'));
      save.disabled = !ed.label.trim();
      save.addEventListener('click', doSave);
      footer.appendChild(save);
      if (ed.originalKey != null) {
        var cancel = el('button', 'l-btn', L('cancelEdit'));
        cancel.addEventListener('click', function () { resetForNew(); draw(); });
        footer.appendChild(cancel);
      }
      card.appendChild(footer);
      return card;
    }

    function buildKeyRow() {
      var wrap = el('div', 'l-nm-keyrow');
      wrap.appendChild(fieldLabel(L('fieldKey')));
      if (ed.keyManual) {
        wrap.appendChild(textInput('u:' + ed.key, function (v) {
          var raw = (v || '').trim();
          ed.key = raw.toLowerCase().indexOf('u:') === 0 ? raw.substring(2) : raw;
        }));
      } else {
        var line = el('div', 'l-nm-keyline');
        keyEl = el('span', 'l-nm-keytext', '{u:' + ed.key + '}');
        line.appendChild(keyEl);
        var editBtn = el('button', 'l-btn', L('fieldKeyEdit'));
        editBtn.addEventListener('click', function () { ed.keyManual = true; draw(); });
        line.appendChild(editBtn);
        wrap.appendChild(line);
      }
      return wrap;
    }

    function buildParameter() {
      var wrap = el('div', 'l-nm-param');
      wrap.appendChild(fieldLabel(L('fieldParameter')));

      var search = textInput(ed.search || '', function (v) { ed.search = v; refreshParamList(); }, L('paramSearch'));
      wrap.appendChild(search);

      paramListEl = el('div', 'l-nm-paramlist');
      wrap.appendChild(paramListEl);
      refreshParamList();

      var manual = el('div', 'l-nm-manual');
      manual.appendChild(textInput(ed.paramName, function (v) { ed.paramName = v; updateLive(); }, L('manualName')));
      manual.appendChild(textInput(ed.paramGuid, function (v) { ed.paramGuid = v; updateLive(); }, L('manualGuid')));
      wrap.appendChild(manual);
      wrap.appendChild(el('div', 'l-nm-caption', L('manualCaption')));

      var matched = matchedParam();
      if (matched && matched.storage !== 'String' && matched.storage !== '')
        wrap.appendChild(el('div', 'l-nm-caption', L('nonString')));
      return wrap;
    }

    function currentCatalog() {
      var c = payload.catalog || {};
      if (ed.subject === payload.subjProject) return c.project || [];
      return ed.entity === payload.entitySheet ? (c.sheet || []) : (c.view || []);
    }

    function refreshParamList() {
      if (!paramListEl) return;
      paramListEl.innerHTML = '';
      var cat = currentCatalog();
      if (cat.length === 0) { paramListEl.appendChild(el('div', 'l-nm-paramnone', L('paramNone'))); return; }
      var q = (ed.search || '').toLowerCase();
      var filtered = q ? cat.filter(function (e2) { return e2.name.toLowerCase().indexOf(q) >= 0; }) : cat;
      filtered.forEach(function (e2) {
        var sel = (e2.name.toLowerCase() === (ed.paramName || '').toLowerCase()) && (e2.guid || '') === (ed.paramGuid || '');
        var row = el('div', 'l-nm-paramrow' + (sel ? ' sel' : ''));
        row.appendChild(el('span', 'nm', e2.name));
        row.appendChild(el('span', 'or' + (e2.shared ? ' shared' : ''), e2.origin));
        row.addEventListener('click', function () { ed.paramName = e2.name; ed.paramGuid = e2.guid || ''; draw(); });
        paramListEl.appendChild(row);
      });
    }

    function matchedParam() {
      return currentCatalog().filter(function (e2) {
        return e2.name.toLowerCase() === (ed.paramName || '').toLowerCase() && (e2.guid || '') === (ed.paramGuid || '');
      })[0];
    }

    function liveText() {
      var m = matchedParam();
      if (m && m.sample) return L('liveSample').replace('{0}', m.sample);
      return ed.fallback ? L('liveFallback').replace('{0}', ed.fallback) : L('liveNoFallback');
    }
    function updateLive() { if (liveEl) liveEl.textContent = liveText(); }

    function doSave() {
      send('action', { action: 'namingSave', originalKey: ed.originalKey || '',
        key: ed.key, label: ed.label, subject: ed.subject, entity: ed.entity,
        paramName: ed.paramName, paramGuid: ed.paramGuid, fallback: ed.fallback, description: ed.description });
    }

    // -- Small builders --------------------------------------------------------
    function fieldLabel(text) { return el('div', 'l-nm-flabel', text); }
    function textInput(value, onInput, placeholder) {
      var inp = el('input', 'l-nm-input');
      inp.type = 'text'; inp.value = value || '';
      if (placeholder) inp.setAttribute('placeholder', placeholder);
      inp.addEventListener('input', function () { onInput(inp.value); });
      return inp;
    }
    function enumSelect(items, current, onChange) {
      // Reuse the house single-select radio list.
      var opts2 = items.map(function (i) { return { value: i, label: i }; });
      return ui.singleSelect({ options: opts2, value: current, onChange: onChange }).el;
    }

    // Ports GlobalSettingsWindow.DeriveNamingKey: PascalCase alphanumerics, "T" prefix if it
    // would start with a digit, capped at 40 chars.
    function deriveKey(label) {
      if (!label || !label.trim()) return '';
      var words = label.split(/[ \-_.\/\\]+/).filter(Boolean);
      var key = words.map(function (w) {
        var clean = w.replace(/[^a-zA-Z0-9]/g, '');
        return clean ? clean.charAt(0).toUpperCase() + clean.substring(1) : '';
      }).join('');
      if (key.length && /[0-9]/.test(key.charAt(0))) key = 'T' + key;
      return key.length > 40 ? key.substring(0, 40) : key;
    }

    return { render: render, showError: showError };
  }

  window.Lemoine = window.Lemoine || {};
  window.Lemoine.namingTab = namingTab;
})();
