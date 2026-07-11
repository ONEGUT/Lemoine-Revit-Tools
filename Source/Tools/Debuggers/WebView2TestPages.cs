using LemoineTools.Framework;

namespace LemoineTools.Tools.Debuggers
{
    /// <summary>
    /// HTML assets for the WebView2 Test harness: a smoke-test page plus HTML/JS
    /// recreations of two common Lemoine inputs (InlineStepper, MultiSelectTabs).
    /// Colors come from the live <see cref="ThemePalette"/>, substituted into
    /// {{TOKEN}} placeholders at load time so the pages match the active theme.
    /// Pages post plain strings to C# via window.chrome.webview.postMessage; the
    /// same lines are echoed into an in-page log so the page is self-diagnosing
    /// even when the bridge is absent (e.g. opened in a normal browser).
    /// </summary>
    internal static class WebView2TestPages
    {
        internal static string SmokePage(ThemePalette theme)   => Fill(SmokeTemplate, theme);
        internal static string StepperPage(ThemePalette theme) => Fill(StepperTemplate, theme);
        internal static string TabsPage(ThemePalette theme)    => Fill(TabsTemplate, theme);

        private static string Fill(string html, ThemePalette t) => html
            .Replace("{{BG}}",        Hex(t.Bg))
            .Replace("{{SURFACE}}",   Hex(t.Surface))
            .Replace("{{RAISED}}",    Hex(t.Raised))
            .Replace("{{BORDER}}",    Hex(t.Border))
            .Replace("{{TEXT}}",      Hex(t.Text))
            .Replace("{{TEXTSUB}}",   Hex(t.TextSub))
            .Replace("{{TEXTDIM}}",   Hex(t.TextDim))
            .Replace("{{ACCENT}}",    Hex(t.Accent))
            .Replace("{{ACCENTDIM}}", Hex(t.AccentDim))
            .Replace("{{GREEN}}",     Hex(t.Green))
            .Replace("{{RED}}",       Hex(t.Red));

        private static string Hex(System.Windows.Media.SolidColorBrush brush)
        {
            var c = brush.Color;
            return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
        }

        private const string SmokeTemplate = @"
<!doctype html>
<html><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>WebView2 smoke test</title>
<style>
  * { box-sizing:border-box; margin:0; padding:0; }
  html,body { height:100%; }
  body { background:{{BG}}; color:{{TEXT}}; font-family:'Segoe UI',sans-serif; font-size:12px;
         display:flex; align-items:center; justify-content:center; padding:14px; }
  .card { background:{{RAISED}}; border:1px solid {{BORDER}}; border-radius:10px;
          padding:22px 26px; text-align:center; max-width:340px; }
  .ok { color:{{GREEN}}; font-size:30px; line-height:1; margin-bottom:10px; }
  h1 { font-size:15px; font-weight:600; margin-bottom:8px; }
  p { color:{{TEXTSUB}}; margin-bottom:6px; }
  .dim { color:{{TEXTDIM}}; font-family:Consolas,monospace; font-size:10px; word-break:break-all; margin-bottom:14px; }
  button { background:{{ACCENTDIM}}; color:{{ACCENT}}; border:1px solid {{ACCENT}};
           font-family:'Segoe UI',sans-serif; font-size:12px; padding:6px 14px; cursor:pointer; }
  button:hover { background:{{ACCENT}}; color:{{BG}}; }
  .sent { color:{{GREEN}}; font-size:11px; margin-top:8px; visibility:hidden; }
</style></head>
<body>
<div class=""card"">
  <div class=""ok"">&#10003;</div>
  <h1>WebView2 renders</h1>
  <p>If you can read this, environment creation, control initialization, navigation and rendering all work.</p>
  <p class=""dim"" id=""ua""></p>
  <button id=""ping"">Post test message to C#</button>
  <div class=""sent"" id=""sent"">message posted &#10003;</div>
</div>
<script>
  document.getElementById('ua').textContent = navigator.userAgent;
  function post(msg) {
    if (window.chrome && window.chrome.webview) { window.chrome.webview.postMessage(msg); return true; }
    return false;
  }
  document.getElementById('ping').addEventListener('click', function () {
    var ok = post('smoke: ping from JS at ' + new Date().toISOString());
    var sent = document.getElementById('sent');
    sent.textContent = ok ? 'message posted ✓' : 'no bridge — chrome.webview missing';
    sent.style.visibility = 'visible';
  });
  post('smoke: page loaded, UA = ' + navigator.userAgent);
</script>
</body></html>
";

