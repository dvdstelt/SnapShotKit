using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SnapShotKit.Ui;

namespace SnapShotKit.Overlay;

/// <summary>
/// The line of hints along the bottom of the overlay.
///
/// The overlay appears without warning over whatever the user was doing, and every one of its
/// gestures is invisible until tried. Spelling them out costs one line at the bottom of the screen,
/// which is cheap next to a user who does not discover that Space takes the whole screen.
/// </summary>
public sealed class HintBar : Border
{
    const double FromBottom = 26;

    public HintBar(params string[] hints)
    {
        Background = Tokens.BgBrush;
        BorderBrush = Tokens.DividerBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = Tokens.Radius;
        Padding = new Thickness(Tokens.Space.S6, Tokens.Space.S2);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.S6 };

        foreach (var hint in hints)
        {
            row.Children.Add(Labels.Body(hint, 12.5, Tokens.Neutral700Brush));
        }

        Child = row;
    }

    public void PlaceAtBottom(Size screen)
    {
        Measure(screen);

        Canvas.SetLeft(this, Math.Max((screen.Width - DesiredSize.Width) / 2, 0));
        Canvas.SetTop(this, Math.Max(screen.Height - FromBottom - DesiredSize.Height, 0));
    }
}
