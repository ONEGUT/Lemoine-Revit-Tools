using System;
using System.Windows;
using System.Windows.Media;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// A complete set of design tokens that drives the entire Lemoine UI.
    /// Mirrors the JS lemoine-themes.js palette exactly.
    ///
    /// Theming works via WPF DynamicResource:
    ///   • Every control uses SetResourceReference(prop, "LemoineXxx")
    ///   • ApplyTo(window) updates window.Resources, which propagates to all children
    ///   • No need to rebuild the window — one call updates every element
    /// </summary>
    public class LemoineTheme
    {
        /// <summary>Display name for this theme (e.g. "Dark Mono"), shown in the toolbar theme picker.</summary>
        public string Name { get; set; } = null!;

        // ── Background layers ──────────────────────────────────────────────
        /// <summary>Primary window background colour.</summary>
        public SolidColorBrush Bg       { get; set; } = null!;  // window background
        /// <summary>Outer page background — slightly darker than <see cref="Bg"/> to create depth behind the main panel.</summary>
        public SolidColorBrush PageBg   { get; set; } = null!;  // outer page (slightly darker)
        /// <summary>Toolbar and tab-bar background colour.</summary>
        public SolidColorBrush Surface  { get; set; } = null!;  // toolbar / tab bar
        /// <summary>Elevated surface colour used for cards and input backgrounds.</summary>
        public SolidColorBrush Raised   { get; set; } = null!;  // cards, input backgrounds
        /// <summary>Background fill for text input fields and select controls.</summary>
        public SolidColorBrush SelectBg { get; set; } = null!;  // text input background

        // ── Borders ───────────────────────────────────────────────────────
        /// <summary>Default border / separator colour. Must meet 3:1 contrast against adjacent backgrounds per WCAG 1.4.11.</summary>
        public SolidColorBrush Border    { get; set; } = null!;
        /// <summary>Mid-weight border colour for secondary separators and dividers.</summary>
        public SolidColorBrush BorderMid { get; set; } = null!;

        // ── Text hierarchy ────────────────────────────────────────────────
        /// <summary>Primary body text colour.</summary>
        public SolidColorBrush Text    { get; set; } = null!;
        /// <summary>Secondary / subdued text colour for labels and helper text.</summary>
        public SolidColorBrush TextSub { get; set; } = null!;
        /// <summary>Tertiary / dimmed text colour for placeholders and disabled labels.</summary>
        public SolidColorBrush TextDim { get; set; } = null!;

        // ── Interactive / accent ─────────────────────────────────────────
        /// <summary>Brand accent colour used for interactive elements such as links, focus rings, and active states.</summary>
        public SolidColorBrush Accent    { get; set; } = null!;
        /// <summary>Translucent tint of <see cref="Accent"/>, used as a background highlight behind accent-coloured text.</summary>
        public SolidColorBrush AccentDim { get; set; } = null!;  // translucent tint background

        // ── Status ────────────────────────────────────────────────────────
        /// <summary>Success / positive status colour (green).</summary>
        public SolidColorBrush Green    { get; set; } = null!;
        /// <summary>Dimmed success background used behind <see cref="Green"/> text badges.</summary>
        public SolidColorBrush GreenDim { get; set; } = null!;
        /// <summary>Error / destructive status colour (red).</summary>
        public SolidColorBrush Red      { get; set; } = null!;
        /// <summary>Dimmed error background used behind <see cref="Red"/> text badges.</summary>
        public SolidColorBrush RedDim   { get; set; } = null!;

        // ── Fonts ─────────────────────────────────────────────────────────
        /// <summary>Monospaced font family used for code or coordinate display (typically Consolas).</summary>
        public FontFamily MonoFont  { get; set; } = null!;
        /// <summary>General UI font family used throughout dialogs and controls (typically Segoe UI).</summary>
        public FontFamily UiFont    { get; set; } = null!;
        /// <summary>IcoMoon Free icon font — shared across all themes.</summary>
        public FontFamily IconFont  { get; set; } = LemoineTheme._iconFont;

        // ── Toggle knob ───────────────────────────────────────────────────
        /// <summary>Toggle-switch knob colour when the switch is ON (rendered over the <see cref="Accent"/> track).</summary>
        public SolidColorBrush KnobOn  { get; set; } = null!;  // knob colour on active (accent) track
        /// <summary>Toggle-switch knob colour when the switch is OFF (rendered over the <see cref="Border"/> track).</summary>
        public SolidColorBrush KnobOff { get; set; } = null!;  // knob colour on inactive (border) track

        // ── Theme swatch colour (shown in the toolbar picker) ─────────────
        /// <summary>Representative swatch colour displayed in the toolbar theme picker to identify this theme at a glance.</summary>
        public SolidColorBrush Swatch { get; set; } = null!;

        // ═════════════════════════════════════════════════════════════════
        // Apply this theme to a WPF Window's ResourceDictionary.
        // All controls that use SetResourceReference / {DynamicResource}
        // with the "Lemoine*" keys will update automatically.
        // ═════════════════════════════════════════════════════════════════
        /// <summary>
        /// Applies this theme to a WPF <see cref="ResourceDictionary"/> by writing every
        /// design token under its <c>Lemoine*</c> key.  All controls that bind via
        /// <c>SetResourceReference</c> or <c>{DynamicResource}</c> update automatically
        /// without requiring the window to be rebuilt.
        /// </summary>
        /// <param name="resources">
        /// The <see cref="ResourceDictionary"/> to update — typically <c>window.Resources</c>.
        /// </param>
        public void ApplyTo(ResourceDictionary resources)
        {
            resources["LemoineBg"]       = Bg;
            resources["LemoinePageBg"]   = PageBg;
            resources["LemoineSurface"]  = Surface;
            resources["LemoineRaised"]   = Raised;
            resources["LemoineSelectBg"] = SelectBg;
            resources["LemoineBorder"]   = Border;
            resources["LemoineBorderMid"]= BorderMid;
            resources["LemoineText"]     = Text;
            resources["LemoineTextSub"]  = TextSub;
            resources["LemoineTextDim"]  = TextDim;
            resources["LemoineAccent"]   = Accent;
            resources["LemoineAccentDim"]= AccentDim;
            resources["LemoineGreen"]    = Green;
            resources["LemoineGreenDim"] = GreenDim;
            resources["LemoineRed"]      = Red;
            resources["LemoineRedDim"]   = RedDim;
            resources["LemoineMonoFont"]  = MonoFont;
            resources["LemoineUiFont"]    = UiFont;
            resources["LemoineIconFont"]  = IconFont;
            resources["LemoineKnobOn"]   = KnobOn;
            resources["LemoineKnobOff"]  = KnobOff;
        }

        // ─────────────────────────────────────────────────────────────────
        // Shared icon font — same for every theme
        // ─────────────────────────────────────────────────────────────────
        // Base URI uses the *executing* assembly name so this resolves correctly
        // whether the framework compiles into LemoineTools (Revit) or
        // LemoineNavisworks. Both embed IcoMoonFree.ttf as a WPF <Resource>.
        private static readonly FontFamily _iconFont = new FontFamily(
            new Uri("pack://application:,,,/"
                + System.Reflection.Assembly.GetExecutingAssembly().GetName().Name
                + ";component/"),
            "./Source/Resources/Fonts/IcoMoonFree.ttf#IcoMoon-Free");

        // ─────────────────────────────────────────────────────────────────
        // Convenience: parse a hex string to a frozen SolidColorBrush
        // ─────────────────────────────────────────────────────────────────
        private static SolidColorBrush B(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        // ═════════════════════════════════════════════════════════════════
        // The 5 predefined themes — mirror lemoine-themes.js exactly
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Dark monochrome theme.  Neutral grey palette with a cool blue accent.
        /// Suitable for general development and low-light environments.
        /// </summary>
        public static readonly LemoineTheme DarkMono = new LemoineTheme
        {
            Name     = "Dark Mono",
            Swatch   = B("#1a1a1a"),
            PageBg   = B("#111111"),
            Bg       = B("#1a1a1a"),
            Surface  = B("#222222"),
            Raised   = B("#2a2a2a"),
            SelectBg = B("#2a2a2a"),
            Border    = B("#686868"),   // was #333333 — darkened to 3.12:1 for UI component contrast
            BorderMid = B("#747474"),   // was #3e3e3e — darkened to 3.72:1
            Text    = B("#d4d4d4"),
            TextSub = B("#8c8c8c"),     // was #858585 — lightened so TextSub on Surface = 4.73:1
            TextDim = B("#919191"),     // was #555555 (2.33:1) — now 5.52:1 on Bg, 5.05:1 on Surface
            Accent    = B("#4f8fc4"),   // was #569cd6 — darkened so white knob on active = 3.47:1
            AccentDim = B("#1b2d3e"),
            Green    = B("#4ec994"),
            GreenDim = B("#0e2219"),
            Red      = B("#f47067"),
            RedDim   = B("#2c1412"),
            KnobOn   = B("#ffffff"),    // white on #4f8fc4 = 3.47:1
            KnobOff  = B("#ffffff"),    // white on #686868 = 5.57:1
            MonoFont = new FontFamily("Consolas"),
            UiFont   = new FontFamily("Segoe UI"),
        };

        /// <summary>
        /// Dark navy / indigo theme.  Deep blue-tinted backgrounds with a bright blue accent.
        /// </summary>
        public static readonly LemoineTheme DarkNavy = new LemoineTheme
        {
            Name     = "Dark Navy",
            Swatch   = B("#0b0d14"),
            PageBg   = B("#060810"),
            Bg       = B("#0b0d14"),
            Surface  = B("#111520"),
            Raised   = B("#161b28"),
            SelectBg = B("#161b28"),
            Border    = B("#5e6272"),   // was #252933 — lightened to 3.20:1 for UI component contrast
            BorderMid = B("#6a6f80"),   // was #303644 — lightened to 3.88:1
            Text    = B("#e8eaf0"),
            TextSub = B("#8b90a0"),
            TextDim = B("#838899"),     // was #555b6e (2.87:1) — now 5.50:1 on Bg, 5.16:1 on Surface
            Accent    = B("#5b8af0"),
            AccentDim = B("#131a30"),
            Green    = B("#3ed598"),
            GreenDim = B("#0d2019"),
            Red      = B("#f06060"),
            RedDim   = B("#2a1212"),
            KnobOn   = B("#ffffff"),    // white on #5b8af0 = 3.31:1
            KnobOff  = B("#ffffff"),    // white on #5e6272 = 6.06:1
            MonoFont = new FontFamily("Consolas"),
            UiFont   = new FontFamily("Segoe UI"),
        };

        /// <summary>
        /// Light clean theme.  White backgrounds with neutral grey borders and a standard blue accent.
        /// Mirrors a GitHub-style light palette.
        /// </summary>
        public static readonly LemoineTheme LightClean = new LemoineTheme
        {
            Name     = "Light Clean",
            Swatch   = B("#ffffff"),
            PageBg   = B("#f0f0ee"),
            Bg       = B("#ffffff"),
            Surface  = B("#f6f8fa"),
            Raised   = B("#eaeef2"),
            SelectBg = B("#ffffff"),
            Border    = B("#8d8d8d"),   // was #d0d7de — darkened to 3.32:1 for UI component contrast
            BorderMid = B("#7a7a7a"),   // was #b8c0c8 — darkened to 4.29:1
            Text    = B("#1f2328"),
            TextSub = B("#656d76"),
            TextDim = B("#5f6d78"),     // was #9198a1 (2.91:1) — now 5.32:1 on Bg, 5.00:1 on Surface
            Accent    = B("#0969da"),
            AccentDim = B("#dbeafe"),
            Green    = B("#1a7f37"),
            GreenDim = B("#dcfce7"),
            Red      = B("#cf222e"),
            RedDim   = B("#fee2e2"),
            KnobOn   = B("#ffffff"),    // white on #0969da = 5.19:1
            KnobOff  = B("#333333"),    // dark on #8d8d8d = 3.81:1
            MonoFont = new FontFamily("Consolas"),
            UiFont   = new FontFamily("Segoe UI"),
        };

        /// <summary>
        /// Light warm theme.  Cream / parchment backgrounds with warm brown-tinted borders and an amber accent.
        /// </summary>
        public static readonly LemoineTheme LightWarm = new LemoineTheme
        {
            Name     = "Light Warm",
            Swatch   = B("#fdf8f2"),
            PageBg   = B("#ede8e0"),
            Bg       = B("#fdf8f2"),
            Surface  = B("#f5efe6"),
            Raised   = B("#ede5d8"),
            SelectBg = B("#f5efe6"),
            Border    = B("#8c8379"),   // was #ddd5c8 — darkened to 3.53:1 for UI component contrast
            BorderMid = B("#7a7168"),   // was #c8bfb0 — darkened to 4.53:1
            Text    = B("#2c2420"),
            TextSub = B("#6a5f57"),     // was #7a6e65 — darkened so TextSub on Surface = 5.42:1
            TextDim = B("#6e6159"),     // was #a89e94 (2.49:1) — now 5.65:1 on Bg, 5.22:1 on Surface
            Accent    = B("#b35500"),   // was #c2610f (3.97:1 fail) — now 4.72:1
            AccentDim = B("#fde8d4"),
            Green    = B("#2a6e3f"),
            GreenDim = B("#d4edda"),
            Red      = B("#b83232"),
            RedDim   = B("#fde8e8"),
            KnobOn   = B("#ffffff"),    // white on #b35500 = 4.98:1
            KnobOff  = B("#333333"),    // dark on #8c8379 = 3.39:1
            MonoFont = new FontFamily("Consolas"),
            UiFont   = new FontFamily("Segoe UI"),
        };

        /// <summary>
        /// Stone grey theme.  Mid-grey backgrounds with a muted steel-blue accent.
        /// A softer alternative to the dark mono theme.
        /// </summary>
        public static readonly LemoineTheme Stone = new LemoineTheme
        {
            Name     = "Stone Gray",
            Swatch   = B("#4a4a4a"),
            PageBg   = B("#2e2e2e"),
            Bg       = B("#3c3c3c"),
            Surface  = B("#444444"),
            Raised   = B("#505050"),
            SelectBg = B("#505050"),
            Border    = B("#888888"),   // was #5a5a5a — lightened to 3.11:1 for UI component contrast
            BorderMid = B("#949494"),   // was #666666 — lightened to 3.64:1
            Text    = B("#e0e0e0"),
            TextSub = B("#b3b3b3"),     // was #aaaaaa — lightened so TextSub on Surface = 4.65:1
            TextDim = B("#b5b5b5"),     // was #777777 (2.46:1) — now 5.38:1 on Bg, 4.75:1 on Surface
            Accent    = B("#9abdd4"),
            AccentDim = B("#243340"),
            Green    = B("#7ec99a"),
            GreenDim = B("#1e3328"),
            Red      = B("#f09090"),    // was #e8847c (4.21:1 fail) — now 4.78:1
            RedDim   = B("#3a1f1e"),
            KnobOn   = B("#2a2a2a"),    // dark on #9abdd4 (light accent) = 7.25:1
            KnobOff  = B("#ffffff"),    // white on #888888 = 3.54:1
            MonoFont = new FontFamily("Consolas"),
            UiFont   = new FontFamily("Segoe UI"),
        };

        /// <summary>
        /// Organic warmth theme.  Earthy greens and warm cream backgrounds with a forest-green accent.
        /// Grounded and approachable — never sterile or techy.
        /// Intended fonts: Nunito Sans (UI), falls back to Segoe UI if not installed.
        /// </summary>
        public static readonly LemoineTheme Terra = new LemoineTheme
        {
            Name     = "Terra",
            Swatch   = B("#4a7c59"),
            PageBg   = B("#ede8dc"),
            Bg       = B("#faf6f0"),
            Surface  = B("#f3ece0"),
            Raised   = B("#e8e0d2"),
            SelectBg = B("#faf6f0"),
            Border    = B("#8a8275"),   // warm gray — 3.40:1 on Bg
            BorderMid = B("#7a7268"),   // slightly darker — 4.0:1 on Bg
            Text    = B("#2a2520"),
            TextSub = B("#6b5f52"),     // warm brown — 5.0:1 on Bg
            TextDim = B("#7a6e60"),     // 4.5:1 on Bg, 4.2:1 on Surface
            Accent    = B("#4a7c59"),   // forest green — 4.6:1 on Bg
            AccentDim = B("#d4e8da"),
            Green    = B("#3d6b4a"),    // deeper forest for success badges
            GreenDim = B("#c8e4d4"),
            Red      = B("#b83030"),    // warm red — 5.2:1 on Bg
            RedDim   = B("#fde8e8"),
            KnobOn   = B("#ffffff"),    // white on #4a7c59 = 4.6:1
            KnobOff  = B("#333333"),    // dark on #8a8275 = 3.5:1
            MonoFont = new FontFamily("Consolas"),
            UiFont   = new FontFamily("Nunito Sans, Segoe UI"),
        };

        /// <summary>
        /// Warm minimalism theme.  Sun-baked linen backgrounds with a burnt-sienna accent.
        /// Editorial and luxurious — abundant whitespace, serif energy.
        /// Intended fonts: Manrope (UI), falls back to Segoe UI if not installed.
        /// </summary>
        public static readonly LemoineTheme Sahara = new LemoineTheme
        {
            Name     = "Sahara",
            Swatch   = B("#c2652a"),
            PageBg   = B("#ede8de"),
            Bg       = B("#faf5ee"),
            Surface  = B("#f2ece2"),
            Raised   = B("#e8e0d4"),
            SelectBg = B("#ffffff"),    // white input fill per design spec
            Border    = B("#8c8278"),   // warm gray — 3.42:1 on Bg
            BorderMid = B("#7c7268"),   // 4.1:1 on Bg
            Text    = B("#2c2218"),
            TextSub = B("#6e5e50"),     // warm brown — 5.1:1 on Bg
            TextDim = B("#7c6c5c"),     // 4.6:1 on Bg
            Accent    = B("#c2652a"),   // burnt sienna — 5.24:1 on Bg
            AccentDim = B("#f8dcc8"),
            Green    = B("#2a6e3f"),    // earthy green
            GreenDim = B("#d4edda"),
            Red      = B("#b03030"),    // earthy red — 5.0:1 on Bg
            RedDim   = B("#fde8e8"),
            KnobOn   = B("#ffffff"),    // white on #c2652a = 5.24:1
            KnobOff  = B("#333333"),    // dark on #8c8278 = 3.5:1
            MonoFont = new FontFamily("Consolas"),
            UiFont   = new FontFamily("Manrope, Segoe UI"),
        };

        /// <summary>
        /// High-contrast dark theme.  True near-black backgrounds, zinc surfaces, and a soft violet accent.
        /// Developer-grade precision — borders over shadows, function over decoration.
        /// Intended fonts: Geist (UI), falls back to Segoe UI if not installed.
        /// </summary>
        public static readonly LemoineTheme Obsidian = new LemoineTheme
        {
            Name     = "Obsidian",
            Swatch   = B("#a78bfa"),
            PageBg   = B("#050507"),
            Bg       = B("#09090b"),
            Surface  = B("#0c0c0f"),
            Raised   = B("#18181b"),
            SelectBg = B("#18181b"),
            Border    = B("#565659"),   // zinc — 3.05:1 on Bg
            BorderMid = B("#606063"),   // 3.5:1 on Bg
            Text    = B("#fafafa"),     // near-white — high contrast
            TextSub = B("#a1a1aa"),     // zinc-400 — 7.5:1 on Bg
            TextDim = B("#71717a"),     // zinc-500 — 4.27:1 on Bg
            Accent    = B("#a78bfa"),   // soft violet — 6.35:1 on Bg
            AccentDim = B("#1c1033"),
            Green    = B("#34d399"),    // emerald per spec — 10:1 on Bg
            GreenDim = B("#0a2e1e"),
            Red      = B("#ef4444"),    // errors only per spec — 4.41:1 on Bg
            RedDim   = B("#2c1010"),
            KnobOn   = B("#09090b"),    // near-black on violet = 6.35:1
            KnobOff  = B("#fafafa"),    // near-white on #565659 = 6.4:1
            MonoFont = new FontFamily("Consolas"),
            UiFont   = new FontFamily("Geist, Segoe UI"),
        };

        /// <summary>
        /// Shared fallback colour for unknown or unassigned elements (neutral mid-grey #888888).
        /// Use this constant instead of repeating <c>Color.FromRgb(0x88, 0x88, 0x88)</c> throughout the codebase.
        /// </summary>
        public static readonly Color FallbackGrey = Color.FromRgb(0x88, 0x88, 0x88);

        /// <summary>
        /// All predefined themes in display order, matching the lemoine-themes.js palette.
        /// Used to populate the toolbar theme picker.
        /// </summary>
        public static readonly LemoineTheme[] All =
            { DarkMono, DarkNavy, LightClean, LightWarm, Stone, Terra, Sahara, Obsidian };
    }
}
