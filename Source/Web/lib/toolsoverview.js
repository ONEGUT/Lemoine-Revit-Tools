/* Tools Overview (HTML analogue of ToolsOverviewWindow). A read-only field guide: category tabs
   over a scrolling pane of tool cards (icon, blurb, feeds / fed-by chips, example). Category
   switching and feeds/fed-by chip navigation are client-side; a card's "Dummy run" posts a
   runDemo action to C# (opens the demo in a real StepFlowWindow). ASCII-only (rule R13). */
(function () {
  'use strict';

  function toolsOverview(opts) {
    opts = opts || {};
    var send = opts.send || function () {};

    var payload = null, activeCat = '', root = null;
    var toolIndex = {}, catByName = {};   // name -> catId

    function el(t, c, x) { var e = document.createElement(t); if (c) e.className = c; if (x != null) e.textContent = x; return e; }
    function action(a, extra) { var p = { action: a }; if (extra) for (var k in extra) p[k] = extra[k]; send('action', p); }

    function render(host, p) {
      root = host; payload = p;
      toolIndex = {}; catByName = {};
      (p.categories || []).forEach(function (c) {
        catByName[c.name] = c.id;
        (c.tools || []).forEach(function (t) { toolIndex[t.name] = c.id; });
      });
      if (!activeCat || !findCat(activeCat)) activeCat = (p.categories[0] || {}).id || '';
      draw();
    }

    function findCat(id) { return (payload.categories || []).filter(function (c) { return c.id === id; })[0]; }

    function draw() {
      root.innerHTML = '';
      root.appendChild(buildToolbar());
      root.appendChild(buildTabs());
      root.appendChild(buildBody());
      root.appendChild(buildFooter());
    }

    function buildToolbar() {
      var bar = el('div', 'l-set-toolbar');
      var title = el('div', 'title', payload.title || 'Tools Overview');
      title.addEventListener('mousedown', function (e) { if (e.button === 0) action('drag'); });
      bar.appendChild(title);
      var tools = el('div', 'tools');
      [['\u2013', 'minimize', 'Minimize'], ['\u25A1', 'maximize', 'Maximize'], ['\u00d7', 'close', 'Close']]
        .forEach(function (b) { var x = el('button', 'win-btn', b[0]); x.title = b[2]; x.addEventListener('click', function () { action(b[1]); }); tools.appendChild(x); });
      bar.appendChild(tools);
      return bar;
    }

    function buildTabs() {
      var strip = el('div', 'l-ov-tabs');
      (payload.categories || []).forEach(function (c) {
        var tab = el('div', 'l-ov-tab' + (c.id === activeCat ? ' active' : ''));
        tab.appendChild(el('span', 'g', c.glyph));
        tab.appendChild(el('span', 'n', c.name));
        tab.addEventListener('click', function () { activeCat = c.id; draw(); });
        strip.appendChild(tab);
      });
      return strip;
    }

    function buildBody() {
      var body = el('div', 'l-ov-body');
      var cat = findCat(activeCat);
      if (!cat) return body;
      body.appendChild(el('div', 'l-ov-cattitle', cat.name));
      body.appendChild(el('div', 'l-ov-intro', cat.intro));
      (cat.tools || []).forEach(function (t) { body.appendChild(buildCard(t)); });
      return body;
    }

    function buildCard(t) {
      var card = el('div', 'l-ov-card');
      card.setAttribute('data-tool', t.name);

      var top = el('div', 'l-ov-cardtop');
      var left = el('div', 'l-ov-cardleft');
      var iconBox = el('div', 'l-ov-icon'); iconBox.appendChild(el('span', null, t.glyph));
      left.appendChild(iconBox);
      left.appendChild(el('div', 'l-ov-name', t.name));
      top.appendChild(left);
      if (t.hasDemo) {
        var run = el('button', 'l-btn primary l-ov-run', payload.runButton || 'Dummy run');
        run.addEventListener('click', function () { action('runDemo', { tool: t.name }); });
        top.appendChild(run);
      }
      card.appendChild(top);

      card.appendChild(el('div', 'l-ov-blurb', t.blurb));

      var fedBy = relationship(payload.fedByLabel, t.fedBy, false);
      if (fedBy) card.appendChild(fedBy);
      var feeds = relationship(payload.feedsLabel, t.feeds, true);
      if (feeds) card.appendChild(feeds);

      if (t.example) {
        var ex = el('div', 'l-ov-example'); ex.appendChild(el('div', null, t.example));
        card.appendChild(ex);
      }
      return card;
    }

    function relationship(label, items, accent) {
      if (!items || items.length === 0) return null;
      var wrap = el('div', 'l-ov-rel');
      wrap.appendChild(el('span', 'lbl', label));
      items.forEach(function (item) {
        var target = resolveTarget(item);
        var text = accent ? item + ' \u2192' : '\u2190 ' + item;
        var chip = el('span', 'l-ov-chip ' + (accent ? 'feeds' : 'fedby') + (target ? ' link' : ''), text);
        if (target) chip.addEventListener('click', function () { navigateTo(target); });
        wrap.appendChild(chip);
      });
      return wrap;
    }

    // Strip a "(...)" qualifier, then match a tool name, then a category name (ports TryResolveTarget).
    function resolveTarget(raw) {
      var clean = raw; var i = clean.indexOf(' (');
      if (i > 0) clean = clean.substring(0, i);
      clean = clean.trim();
      if (toolIndex.hasOwnProperty(clean)) return { catId: toolIndex[clean], tool: clean };
      if (catByName.hasOwnProperty(clean)) return { catId: catByName[clean], tool: null };
      return null;
    }

    function navigateTo(target) {
      activeCat = target.catId; draw();
      if (!target.tool) return;
      var card = root.querySelector('[data-tool="' + cssEscape(target.tool) + '"]');
      if (card) {
        card.scrollIntoView({ block: 'nearest' });
        card.classList.add('flash');
        setTimeout(function () { card.classList.remove('flash'); }, 1100);
      }
    }
    function cssEscape(s) { return (s || '').replace(/["\\]/g, '\\$&'); }

    function buildFooter() {
      var f = el('div', 'l-set-footer');
      f.appendChild(el('div', 'build', payload.footerHint || ''));
      var close = el('button', 'l-btn', payload.close || 'Close');
      close.addEventListener('click', function () { action('close'); });
      f.appendChild(close);
      return f;
    }

    return { render: render };
  }

  window.Lemoine = window.Lemoine || {};
  window.Lemoine.toolsOverview = toolsOverview;
})();
