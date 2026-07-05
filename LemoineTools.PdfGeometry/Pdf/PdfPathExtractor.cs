using System;
using System.Collections.Generic;
using LemoineTools.PdfGeometry.Primitives;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;

namespace LemoineTools.PdfGeometry.Pdf
{
    public sealed class PathExtractOptions
    {
        /// <summary>Drop strokes thinner than this (pts). Wall lines are usually heavier; 0 keeps everything.</summary>
        public double MinLineWidth { get; set; } = 0;

        /// <summary>Include the outlines of filled (non-stroked) paths — solid wall pochés.</summary>
        public bool IncludeFilledOutlines { get; set; } = true;

        /// <summary>Chord tolerance for flattening bezier curves, pts.</summary>
        public double ChordTol { get; set; } = 0.25;
    }

    /// <summary>
    /// Extracts the page's vector linework as flat segments (PDF point space,
    /// origin bottom-left) for the segment graph. All PdfPig API usage for path
    /// content is concentrated here.
    /// </summary>
    public static class PdfPathExtractor
    {
        public static List<Seg2> Extract(string pdfPath, int pageNumber, PathExtractOptions options)
        {
            var segments = new List<Seg2>();
            using (var doc = PdfDocument.Open(pdfPath))
            {
                var page = doc.GetPage(pageNumber);
                foreach (var path in page.Paths)
                {
                    if (path.IsClipping) continue;
                    bool stroked = path.IsStroked;
                    bool filled = path.IsFilled;
                    if (!stroked && !(filled && options.IncludeFilledOutlines)) continue;
                    if (stroked && options.MinLineWidth > 0 && path.LineWidth < options.MinLineWidth) continue;

                    foreach (var subpath in path)
                        AppendSubpath(segments, subpath, options.ChordTol);
                }
            }
            return segments;
        }

        private static void AppendSubpath(List<Seg2> segments, PdfSubpath subpath, double chordTol)
        {
            Pt2? start = null;   // subpath start, for Close
            Pt2? current = null;

            foreach (var cmd in subpath.Commands)
            {
                switch (cmd)
                {
                    case PdfSubpath.Move move:
                        current = ToPt(move.Location);
                        start = current;
                        break;

                    case PdfSubpath.Line line:
                        {
                            var from = ToPt(line.From);
                            var to = ToPt(line.To);
                            AddSeg(segments, from, to);
                            current = to;
                            start = start ?? from;
                            break;
                        }

                    case PdfSubpath.BezierCurve bez:
                        {
                            var p0 = current ?? ToPt(bez.StartPoint);
                            var p1 = ToPt(bez.FirstControlPoint);
                            var p2 = ToPt(bez.SecondControlPoint);
                            var p3 = ToPt(bez.EndPoint);
                            var flat = FlattenCubic(p0, p1, p2, p3, chordTol);
                            for (int i = 1; i < flat.Count; i++)
                                AddSeg(segments, flat[i - 1], flat[i]);
                            current = p3;
                            start = start ?? p0;
                            break;
                        }

                    case PdfSubpath.Close _:
                        if (current.HasValue && start.HasValue)
                            AddSeg(segments, current.Value, start.Value);
                        current = start;
                        break;
                }
            }
        }

        private static Pt2 ToPt(PdfPoint p) => new Pt2(p.X, p.Y);

        private static void AddSeg(List<Seg2> segments, Pt2 a, Pt2 b)
        {
            if (a.DistanceTo(b) > 1e-9) segments.Add(new Seg2(a, b));
        }

        /// <summary>Recursive de Casteljau flattening with a flatness test against the chord.</summary>
        internal static List<Pt2> FlattenCubic(Pt2 p0, Pt2 p1, Pt2 p2, Pt2 p3, double chordTol)
        {
            var pts = new List<Pt2> { p0 };
            FlattenCubicRec(p0, p1, p2, p3, chordTol, 0, pts);
            pts.Add(p3);
            return pts;
        }

        private static void FlattenCubicRec(Pt2 p0, Pt2 p1, Pt2 p2, Pt2 p3, double tol, int depth, List<Pt2> outPts)
        {
            // Flat enough when both control points sit within tol of the chord.
            var chord = new Seg2(p0, p3);
            if (depth >= 16 || (chord.DistanceTo(p1) <= tol && chord.DistanceTo(p2) <= tol))
                return;

            var p01 = Pt2.Lerp(p0, p1, 0.5);
            var p12 = Pt2.Lerp(p1, p2, 0.5);
            var p23 = Pt2.Lerp(p2, p3, 0.5);
            var p012 = Pt2.Lerp(p01, p12, 0.5);
            var p123 = Pt2.Lerp(p12, p23, 0.5);
            var mid = Pt2.Lerp(p012, p123, 0.5);

            FlattenCubicRec(p0, p01, p012, mid, tol, depth + 1, outPts);
            outPts.Add(mid);
            FlattenCubicRec(mid, p123, p23, p3, tol, depth + 1, outPts);
        }
    }
}
