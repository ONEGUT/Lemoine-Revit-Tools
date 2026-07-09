using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LemoineTools.Framework;

namespace LemoineTools.Preview.Parity
{
    /// <summary>
    /// Walks a rendered WPF visual tree and writes the exact per-element bounds,
    /// text, font size, corner radius, and visibility to JSON — the WPF half of
    /// the design-twin parity harness (see plan-design-twin-parity-and-webview2-overview.md).
    ///
    /// Element identity for joining against the twin's measurement JSON:
    ///   1. FrameworkElement.Uid, if set (the parity tagging convention used on
    ///      ToolsOverviewWindow — see its Uid="card-..." etc. assignments)
    ///   2. FrameworkElement.Name, if set (x:Name'd chrome elements)
    ///   3. A structural path (Type[siblingIndex]/Type[siblingIndex]/...) as a
    ///      last resort, so no element is silently dropped from the report even
    ///      if it has no stable tag yet.
    ///
    /// No JSON library is referenced (CLAUDE.md: System.Web.Extensions drags
    /// System.Web into the XAML compiler and breaks the build) — this writes a
    /// small, fixed schema by hand.
    /// </summary>
    internal static class SnapshotExporter
    {
        public sealed class ElementBounds
        {
            public double X, Y, Width, Height;
            public string Type = "";
            public string? Text;
            public double? FontSize;
            public double? CornerRadius;
            public string Visibility = "Visible";
            public bool HasUid;
        }

        /// <summary>
        /// Captures <paramref name="root"/>'s current (already-laid-out) visual tree
        /// and writes both the measurement JSON and a PNG screenshot to <paramref name="outDir"/>
        /// under the given <paramref name="surfaceId"/> (e.g. "tools-overview.setup.dark-mono.medium").
        /// </summary>
        public static void Capture(FrameworkElement root, string surfaceId, string outDir)
        {
            Directory.CreateDirectory(outDir);

            var elements = new Dictionary<string, ElementBounds>();
            var pathCounters = new Dictionary<string, int>();
            Walk(root, root, "", pathCounters, elements);

            string jsonPath = Path.Combine(outDir, surfaceId + ".json");
            File.WriteAllText(jsonPath, BuildJson(surfaceId, root, elements), Encoding.UTF8);

            string pngPath = Path.Combine(outDir, surfaceId + ".png");
            SavePng(root, pngPath);
        }

        private static void Walk(
            FrameworkElement root,
            DependencyObject node,
            string parentPath,
            Dictionary<string, int> pathCounters,
            Dictionary<string, ElementBounds> outElements)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement fe)
                {
                    string id = ResolveId(fe, parentPath, pathCounters);
                    outElements[id] = Measure(root, fe, id);
                    Walk(root, fe, id, pathCounters, outElements);
                }
                else
                {
                    // Non-FrameworkElement visuals (e.g. a Drawing) can't carry a Uid/Name
                    // and aren't part of the parity contract — descend without recording.
                    Walk(root, child, parentPath, pathCounters, outElements);
                }
            }
        }

        private static string ResolveId(FrameworkElement fe, string parentPath, Dictionary<string, int> pathCounters)
        {
            if (!string.IsNullOrEmpty(fe.Uid)) return fe.Uid;
            if (!string.IsNullOrEmpty(fe.Name)) return fe.Name;

            string typeName = fe.GetType().Name;
            string key = parentPath + "/" + typeName;
            pathCounters.TryGetValue(key, out int idx);
            pathCounters[key] = idx + 1;
            return $"{key}[{idx}]";
        }

        private static ElementBounds Measure(FrameworkElement root, FrameworkElement fe, string id)
        {
            var bounds = new ElementBounds
            {
                Type = fe.GetType().Name,
                Visibility = fe.Visibility.ToString(),
                HasUid = !string.IsNullOrEmpty(fe.Uid),
            };

            try
            {
                var transform = fe.TransformToAncestor(root);
                var rect = transform.TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                bounds.X = Math.Round(rect.X, 2);
                bounds.Y = Math.Round(rect.Y, 2);
                bounds.Width = Math.Round(rect.Width, 2);
                bounds.Height = Math.Round(rect.Height, 2);
            }
            catch (InvalidOperationException ex)
            {
                // Not connected to root's visual tree (e.g. mid-teardown) — reported as a
                // real 0x0 rather than silently missing from the file, but the reason is
                // still logged since a capture full of unexpected 0x0s would otherwise be
                // indistinguishable from a genuinely broken exporter.
                DiagnosticsLog.Swallowed($"SnapshotExporter: element '{id}' not connected to capture root", ex);
            }

            bounds.Text = ExtractText(fe);

            object fontSizeVal = fe.GetValue(TextElement.FontSizeProperty);
            if (fontSizeVal is double fs) bounds.FontSize = Math.Round(fs, 2);

            if (fe is Border b)
                bounds.CornerRadius = b.CornerRadius.TopLeft;

            return bounds;
        }

        private static string? ExtractText(FrameworkElement fe)
        {
            switch (fe)
            {
                case TextBlock tb: return tb.Text;
                case ContentControl cc when cc.Content is string s: return s;
                case ContentControl cc when cc.Content is TextBlock innerTb: return innerTb.Text;
                default: return null;
            }
        }

        // ── Minimal hand-rolled JSON writer (fixed schema, see class doc) ──────
        private static string BuildJson(string surfaceId, FrameworkElement root, Dictionary<string, ElementBounds> elements)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"surface\": ").Append(Q(surfaceId)).Append(",\n");
            sb.Append("  \"rootWidth\": ").Append(Num(root.ActualWidth)).Append(",\n");
            sb.Append("  \"rootHeight\": ").Append(Num(root.ActualHeight)).Append(",\n");
            sb.Append("  \"elements\": {\n");

            bool first = true;
            foreach (var kv in elements)
            {
                if (!first) sb.Append(",\n");
                first = false;
                var e = kv.Value;
                sb.Append("    ").Append(Q(kv.Key)).Append(": {");
                sb.Append("\"x\":").Append(Num(e.X)).Append(',');
                sb.Append("\"y\":").Append(Num(e.Y)).Append(',');
                sb.Append("\"width\":").Append(Num(e.Width)).Append(',');
                sb.Append("\"height\":").Append(Num(e.Height)).Append(',');
                sb.Append("\"type\":").Append(Q(e.Type)).Append(',');
                sb.Append("\"text\":").Append(e.Text == null ? "null" : Q(e.Text)).Append(',');
                sb.Append("\"fontSize\":").Append(e.FontSize.HasValue ? Num(e.FontSize.Value) : "null").Append(',');
                sb.Append("\"cornerRadius\":").Append(e.CornerRadius.HasValue ? Num(e.CornerRadius.Value) : "null").Append(',');
                sb.Append("\"visibility\":").Append(Q(e.Visibility)).Append(',');
                sb.Append("\"hasUid\":").Append(e.HasUid ? "true" : "false");
                sb.Append('}');
            }

            sb.Append("\n  }\n}\n");
            return sb.ToString();
        }

        private static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static void SavePng(FrameworkElement root, string path)
        {
            int w = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
            int h = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                encoder.Save(fs);
        }
    }
}
