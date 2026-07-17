using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Minimal, dependency-free JSON <b>writer</b> — the serialization counterpart to
    /// <see cref="MiniJson"/> (which only reads). Kept deliberately tiny and NuGet-free for
    /// the same reason MiniJson is (see CLAUDE.md "Text Externalization" — no
    /// System.Text.Json / Newtonsoft, which drag System.Web into the WPF XAML compiler).
    ///
    /// Serializes the value shapes the WebView2 bridge produces: <see langword="null"/>,
    /// <see cref="string"/>, <see cref="bool"/>, any numeric type, an
    /// <see cref="IDictionary{TKey,TValue}"/> with string keys (→ JSON object), and any other
    /// <see cref="IEnumerable"/> (→ JSON array). Anything else is written as its
    /// <c>ToString()</c> string, so an unexpected type degrades to a quoted string rather than
    /// throwing mid-serialize.
    /// </summary>
    internal static class WebJson
    {
        // U+2028 LINE SEPARATOR / U+2029 PARAGRAPH SEPARATOR are valid in JSON but break a
        // <script>-embedded JS string literal, so they are always \u-escaped.
        private const char LineSep = '\u2028';
        private const char ParaSep = '\u2029';

        public static string Serialize(object? value)
        {
            var sb = new StringBuilder(128);
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object? value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case float f:
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case decimal m:
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    break;
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    sb.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                    break;
                case IDictionary dict:
                    WriteObject(sb, dict);
                    break;
                case IEnumerable seq:
                    WriteArray(sb, seq);
                    break;
                default:
                    WriteString(sb, value.ToString() ?? "");
                    break;
            }
        }

        private static void WriteObject(StringBuilder sb, IDictionary dict)
        {
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, entry.Key?.ToString() ?? "");
                sb.Append(':');
                Write(sb, entry.Value);
            }
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable seq)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in seq)
            {
                if (!first) sb.Append(',');
                first = false;
                Write(sb, item);
            }
            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20 || c == LineSep || c == ParaSep)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
