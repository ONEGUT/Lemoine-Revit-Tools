using System;
using System.Collections.Generic;

namespace LemoineTools.Framework
{
    /// <summary>
    /// Orders strings the way a person reads a sheet number: digit runs compare by VALUE, everything
    /// else compares as text. Plain alphabetical ordering puts <c>C.17</c> before <c>C.2</c> because
    /// it compares '1' against '2' one character at a time, which is wrong for every numbered sheet
    /// series, level name and view name in a project.
    ///
    /// Use this for any user-facing list of sheets, views or other numbered names —
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> is only correct for lists that carry no numbers.
    ///
    /// Digit runs are compared without parsing, so an arbitrarily long run of digits can never
    /// overflow: leading zeros are skipped, then the longer run is the larger number, and equal
    /// lengths fall through to a plain digit-by-digit comparison. Two strings that differ only in
    /// leading zeros (<c>A01</c> vs <c>A1</c>) compare equal numerically, so an ordinal comparison
    /// breaks the tie and keeps the sort deterministic.
    /// </summary>
    public sealed class NaturalOrderComparer : IComparer<string>
    {
        /// <summary>Shared case-insensitive instance — the comparer holds no state.</summary>
        public static readonly NaturalOrderComparer OrdinalIgnoreCase = new NaturalOrderComparer();

        private NaturalOrderComparer() { }

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int xs = i, ys = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;

                    int c = CompareDigitRun(x, xs, i, y, ys, j);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }

            // Whichever still has characters left is the longer name, so it sorts after.
            int rest = (x.Length - i).CompareTo(y.Length - j);
            if (rest != 0) return rest;

            // Numerically identical (e.g. "A01" vs "A1") — settle it so the order is stable.
            return string.CompareOrdinal(x, y);
        }

        /// <summary>Compares two digit runs by value without parsing them into a number.</summary>
        private static int CompareDigitRun(string x, int xs, int xe, string y, int ys, int ye)
        {
            while (xs < xe - 1 && x[xs] == '0') xs++;   // keep the last digit, so "000" reads as "0"
            while (ys < ye - 1 && y[ys] == '0') ys++;

            int lenCmp = (xe - xs).CompareTo(ye - ys);
            if (lenCmp != 0) return lenCmp;             // more significant digits ⇒ larger number

            for (int a = xs, b = ys; a < xe; a++, b++)
            {
                int c = x[a].CompareTo(y[b]);
                if (c != 0) return c;
            }
            return 0;
        }
    }
}
