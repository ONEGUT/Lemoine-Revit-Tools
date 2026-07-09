// Tools Overview — WebView2 pilot renderer.
//
// Fully data-driven: every string comes from the host (ToolsOverviewWebWindow),
// which serializes ToolsOverviewCatalog.Categories + the active ThemePalette/UiSize
// to JSON and pushes it via CoreWebView2.PostWebMessageAsJson. This file holds no
// tool text of its own — see plan-design-twin-parity-and-webview2-overview.md Part B2.
//
// Host -> page messages (JSON):
//   { type: "state", theme: "dark-mono", categories: [...], activeCategoryId: "setup" }
// Page -> host messages (JSON), sent via chrome.webview.postMessage:
//   { type: "ready" }
//   { type: "selectCategory", categoryId: "..." }
//   { type: "runDemo", toolName: "..." }
//   { type: "close" }

(function () {
  var state = null;

  function post(msg) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(msg);
    }
  }

  function el(tag, className, text) {
    var e = document.createElement(tag);
    if (className) e.className = className;
    if (text != null) e.textContent = text;
    return e;
  }

  function render() {
    document.documentElement.setAttribute("data-theme", state.theme || "dark-mono");
    var root = document.getElementById("root");
    root.innerHTML = "";

    var win = el("div", "ov-window");

    // Toolbar
    var toolbar = el("div", "ov-toolbar");
    toolbar.appendChild(el("span", "ov-toolbar-title", state.windowTitle || ""));
    var closeBtn = el("div", "ov-close", "×");
    closeBtn.onclick = function () { post({ type: "close" }); };
    toolbar.appendChild(closeBtn);
    win.appendChild(toolbar);

    // Tab strip
    var tabstrip = el("div", "ov-tabstrip");
    var activeCat = null;
    (state.categories || []).forEach(function (cat) {
      var tab = el("div", "ov-tab" + (cat.id === state.activeCategoryId ? " active" : ""));
      tab.appendChild(el("span", "ov-tab-label", cat.name));
      tab.onclick = function () { post({ type: "selectCategory", categoryId: cat.id }); };
      tabstrip.appendChild(tab);
      if (cat.id === state.activeCategoryId) activeCat = cat;
    });
    win.appendChild(tabstrip);

    // Body
    var body = el("div", "ov-body");
    var cards = el("div", "ov-cards");
    if (activeCat) {
      cards.appendChild(el("div", "ov-cards-title", activeCat.name));
      cards.appendChild(el("div", "ov-cards-intro", activeCat.intro));
      (activeCat.tools || []).forEach(function (tool) {
        var card = el("div", "ov-card");
        var top = el("div", "ov-card-top");
        top.appendChild(el("div", "ov-card-name", tool.name));
        if (tool.hasDemo) {
          var run = el("div", "ov-run", state.runButtonLabel || "Run");
          run.onclick = function () { post({ type: "runDemo", toolName: tool.name }); };
          top.appendChild(run);
        }
        card.appendChild(top);
        card.appendChild(el("div", "ov-card-blurb", tool.blurb));
        cards.appendChild(card);
      });
    }
    body.appendChild(cards);
    win.appendChild(body);

    // Footer
    var footer = el("div", "ov-footer");
    footer.appendChild(el("span", "ov-footer-hint", state.footerHint || ""));
    win.appendChild(footer);

    root.appendChild(win);
  }

  function showError(message) {
    var root = document.getElementById("root");
    root.innerHTML = "";
    root.appendChild(el("div", "ov-error", message));
  }

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener("message", function (e) {
      var data = e.data;
      if (data && data.type === "state") {
        state = data;
        render();
      }
    });
    post({ type: "ready" });
  } else {
    // Standalone preview (opened directly in a browser, no WebView2 host) — render
    // the bundled sample so the page is never blank during design-twin QA. Inlined
    // via a <script type="application/json"> tag rather than fetch()'d: fetching a
    // sibling file over file:// is CORS-blocked in Chromium, and the real WebView2
    // host never uses file:// anyway (it serves via SetVirtualHostNameToFolderMapping).
    var sampleTag = document.getElementById("sample-state");
    if (sampleTag) {
      try {
        state = JSON.parse(sampleTag.textContent);
        render();
      } catch (ex) {
        showError("sample-state could not be parsed: " + ex.message);
      }
    } else {
      showError("No WebView2 host and no #sample-state fallback present.");
    }
  }
})();
