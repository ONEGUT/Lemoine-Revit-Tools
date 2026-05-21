using System.Windows;
using System.Windows.Controls;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// A reusable themed warning banner: a rounded border in LemoineWarnBg/Border
    /// colours containing a wrapped text block in LemoineWarnText.
    ///
    /// Used wherever a destructive-action warning must appear inline inside a
    /// step panel.  Replaces ad-hoc Border + TextBlock construction that was
    /// previously duplicated across multiple ViewModel step methods.
    ///
    /// Usage:
    ///   var warn = new LemoineWarnBanner("⚠  Some warning message.");
    ///   outer.Children.Add(warn);
    ///
    ///   // To update the message after construction (e.g. on ValidationChanged):
    ///   warn.Message = $"⚠  {count} items will be affected.";
    /// </summary>
    public class LemoineWarnBanner : Border
    {
        private readonly TextBlock _textBlock;

        /// <summary>Gets or sets the warning message shown inside the banner.</summary>
        public string Message
        {
            get => _textBlock.Text;
            set => _textBlock.Text = value ?? string.Empty;
        }

        /// <summary>
        /// Creates a warning banner with the specified message.
        /// </summary>
        /// <param name="message">
        ///   The text to display.  Prefix with "⚠  " by convention.
        /// </param>
        /// <param name="bottomMargin">
        ///   Bottom margin in device-independent pixels (default 10).
        ///   Pass 0 when the caller manages its own spacing.
        /// </param>
        public LemoineWarnBanner(string message, double bottomMargin = 10)
        {
            CornerRadius    = new CornerRadius(3);
            BorderThickness = new Thickness(1);
            Margin          = new Thickness(0, 0, 0, bottomMargin);

            this.SetResourceReference(BackgroundProperty,  "LemoineWarnBg");
            this.SetResourceReference(BorderBrushProperty, "LemoineWarnBorder");
            this.SetResourceReference(PaddingProperty,     "LemoineTh_CardPad");

            _textBlock = new TextBlock
            {
                Text         = message ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
            };
            _textBlock.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _textBlock.SetResourceReference(TextBlock.ForegroundProperty, "LemoineWarnText");
            _textBlock.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            Child = _textBlock;
        }
    }
}
