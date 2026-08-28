using Avalonia.Controls;
using Avalonia.Media;

namespace SnapShotKit.Ui;

/// <summary>
/// Text in the two voices the system speaks in: condensed uppercase for chrome labels, and Barlow
/// for everything a sentence would be written in.
///
/// Chrome labels are always uppercase and always tracked out. That tracking is not decoration: at
/// 11 pixels, condensed uppercase set solid is genuinely hard to read, and the letter spacing is
/// what buys the density back.
/// </summary>
public static class Labels
{
    /// <summary>A chrome label: condensed, uppercase, tracked.</summary>
    public static TextBlock Heading(string text, double size = 11.5, double tracking = 0.16, IBrush? foreground = null) => new()
    {
        Text = text.ToUpperInvariant(),
        FontFamily = Tokens.Fonts.Heading,
        FontWeight = Tokens.Fonts.HeadingWeight,
        FontSize = size,
        LetterSpacing = Tokens.Tracking(size, tracking),
        Foreground = foreground ?? Tokens.Neutral600Brush
    };

    /// <summary>Interface text: menus, buttons, fields, meta.</summary>
    public static TextBlock Body(string text, double size = 12.5, IBrush? foreground = null) => new()
    {
        Text = text,
        FontFamily = Tokens.Fonts.Body,
        FontSize = size,
        Foreground = foreground ?? Tokens.Neutral700Brush
    };
}
