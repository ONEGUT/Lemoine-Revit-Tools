using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Minimal static CSV parser. No external dependencies.
    /// Handles quoted fields and commas within quoted fields.
    /// </summary>
    public static class CsvParser
    {
        /// <summary>
        /// Parses a CSV file and returns all rows as string arrays.
        /// The first item is the header row.
        /// </summary>
        public static List<string[]> Parse(string filePath)
        {
            var rows = new List<string[]>();
            if (!File.Exists(filePath)) return rows;

            using (var sr = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    rows.Add(ParseLine(line));
                }
            }
            return rows;
        }

        /// <summary>
        /// Parses a CSV file and returns rows as dictionaries keyed by header name.
        /// </summary>
        public static List<Dictionary<string, string>> ParseAsDicts(string filePath)
        {
            var all     = Parse(filePath);
            var result  = new List<Dictionary<string, string>>();
            if (all.Count == 0) return result;

            var headers = all[0];
            for (int i = 1; i < all.Count; i++)
            {
                var row  = all[i];
                var dict = new Dictionary<string, string>();
                for (int j = 0; j < headers.Length; j++)
                    dict[headers[j]] = j < row.Length ? row[j] : "";
                result.Add(dict);
            }
            return result;
        }

        // ── Private ───────────────────────────────────────────────────────────

        private static string[] ParseLine(string line)
        {
            var fields  = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // Check for escaped quote ("")
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++; // skip second quote
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == ',')
                    {
                        fields.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
            }

            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}
