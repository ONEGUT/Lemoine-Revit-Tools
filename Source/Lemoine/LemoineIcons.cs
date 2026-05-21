using System.Windows;
using System.Windows.Controls;

namespace LemoineTools.Lemoine
{
    // IcoMoon Free codepoints — full list at https://icomoon.io/#preview-free
    public enum LemoineIcon
    {
        // ── Navigation / layout ───────────────────────────────────────────
        Home            = 0xe900,
        Office          = 0xe903,
        Menu            = 0xe935,
        Menu2           = 0xe936,
        Menu3           = 0xe937,
        Tree            = 0xe934,
        List            = 0xe932,
        ListNumbered    = 0xe931,
        Table           = 0xe9dc,
        Table2          = 0xe9dd,

        // ── Editing / tools ───────────────────────────────────────────────
        Pencil          = 0xe905,
        Pencil2         = 0xe906,
        Pen             = 0xe908,
        Eyedropper      = 0xe90a,
        PaintFormat     = 0xe90c,
        Scissors        = 0xe9ce,
        Crop            = 0xe9cb,
        MakeGroup       = 0xe9cc,
        Ungroup         = 0xe9cd,
        Filter          = 0xe9cf,

        // ── Files / media ─────────────────────────────────────────────────
        Image           = 0xe90d,
        Images          = 0xe90e,
        Camera          = 0xe90f,
        Attachment      = 0xe945,
        Clipboard       = 0xe930,
        FilePdf         = 0xea44,
        FileWord        = 0xea46,
        FileExcel       = 0xea47,

        // ── Cloud / network ───────────────────────────────────────────────
        Cloud           = 0xe939,
        CloudDownload   = 0xe93a,
        CloudUpload     = 0xe93b,
        CloudCheck      = 0xe93c,
        Download        = 0xe93d,
        Upload          = 0xe93e,
        Link            = 0xe943,
        Share           = 0xe9e9,
        Share2          = 0xe9ee,
        NewTab          = 0xe9ea,

        // ── Status / feedback ────────────────────────────────────────────
        Eye             = 0xe946,
        EyeBlocked      = 0xe949,
        Warning         = 0xe97b,
        Notification    = 0xe97c,
        Question        = 0xe97d,
        Info            = 0xe980,
        Cancel          = 0xe981,
        Blocked         = 0xe982,
        Cross           = 0xe983,
        Checkmark       = 0xe984,
        Checkmark2      = 0xe985,
        SpellCheck      = 0xe986,
        Flag            = 0xe944,
        Bookmark        = 0xe94a,
        Star            = 0xe94f,
        StarHalf        = 0xe950,
        StarFull        = 0xe951,

        // ── Actions ───────────────────────────────────────────────────────
        Plus            = 0xe97e,
        Minus           = 0xe97f,
        Enter           = 0xe987,
        Exit            = 0xe988,
        Bin             = 0xe924,
        Bin2            = 0xe925,
        Target          = 0xe92b,
        Shield          = 0xe92c,
        Power           = 0xe92d,

        // ── Playback / process ────────────────────────────────────────────
        Play            = 0xe989,
        Pause           = 0xe98a,
        Stop            = 0xe98b,
        Previous        = 0xe98c,
        Next            = 0xe98d,
        Loop            = 0xe9a1,
        Shuffle         = 0xe9a4,

        // ── Sort / order ──────────────────────────────────────────────────
        SortAlphaAsc    = 0xe9bc,
        SortAlphaDesc   = 0xe9bd,
        SortNumericAsc  = 0xe9be,
        SortAmountAsc   = 0xe9bf,
        SortAmountDesc  = 0xe9c0,
        SortNumericDesc = 0xe9c1,

        // ── Arrows ────────────────────────────────────────────────────────
        ArrowUp         = 0xe9a6,
        ArrowRight      = 0xe9a8,
        ArrowDown       = 0xe9aa,
        ArrowLeft       = 0xe9ac,
        ArrowUp2        = 0xe9ae,
        ArrowRight2     = 0xe9b0,
        ArrowDown2      = 0xe9b2,
        ArrowLeft2      = 0xe9b4,
        CircleUp        = 0xe9b5,
        CircleRight     = 0xe9b6,
        CircleDown      = 0xe9b7,
        CircleLeft      = 0xe9b8,

        // ── Form controls ────────────────────────────────────────────────
        CheckboxChecked   = 0xe9c6,
        CheckboxUnchecked = 0xe9c7,
        RadioChecked      = 0xe9c8,
        RadioUnchecked    = 0xe9ca,

        // ── Text / typography ────────────────────────────────────────────
        Bold            = 0xe9d6,
        Italic          = 0xe9d8,
        Underline       = 0xe9d7,
        IndentIncrease  = 0xe9e7,
        IndentDecrease  = 0xe9e8,

        // ── Misc ──────────────────────────────────────────────────────────
        Newspaper       = 0xe904,
        Droplet         = 0xe90b,
        Hammer          = 0xe920,
        Lab             = 0xe922,
        Briefcase       = 0xe926,
        Road            = 0xe929,
        Terminal        = 0xe9ed,
        Sun             = 0xe94c,
        Mail            = 0xe9ef,
    }

    public static class LemoineIcons
    {
        /// <summary>
        /// Returns a TextBlock rendering the given icon using the LemoineIconFont and
        /// the specified design-token size key (default: LemoineFS_MD).
        /// Colour follows LemoineText unless you override Foreground after the call.
        /// </summary>
        public static TextBlock Build(LemoineIcon icon, string sizeToken = "LemoineFS_MD")
        {
            var tb = new TextBlock { Text = char.ConvertFromUtf32((int)icon) };
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineIconFont");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   sizeToken);
            return tb;
        }

        /// <summary>Convenience overload for a specific foreground colour token.</summary>
        public static TextBlock Build(LemoineIcon icon, string sizeToken, string foregroundToken)
        {
            var tb = Build(icon, sizeToken);
            tb.SetResourceReference(TextBlock.ForegroundProperty, foregroundToken);
            return tb;
        }
    }
}
