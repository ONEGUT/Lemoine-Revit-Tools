using System;
using System.Collections.Generic;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// The JS &lt;-&gt; C# message router for one WebView2 control (rules R17-R21).
    ///
    /// Wire shape, both directions: <c>{ "type": string, "id": string?, "payload": object }</c>.
    ///   JS  -&gt; C#: <c>chrome.webview.postMessage(JSON.stringify(msg))</c>
    ///   C#  -&gt; JS: <c>CoreWebView2.PostWebMessageAsString(json)</c>, dispatched page-side by a
    ///                <c>chrome.webview.addEventListener('message', ...)</c> handler.
    ///
    /// Outbound messages sent before the page signals ready are queued and flushed on the
    /// page's <c>ready</c> message (R18) - a message posted into a not-yet-loaded page is
    /// otherwise dropped silently. Inbound messages with no registered handler are logged,
    /// never dropped (R20).
    ///
    /// Thread affinity: construct and call on the WebView2's own dispatcher (STA) thread.
    /// Callbacks arriving from Revit's thread (run log/progress) must be marshalled by the
    /// caller before calling <see cref="Send"/>, exactly as the ViewModel already marshals to
    /// StepFlowWindow via SafeBeginInvoke.
    /// </summary>
    public sealed class WebBridge
    {
        private readonly CoreWebView2 _core;
        private readonly Dictionary<string, Action<IReadOnlyDictionary<string, object?>>> _handlers
            = new Dictionary<string, Action<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
        private readonly List<string> _outboundQueue = new List<string>();
        private bool _pageReady;

        /// <summary>Raised for every inbound message, after any type-specific handler runs.
        /// Args: (type, payload). Useful for logging/diagnostics sinks (e.g. the harness echo log).</summary>
        public event Action<string, IReadOnlyDictionary<string, object?>>? MessageReceived;

        public WebBridge(CoreWebView2 core)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));
            _core.WebMessageReceived += OnWebMessageReceived;
        }

        /// <summary>Register a handler for one message <paramref name="type"/>. The built-in
        /// <c>ready</c> type is handled internally; registering it additionally is allowed.</summary>
        public void On(string type, Action<IReadOnlyDictionary<string, object?>> handler)
        {
            if (string.IsNullOrEmpty(type) || handler == null) return;
            _handlers[type] = handler;
        }

        /// <summary>Send a message to the page. Queued until the page's <c>ready</c> arrives (R18).</summary>
        public void Send(string type, object? payload = null)
        {
            var msg = new Dictionary<string, object?>
            {
                ["type"]    = type,
                ["payload"] = payload ?? new Dictionary<string, object?>(),
            };
            string json = WebJson.Serialize(msg);

            if (!_pageReady)
            {
                _outboundQueue.Add(json);
                return;
            }
            PostRaw(json);
        }

        private void PostRaw(string json)
        {
            try { _core.PostWebMessageAsString(json); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebBridge: post message", ex); }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.TryGetWebMessageAsString(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("WebBridge: read inbound message", ex);
                return;
            }

            string type;
            IReadOnlyDictionary<string, object?> payload;
            try
            {
                var parsed = MiniJson.Parse(raw) as Dictionary<string, object?>;
                if (parsed == null)
                {
                    DiagnosticsLog.Warn("WebBridge: malformed message (not an object)", Trim(raw));
                    return;
                }
                type = parsed.TryGetValue("type", out var t) ? t as string ?? "" : "";
                payload = parsed.TryGetValue("payload", out var p) && p is Dictionary<string, object?> pd
                    ? pd
                    : Empty;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("WebBridge: unparseable message", $"{Trim(raw)} - {ex.Message}");
                return;
            }

            if (string.IsNullOrEmpty(type))
            {
                DiagnosticsLog.Warn("WebBridge: message missing type", Trim(raw));
                return;
            }

            if (type == "ready")
            {
                _pageReady = true;
                Flush();
            }

            if (_handlers.TryGetValue(type, out var handler))
            {
                try { handler(payload); }
                catch (Exception ex) { DiagnosticsLog.Error($"WebBridge: handler for '{type}'", ex); }
            }
            else if (type != "ready")
            {
                DiagnosticsLog.Warn("WebBridge: no handler for message type", type); // R20 - never a silent drop
            }

            try { MessageReceived?.Invoke(type, payload); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebBridge: MessageReceived subscriber", ex); }
        }

        private void Flush()
        {
            foreach (var json in _outboundQueue) PostRaw(json);
            _outboundQueue.Clear();
        }

        private static string Trim(string s) => s.Length <= 200 ? s : s.Substring(0, 200) + "...";

        private static readonly IReadOnlyDictionary<string, object?> Empty =
            new Dictionary<string, object?>();
    }
}
