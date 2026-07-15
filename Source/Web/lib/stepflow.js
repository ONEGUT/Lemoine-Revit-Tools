// =============================================================================
// stepflow.js - the HTML StepFlow window shell (Phase 2, rules R17-R21).
//
// Builds the full tool-window chrome (toolbar, step accordion with pips, per-step
// inputs, footer, output log + progress) from a serializable spec delivered in the
// `init` bridge message, and drives it entirely by messages:
//   C# -> JS : init, validation, log, progress, complete, themeChanged
//   JS -> C# : state (an input changed), action (confirm/run/reset/cancel/navigate)
//
// The tool ViewModel stays the authority on validation (rule R21): JS reports input
// state and reflects the `validation` message; it never gates Run on its own.
//
// Depends on lemoine.js (component factories) + lemoine.css (chrome styles).
// =============================================================================
window.Lemoine = window.Lemoine || {};
Lemoine.stepflow = function (container, opts) {
  var U = Lemoine.ui;
  opts = opts || {};
  var send = opts.send || function () {};

  var spec = null;
  var steps = [];             // [{def, el, bodyEl, pipEl, confirmBtn, inputs:{id:handle}}]
  var activeId = null;
  var valid = {};             // stepId -> bool (from C#)
  var canRun = false;
  var running = false, finished = false, finishCls = 'pass';
  var runEl = null, logEl = null, runBtn = null, backBtn = null;
  var continueBtn = null, skipBtn = null; // pausable-run footer buttons
  var chipEl = null, statusEl = null, fillEl = null, pipsEl = null, countsEl = null; // top-strip chrome

  // ── Public API (driven by inbound bridge messages) ─────────────────────────
  function init(newSpec) {
    spec = newSpec || {};
    build();
  }
  function applyValidation(v) {
    valid = (v && v.steps) || {};
    canRun = !!(v && v.canRun);
    steps.forEach(function (s) { if (s.confirmBtn) s.confirmBtn.disabled = valid[s.def.id] === false; });
    refreshStepStates();
    if (runBtn) runBtn.disabled = !canRun || running;
  }
  // Pip / accent / required-hint state. A step is "done" (green) only once you've moved PAST it
  // (matches WPF: an unvisited valid step stays grey "Waiting", not green). The active required
  // step shows an inline "Required before proceeding" hint while invalid.
  function refreshStepStates() {
    var vis = visibleSteps();
    var aIdx = vis.findIndex(function (s) { return s.def.id === activeId; });
    steps.forEach(function (s) {
      var vIdx = vis.indexOf(s);
      var ok = valid[s.def.id] !== false;
      var passed = vIdx >= 0 && aIdx >= 0 && vIdx < aIdx;
      var isDone = passed && ok && isRequired(s.def);
      s.el.classList.toggle('done', isDone);
      s.el.classList.toggle('show-req', s.def.id === activeId && isRequired(s.def) && !ok);
      if (s.pipEl) s.pipEl.textContent = isDone ? '\u2713' : s.pipNum; // WPF: done shows a check
      // Collapsed summary line: hidden on the active step, "Waiting..." for future steps,
      // the tool's summary for steps already passed (matches WPF).
      if (s.summaryEl) {
        if (s.def.id === activeId) { s.summaryEl.style.display = 'none'; }
        else if (vIdx > aIdx)      { s.summaryEl.style.display = ''; s.summaryEl.textContent = 'Waiting...'; }
        else                       { s.summaryEl.style.display = ''; s.summaryEl.textContent = s.summaryText || ''; }
      }
    });
  }
  function pushLog(text, status) {
    showRun();
    var line = U.el('div', status || 'info', text);
    logEl.appendChild(line); logEl.scrollTop = logEl.scrollHeight;
  }
  function setProgress(p) {
    running = true; setStatus('Running...', 'accent');
    setProgressBar(Math.max(0, Math.min(100, p.pct || 0)));
    setCounts(p.pass, p.fail, p.skip);
    if (runBtn) runBtn.disabled = true;
  }
  function complete(r) {
    running = false; finished = true; finishCls = (r.fail > 0) ? 'fail' : 'pass';
    setStatus((r.fail > 0) ? 'Stopped' : 'Done', finishCls);
    setProgressBar(100); renderPips();
    setCounts(r.pass, r.fail, r.skip);
    pushLog('Done - ' + (r.pass || 0) + ' passed, ' + (r.fail || 0) + ' failed, ' + (r.skip || 0) + ' skipped.', finishCls);
    if (runBtn) runBtn.disabled = !canRun;
  }
  function setTitle(t) { var el = container.querySelector('.toolbar .title'); if (el) el.textContent = t; }

  // ── Top-strip chrome helpers ───────────────────────────────────────────────
  function setStatus(text, cls) {
    if (!statusEl) return;
    statusEl.className = 'status ' + (cls || 'accent');
    var txt = statusEl.querySelector('.txt'); if (txt) txt.textContent = text;
  }
  function setProgressBar(pct) { if (fillEl) fillEl.style.width = pct + '%'; }
  // Step-completion pips: done (green) for passed steps, accent for the active step, grey for
  // future ones; all green/red after a run finishes. Matches the WPF pip row.
  function renderPips() {
    if (!pipsEl) return;
    var vis = visibleSteps();
    var aIdx = vis.findIndex(function (s) { return s.def.id === activeId; });
    pipsEl.innerHTML = '';
    for (var i = 0; i < vis.length; i++) {
      var cls = 'pip';
      if (finished)      cls += (finishCls === 'fail') ? ' fail' : ' done';
      else if (i < aIdx) cls += ' done';
      else if (i === aIdx) cls += ' active';
      pipsEl.appendChild(U.el('div', cls));
    }
  }
  function setCounts(pass, fail, skip) {
    if (!countsEl) return;
    countsEl.innerHTML = '';
    countsEl.appendChild(U.el('span', 'pass', (pass || 0) + ' pass'));
    countsEl.appendChild(U.el('span', 'fail', (fail || 0) + ' fail'));
    countsEl.appendChild(U.el('span', 'skip', (skip || 0) + ' skip'));
  }
  function updateChip() {
    if (!chipEl) return;
    var vis = visibleSteps();
    var idx = vis.findIndex(function (s) { return s.def.id === activeId; });
    chipEl.textContent = 'Step ' + (idx < 0 ? 1 : idx + 1) + ' / ' + (vis.length || 1);
  }

  // ── Build ──────────────────────────────────────────────────────────────────
  function isRequired(def) { return def.required !== false; }
  function visibleSteps() { return steps.filter(function (s) { return !s.el.classList.contains('hidden'); }); }

  function build() {
    container.innerHTML = '';
    container.className = 'l-flow';

    // ── Toolbar: mono title | step chip + minimize + close ───────────────────
    var toolbar = U.el('div', 'toolbar');
    toolbar.appendChild(U.el('span', 'title', spec.title || ''));
    var tools = U.el('div', 'tools');
    chipEl = U.el('div', 'step-chip', 'Step 1 / 1');
    tools.appendChild(chipEl);
    var minBtn = U.el('button', 'win-btn', '\u2013'); minBtn.type = 'button'; minBtn.title = 'Minimize';
    minBtn.addEventListener('click', function () { send('action', { action: 'minimize' }); });
    var closeBtn = U.el('button', 'win-btn close', '\u00D7'); closeBtn.type = 'button'; closeBtn.title = 'Close';
    closeBtn.addEventListener('click', function () { send('action', { action: 'close' }); });
    tools.appendChild(minBtn); tools.appendChild(closeBtn);
    toolbar.appendChild(tools);
    // Drag-to-move: mousedown on the bar (not a button) starts a native window move (C# side).
    toolbar.addEventListener('mousedown', function (e) {
      if (e.button !== 0 || (e.target.closest && e.target.closest('.win-btn'))) return;
      send('action', { action: 'drag' });
    });
    container.appendChild(toolbar);

    // ── Status / progress strip: status + progress bar + counts, then a pip row ──
    finished = false;
    var strip = U.el('div', 'strip');
    var statusRow = U.el('div', 'status-row');
    statusEl = U.el('div', 'status accent');
    statusEl.appendChild(U.el('span', 'dot'));
    statusEl.appendChild(U.el('span', 'txt', 'Configuring...'));
    var bar = U.el('div', 'bar'); fillEl = U.el('div', 'fill'); bar.appendChild(fillEl);
    countsEl = U.el('div', 'counts');
    statusRow.appendChild(statusEl); statusRow.appendChild(bar); statusRow.appendChild(countsEl);
    pipsEl = U.el('div', 'pips');
    strip.appendChild(statusRow); strip.appendChild(pipsEl);
    container.appendChild(strip);
    setCounts(0, 0, 0);

    // ── Content + steps ──────────────────────────────────────────────────────
    var content = U.el('div', 'content');
    var stepsEl = U.el('div', 'l-steps');
    steps = [];
    (spec.steps || []).forEach(function (def, i) { steps.push(buildStep(def, i)); });
    steps.forEach(function (s) { stepsEl.appendChild(s.el); });
    content.appendChild(stepsEl);

    // Run log (log only; progress now lives in the top strip), hidden until a run starts.
    runEl = U.el('div', 'l-run l-hidden');
    logEl = U.el('div', 'l-log');
    runEl.appendChild(logEl);
    content.appendChild(runEl);
    container.appendChild(content);

    // ── Footer ───────────────────────────────────────────────────────────────
    var footer = U.el('div', 'footer');
    backBtn = U.button({ label: '← Back', variant: 'ghost', onClick: goBack }).el; // LEFTWARDS ARROW
    var mid = U.el('div', 'mid');
    var resetBtn = U.button({ label: 'Reset', variant: 'ghost', onClick: function () { send('action', { action: 'reset' }); } }).el;
    // Pausable-run buttons (IWebRunPausable) - hidden until a `pause` message arrives.
    continueBtn = U.button({ label: 'Continue', variant: 'primary', onClick: function () { send('action', { action: 'continueRun' }); } }).el;
    skipBtn = U.button({ label: 'Skip', variant: 'ghost', onClick: function () { send('action', { action: 'skipItem' }); } }).el;
    continueBtn.style.display = 'none'; skipBtn.style.display = 'none';
    mid.appendChild(resetBtn); mid.appendChild(continueBtn); mid.appendChild(skipBtn);
    var runHandle = U.button({ label: spec.runLabel || 'Run', variant: 'primary', onClick: function () {
      running = true; setStatus('Running...', 'accent'); showRun(); send('action', { action: 'run' });
    } });
    runBtn = runHandle.el; runBtn.disabled = true;
    footer.appendChild(backBtn);
    footer.appendChild(mid);
    footer.appendChild(runBtn);
    container.appendChild(footer);

    activeId = (spec.steps && spec.steps.length) ? spec.steps[0].id : null;
    paintActive();
  }

  function buildStep(def, index) {
    var el = U.el('div', 'l-step' + (def.hidden ? ' hidden' : ''));
    var head = U.el('div', 'head');
    var pip = U.el('div', 'pip', String(index + 1));
    var titles = U.el('div', 'titles');
    var title = U.el('div', 'title');
    title.appendChild(U.el('span', 'ttext', def.title || ''));
    if (isRequired(def)) title.appendChild(U.el('span', 'req', '*'));
    var summary = U.el('div', 'summary', def.summary || '');
    titles.appendChild(title); titles.appendChild(summary);
    head.appendChild(pip); head.appendChild(titles);
    // Navigation is Confirm / Back only (matches the WPF step flow) - no free header jumping.

    var body = U.el('div', 'body');
    var inputsEl = U.el('div', 'inputs');
    var inputs = {};
    (def.inputs || []).forEach(function (inp) {
      var row = buildInput(inp, def.id);
      if (row) { inputs[inp.id] = row.handle; inputsEl.appendChild(row.el); }
    });
    body.appendChild(inputsEl);

    // Auto "Required before proceeding" hint (shown by refreshStepStates when active + invalid).
    var reqHint = U.el('div', 'req-hint');
    reqHint.textContent = '\u2717 Required before proceeding'; // BALLOT X
    body.appendChild(reqHint);

    // Non-last steps get a Confirm button that advances to the next visible step.
    var confirmBtn = null;
    var isLast = index === (spec.steps.length - 1);
    if (!isLast) {
      var crow = U.el('div', 'confirm-row');
      var handle = U.button({ label: def.confirmLabel || 'Confirm →', variant: 'primary', onClick: function () { // RIGHTWARDS ARROW
        send('action', { action: 'confirm', stepId: def.id });
        goNext();
      } });
      confirmBtn = handle.el;
      crow.appendChild(confirmBtn);
      body.appendChild(crow);
    }

    el.appendChild(head); el.appendChild(body);
    return { def: def, el: el, bodyEl: body, inputsEl: inputsEl, pipEl: pip, pipNum: String(index + 1),
             summaryEl: summary, summaryText: def.summary || '', confirmBtn: confirmBtn, inputs: inputs };
  }

  // Rebuild one step's inputs live (C# -> JS `stepInputs`), e.g. to refresh the review
  // step's summary as earlier steps change. Only replaces the inputs container, leaving the
  // step's Confirm button and the rest of the accordion untouched.
  function setStepInputs(id, inputs) {
    var s = steps.filter(function (x) { return x.def.id === id; })[0];
    if (!s || !s.inputsEl) return;
    s.inputsEl.innerHTML = '';
    (inputs || []).forEach(function (inp) {
      var row = buildInput(inp, id);
      if (row) s.inputsEl.appendChild(row.el);
    });
  }

  // Maps a spec input to a lemoine.js factory and wires its change to a `state` message.
  function buildInput(inp, stepId) {
    function onChange(value) { send('state', { stepId: stepId, inputId: inp.id, value: value }); }
    var handle, el;
    switch (inp.kind) {
      case 'stepper':
        handle = U.stepper({ min: inp.min, max: inp.max, step: inp.step, decimals: inp.decimals,
                             value: inp.value, onChange: onChange });
        el = labeledRow(inp.label, handle.el); break;
      case 'toggle':
        handle = U.toggle({ label: inp.label, checked: inp.checked, disabled: inp.disabled, onChange: onChange });
        el = handle.el; break;
      case 'textField':
        handle = U.textField({ label: inp.label, value: inp.value, placeholder: inp.placeholder,
                               multiline: inp.multiline, onChange: onChange });
        el = handle.el; break;
      case 'singleSelect':
        handle = U.singleSelect({ options: inp.options, value: inp.value, onChange: onChange });
        el = labeledRow(inp.label, handle.el, true); break;
      case 'multiSelectTabs':
        handle = U.multiSelectTabs({ groups: inp.groups, selected: inp.selected, singleSelect: inp.singleSelect,
                                     hierarchy: inp.hierarchy, disabledItems: inp.disabledItems,
                                     height: inp.height || '232px', onChange: onChange });
        el = labeledRow(inp.label, handle.el, true); break;
      case 'folderBrowser':
      case 'fileBrowser':
        var browseAction = inp.kind === 'fileBrowser' ? 'browseFile' : 'browseFolder';
        var mk = inp.kind === 'fileBrowser' ? U.fileBrowser : U.folderBrowser;
        handle = mk({ value: inp.value, placeholder: inp.placeholder,
                      onBrowse: function () { send('action', { action: browseAction, stepId: stepId, inputId: inp.id, filter: inp.filter || null }); } });
        el = labeledRow(inp.label, handle.el, true); break;
      case 'browserTree':
        handle = U.browserTree({ roots: inp.roots, selected: inp.selected, singleSelect: inp.singleSelect,
                                 onChange: onChange });
        el = labeledRow(inp.label, handle.el, true); break;
      case 'numberRange':
        handle = U.numberRange({ min: inp.min, max: inp.max, lo: inp.lo, hi: inp.hi,
                                 step: inp.step, decimals: inp.decimals, onChange: onChange });
        el = labeledRow(inp.label, handle.el, true); break;
      case 'searchSelect':
        handle = U.searchSelect({ options: inp.options, value: inp.value, placeholder: inp.placeholder,
                                  onChange: onChange });
        el = labeledRow(inp.label, handle.el, true); break;
      case 'actionButton':
        handle = U.button({ label: inp.label, variant: inp.variant,
                            onClick: function () { send('action', { action: 'tool', stepId: stepId, inputId: inp.id }); } });
        el = handle.el; break;
      case 'hint':
        el = U.el('div', 'l-hint', inp.text); handle = { el: el }; break;
      case 'tokenInput':
        handle = U.tokenInput({ value: inp.value, defaultPattern: inp.defaultPattern,
                                groups: inp.groups, sample: inp.sample, onChange: onChange });
        el = labeledRow(inp.label, handle.el, true); break;
      case 'checkList':
        handle = U.checkList({ items: inp.items, onChange: onChange });
        el = labeledRow(inp.label, handle.el, true); break;
      case 'review':
        handle = U.review({ items: inp.items, note: inp.note, warning: inp.warning });
        el = handle.el; break;
      case 'warn':
        handle = U.warnBanner(inp.text); el = handle.el; break;
      default:
        el = U.el('div', 'l-hint', 'Unknown input kind: ' + inp.kind);
        handle = { el: el };
    }
    return { el: el, handle: handle };
  }

  function labeledRow(label, inner, stacked) {
    if (!label) return inner;
    var row = U.el('div', 'field-row');
    row.appendChild(U.el('div', 'rlabel', label));
    row.appendChild(inner);
    return row;
  }

  // ── Navigation ───────────────────────────────────────────────────────────
  function activate(id) { activeId = id; paintActive(); send('action', { action: 'navigate', stepId: id }); }
  function paintActive() {
    steps.forEach(function (s) { s.el.classList.toggle('active', s.def.id === activeId); });
    var vis = visibleSteps();
    var idx = vis.findIndex(function (s) { return s.def.id === activeId; });
    if (backBtn) backBtn.disabled = idx <= 0;
    updateChip();
    refreshStepStates();
    renderPips();
  }
  function goNext() {
    var vis = visibleSteps();
    var idx = vis.findIndex(function (s) { return s.def.id === activeId; });
    if (idx >= 0 && idx < vis.length - 1) activate(vis[idx + 1].def.id);
  }
  function goBack() {
    var vis = visibleSteps();
    var idx = vis.findIndex(function (s) { return s.def.id === activeId; });
    if (idx > 0) activate(vis[idx - 1].def.id);
  }

  function showRun() { if (runEl) runEl.classList.remove('l-hidden'); }

  return { init: init, applyValidation: applyValidation, pushLog: pushLog,
           setProgress: setProgress, complete: complete, setTitle: setTitle,
           setStepInputs: setStepInputs,
           setPause: function (p) {
             if (!continueBtn) return;
             var on = !!(p && p.awaiting);
             continueBtn.style.display = on ? '' : 'none';
             skipBtn.style.display     = on ? '' : 'none';
             if (on) {
               continueBtn.textContent = (p.continueLabel || 'Continue');
               skipBtn.textContent     = (p.skipLabel || 'Skip');
             }
           },
           // Push a value into one input's display (e.g. a folder path chosen by the C# dialog).
           setInput: function (stepId, inputId, value) {
             var s = steps.filter(function (x) { return x.def.id === stepId; })[0];
             if (s && s.inputs[inputId] && s.inputs[inputId].setValue) s.inputs[inputId].setValue(value);
           },
           setStepSummary: function (id, text) {
             var s = steps.filter(function (x) { return x.def.id === id; })[0];
             if (s) { s.summaryText = text; refreshStepStates(); } // state decides Waiting vs summary
           },
           setStepHidden: function (id, hidden) {
             var s = steps.filter(function (x) { return x.def.id === id; })[0];
             if (s) s.el.classList.toggle('hidden', !!hidden);
           } };
};