        private const string StepperTemplate = @"
<!doctype html>
<html><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>InlineStepper recreation</title>
<style>
  * { box-sizing:border-box; margin:0; padding:0; }
  body { background:{{BG}}; color:{{TEXT}}; font-family:'Segoe UI',sans-serif; font-size:12px; padding:14px; }
  h1 { font-size:11px; font-weight:600; color:{{TEXTSUB}}; text-transform:uppercase; letter-spacing:.06em; margin-bottom:14px; }
  .row { display:flex; align-items:center; gap:10px; margin-bottom:10px; }
  .row label { width:190px; }
  .stepper { display:inline-flex; align-items:stretch; height:26px; border:1px solid {{BORDER}}; background:{{RAISED}}; }
  .stepper button { border:0; background:{{RAISED}}; color:{{TEXT}}; font-family:Consolas,monospace;
                    font-size:11px; padding:0 9px; cursor:pointer; }
  .stepper button:hover { background:{{ACCENTDIM}}; }
  .stepper .sep { width:1px; background:{{BORDER}}; }
  .stepper input { width:46px; border:0; background:transparent; color:{{TEXT}}; font-family:Consolas,monospace;
                   font-size:11px; text-align:center; outline:none; }
  .hint { color:{{TEXTDIM}}; font-size:11px; margin:6px 0 14px; max-width:420px; }
  .log .t { color:{{TEXTSUB}}; font-size:10px; text-transform:uppercase; letter-spacing:.06em;
            border-top:1px solid {{BORDER}}; padding-top:10px; margin-bottom:6px; }
  .log .lines { font-family:Consolas,monospace; font-size:11px; color:{{TEXTDIM}}; max-height:110px; overflow-y:auto; }
  .log .lines div { margin-bottom:2px; }
  .log .lines b { color:{{ACCENT}}; font-weight:normal; }
</style></head>
<body>
<h1>InlineStepper &mdash; HTML recreation</h1>
<div class=""row"">
  <label>Columns per row (integer, 1&ndash;12)</label>
  <span class=""stepper"" data-id=""columns"" data-min=""1"" data-max=""12"" data-step=""1"" data-dec=""0"" data-val=""4"">
    <button class=""minus"" type=""button"">&minus;</button><span class=""sep""></span><input
      spellcheck=""false""><span class=""sep""></span><button class=""plus"" type=""button"">+</button>
  </span>
</div>
<div class=""row"">
  <label>Paper gap (decimal, 0&ndash;5, step 0.25)</label>
  <span class=""stepper"" data-id=""paperGap"" data-min=""0"" data-max=""5"" data-step=""0.25"" data-dec=""2"" data-val=""0.75"">
    <button class=""minus"" type=""button"">&minus;</button><span class=""sep""></span><input
      spellcheck=""false""><span class=""sep""></span><button class=""plus"" type=""button"">+</button>
  </span>
</div>
<div class=""hint"">Type in the centre field (Enter commits, Esc reverts) or use the &minus; / + buttons.
Values clamp to min/max and round to the configured decimals. Every committed change is posted to C# over the bridge.</div>
<div class=""log"">
  <div class=""t"">Bridge messages (JS &rarr; C#)</div>
  <div class=""lines"" id=""log""></div>
</div>
<script>
  function post(msg) {
    var line = document.createElement('div');
    line.innerHTML = '<b>&rsaquo;</b> ';
    line.appendChild(document.createTextNode(msg));
    var log = document.getElementById('log');
    log.appendChild(line);
    log.scrollTop = log.scrollHeight;
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(msg);
  }
  Array.prototype.forEach.call(document.querySelectorAll('.stepper'), function (el) {
    var min  = parseFloat(el.getAttribute('data-min'));
    var max  = parseFloat(el.getAttribute('data-max'));
    var step = parseFloat(el.getAttribute('data-step'));
    var dec  = parseInt(el.getAttribute('data-dec'), 10);
    var val  = parseFloat(el.getAttribute('data-val'));
    var input = el.querySelector('input');
    function fmt(v) { return v.toFixed(dec); }
    function commit(v, from) {
      if (isNaN(v)) { input.value = fmt(val); return; }              // revert unparseable input
      var c = Math.min(max, Math.max(min, v));
      c = parseFloat(c.toFixed(dec));
      var changed = Math.abs(c - val) > 1e-9;
      val = c;
      input.value = fmt(val);
      if (changed) post('stepper ""' + el.getAttribute('data-id') + '"" = ' + fmt(val) + ' (' + from + ')');
    }
    input.value = fmt(val);
    el.querySelector('.minus').addEventListener('click', function () { commit(val - step, 'button'); });
    el.querySelector('.plus') .addEventListener('click', function () { commit(val + step, 'button'); });
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter')  { commit(parseFloat(input.value), 'typed'); input.blur(); }
      if (e.key === 'Escape') { input.value = fmt(val); input.blur(); }
    });
    input.addEventListener('blur', function () { commit(parseFloat(input.value), 'typed'); });
  });
  post('stepper page ready');
</script>
</body></html>
";

