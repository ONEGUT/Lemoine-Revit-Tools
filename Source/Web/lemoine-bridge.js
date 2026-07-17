// Shared JS <-> C# bridge (rules R17-R21). One JSON message shape both directions:
//   { "type": string, "id": string?, "payload": object }
// JS -> C#: window.chrome.webview.postMessage(JSON.stringify(msg))
// C# -> JS: CoreWebView2.PostWebMessageAsString(json), delivered to the 'message' listener.
//
// Degrades gracefully with no bridge present (opened in a plain browser for headless
// screenshot verification, rule R14): send() becomes a no-op and hasBridge() reports false,
// so a page's own script never throws for lack of Revit.
window.Lemoine = (function () {
  var hasBridge = !!(window.chrome && window.chrome.webview);
  var listeners = {};
  var errorHooked = false;

  function send(type, payload) {
    if (!hasBridge) return;
    try {
      window.chrome.webview.postMessage(JSON.stringify({ type: type, payload: payload || {} }));
    } catch (e) { /* posting must never throw into page code */ }
  }

  function on(type, fn) { listeners[type] = fn; }

  if (hasBridge) {
    window.chrome.webview.addEventListener('message', function (e) {
      var m;
      try { m = JSON.parse(e.data); } catch (_) { return; }
      if (m && m.type && listeners[m.type]) {
        try { listeners[m.type](m.payload || {}); } catch (err) { reportError('handler:' + m.type, err); }
      }
    });
  }

  // Surface page-side script errors to C# (rule R35) instead of failing silently.
  function reportError(where, err) {
    send('error', { where: where, message: (err && err.message) || String(err),
                    stack: (err && err.stack) || '' });
  }
  if (!errorHooked) {
    errorHooked = true;
    window.addEventListener('error', function (e) {
      send('error', { where: 'window.onerror', message: e.message,
                      source: e.filename, line: e.lineno, col: e.colno });
    });
    window.addEventListener('unhandledrejection', function (e) {
      send('error', { where: 'unhandledrejection',
                      message: (e.reason && e.reason.message) || String(e.reason) });
    });
  }

  // Announce readiness so C# can flush any queued outbound messages (rule R18).
  document.addEventListener('DOMContentLoaded', function () { send('ready', {}); });

  return { send: send, on: on, hasBridge: function () { return hasBridge; }, reportError: reportError };
})();