        private const string TabsTemplate = @"
<!doctype html>
<html><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>MultiSelectTabs recreation</title>
<style>
  * { box-sizing:border-box; margin:0; padding:0; }
  body { background:{{BG}}; color:{{TEXT}}; font-family:'Segoe UI',sans-serif; font-size:12px; padding:14px; }
  h1 { font-size:11px; font-weight:600; color:{{TEXTSUB}}; text-transform:uppercase; letter-spacing:.06em; margin-bottom:14px; }
  .mst { display:flex; border:1px solid {{BORDER}}; background:{{RAISED}}; height:232px; }
  .tabs { width:152px; border-right:1px solid {{BORDER}}; overflow-y:auto; padding:4px 0; flex-shrink:0; }
  .tab { display:flex; align-items:center; justify-content:space-between; gap:6px; padding:7px 8px;
         border-left:2px solid transparent; cursor:pointer; font-size:11px; color:{{TEXT}}; }
  .tab:hover { background:{{ACCENTDIM}}; }
  .tab.active { background:{{ACCENTDIM}}; border-left-color:{{ACCENT}}; }
  .tab .lbl { white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .tab .badge { font-family:Consolas,monospace; font-size:10px; color:{{TEXTDIM}}; background:{{RAISED}};
                border:1px solid {{BORDER}}; border-radius:10px; padding:1px 5px; flex-shrink:0; }
  .tab.has .badge { color:{{ACCENT}}; border-color:{{ACCENT}}; background:{{ACCENTDIM}}; }
  .tabsep { height:1px; background:{{BORDER}}; margin:4px 6px; }
  .list { flex:1; overflow-y:auto; padding:8px; }
  .item { display:flex; align-items:center; gap:6px; padding:3px 4px; margin-bottom:2px; cursor:pointer; }
  .item:hover { background:{{ACCENTDIM}}; }
  .item input { accent-color:{{ACCENT}}; margin:0; cursor:pointer; }
  .item.all { font-weight:600; }
  .divider { height:1px; background:{{BORDER}}; margin:3px 0 8px; }
  .empty { color:{{TEXTDIM}}; font-style:italic; padding:4px; }
  .log { margin-top:12px; }
  .log .t { color:{{TEXTSUB}}; font-size:10px; text-transform:uppercase; letter-spacing:.06em;
            border-top:1px solid {{BORDER}}; padding-top:10px; margin-bottom:6px; }
  .log .lines { font-family:Consolas,monospace; font-size:11px; color:{{TEXTDIM}}; max-height:90px; overflow-y:auto; }
  .log .lines div { margin-bottom:2px; }
  .log .lines b { color:{{ACCENT}}; font-weight:normal; }
</style></head>
<body>
<h1>MultiSelectTabs &mdash; HTML recreation</h1>
<div class=""mst"">
  <div class=""tabs"" id=""tabs""></div>
  <div class=""list"" id=""list""></div>
</div>
<div class=""log"">
  <div class=""t"">Bridge messages (JS &rarr; C#)</div>
  <div class=""lines"" id=""log""></div>
</div>
<script>
  var SELECTED_KEY = '__selected__';
  var groups = {
    'Ducts':     ['Supply Air', 'Return Air', 'Exhaust Air', 'Outside Air'],
    'Equipment': ['Air Handling Unit', 'VAV Box', 'Fan Coil Unit', 'Circulation Pump'],
    'Pipes':     ['CHW Supply', 'CHW Return', 'HHW Supply', 'HHW Return', 'Condensate Drain']
  };
  var groupKeys = Object.keys(groups); // already alphabetical; ""Other"" would pin last
  var selected  = {};
  ['Supply Air', 'Return Air', 'CHW Supply'].forEach(function (s) { selected[s] = true; });
  var active = groupKeys[0];

  function selCount()      { return Object.keys(selected).length; }
  function isSel(item)     { return selected[item] === true; }
  function post(msg) {
    var line = document.createElement('div');
    line.innerHTML = '<b>&rsaquo;</b> ';
    line.appendChild(document.createTextNode(msg));
    var log = document.getElementById('log');
    log.appendChild(line);
    log.scrollTop = log.scrollHeight;
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(msg);
  }
  function toggle(item, on) {
    if (on) selected[item] = true; else delete selected[item];
    post('tabs: ' + (on ? '+ ' : '- ') + item + '  (' + selCount() + ' selected)');
    render();
  }

  function makeTab(key, label, count, total) {
    var tab = document.createElement('div');
    tab.className = 'tab' + (key === active ? ' active' : '') + (count > 0 ? ' has' : '');
    var lbl = document.createElement('span');
    lbl.className = 'lbl';
    lbl.textContent = label;
    var badge = document.createElement('span');
    badge.className = 'badge';
    badge.textContent = total < 0 ? String(count) : count + '/' + total;
    tab.appendChild(lbl);
    tab.appendChild(badge);
    tab.addEventListener('click', function () { active = key; render(); });
    return tab;
  }

  function makeItem(label, checked, indeterminate, bold, onToggle) {
    var row = document.createElement('label');
    row.className = 'item' + (bold ? ' all' : '');
    var cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = checked;
    cb.indeterminate = indeterminate;
    cb.addEventListener('change', function () { onToggle(cb.checked); });
    var lbl = document.createElement('span');
    lbl.textContent = label;
    row.appendChild(cb);
    row.appendChild(lbl);
    return row;
  }

  function render() {
    var tabs = document.getElementById('tabs');
    tabs.innerHTML = '';
    tabs.appendChild(makeTab(SELECTED_KEY, 'Selected', selCount(), -1));
    var sep = document.createElement('div');
    sep.className = 'tabsep';
    tabs.appendChild(sep);
    groupKeys.forEach(function (key) {
      var items = groups[key];
      var count = items.filter(isSel).length;
      tabs.appendChild(makeTab(key, key, count, items.length));
    });

    var list = document.getElementById('list');
    list.innerHTML = '';
    if (active === SELECTED_KEY) {
      var sel = Object.keys(selected);
      if (sel.length === 0) {
        var empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = 'No items selected yet.';
        list.appendChild(empty);
        return;
      }
      sel.forEach(function (item) {
        list.appendChild(makeItem(item, true, false, false, function (on) { toggle(item, on); }));
      });
      return;
    }

    var items = groups[active];
    var checkedCount = items.filter(isSel).length;
    var allChecked  = checkedCount === items.length;
    var someChecked = checkedCount > 0 && !allChecked;
    list.appendChild(makeItem('All ' + active, allChecked, someChecked, true, function (on) {
      items.forEach(function (item) { if (on) selected[item] = true; else delete selected[item]; });
      post('tabs: ' + (on ? 'all of ' : 'none of ') + active + '  (' + selCount() + ' selected)');
      render();
    }));
    var divider = document.createElement('div');
    divider.className = 'divider';
    list.appendChild(divider);
    items.forEach(function (item) {
      list.appendChild(makeItem(item, isSel(item), false, false, function (on) { toggle(item, on); }));
    });
  }

  render();
  post('tabs page ready (' + selCount() + ' preselected)');
</script>
</body></html>
";
    }
}
