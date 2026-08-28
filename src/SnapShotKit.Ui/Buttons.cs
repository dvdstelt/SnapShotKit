using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace SnapShotKit.Ui;

/// <summary>
/// The system's buttons.
///
/// Square corners, hairline border, an optional Lucide glyph ahead of the label. The primary is the
/// one solid object on the board: an accent fill that keeps the square corners and the registration
/// marks, and there is only ever one of it in view.
///
/// The primary is handed back already wrapped in its marks, so these take the click handler rather
/// than returning a Button to subscribe to: the caller would otherwise have to know that one
/// variant is wrapped in a decorator and the other is not.
/// </summary>
public static class Buttons
{
    const double IconSize = 16;
    const double IconGap = 8;
    const double Height = 38;

    /// <summary>
    /// A button theme with no state styling of its own.
    ///
    /// The stock Fluent theme repaints the content presenter on hover and press with its own
    /// translucent brushes. Those are set on the presenter, not on the button, so they win over
    /// whatever background the button carries: a hovered blueprint button turned see-through and
    /// took its border and label with it. This template binds the presenter to the button and
    /// nothing else, leaving every state to the handlers below.
    /// </summary>
    public static readonly ControlTheme Bare = new(typeof(Button))
    {
        Setters =
        {
            new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<Button>((button, scope) =>
                new ContentPresenter
                {
                    Name = "PART_ContentPresenter",
                    [!ContentPresenter.ContentProperty] = button[!ContentControl.ContentProperty],
                    [!ContentPresenter.BackgroundProperty] = button[!TemplatedControl.BackgroundProperty],
                    [!ContentPresenter.BorderBrushProperty] = button[!TemplatedControl.BorderBrushProperty],
                    [!ContentPresenter.BorderThicknessProperty] = button[!TemplatedControl.BorderThicknessProperty],
                    [!ContentPresenter.CornerRadiusProperty] = button[!TemplatedControl.CornerRadiusProperty],
                    [!ContentPresenter.PaddingProperty] = button[!TemplatedControl.PaddingProperty],
                    [!ContentPresenter.HorizontalContentAlignmentProperty] = button[!ContentControl.HorizontalContentAlignmentProperty],
                    [!ContentPresenter.VerticalContentAlignmentProperty] = button[!ContentControl.VerticalContentAlignmentProperty]
                }.RegisterInNameScope(scope))),

            // Keyboard focus is always visible and always the same: an accent ring just outside the
            // object, never a platform default.
            new Setter(Control.FocusAdornerProperty, new FuncTemplate<Control>(() => new Border
            {
                BorderBrush = Tokens.AccentBrush,
                BorderThickness = new Thickness(2),
                CornerRadius = Tokens.Radius,
                Margin = new Thickness(-4)
            }))
        }
    };

    /// <summary>The solid accent button, with its registration marks. One per view: it is what the eye is meant to land on.</summary>
    public static Control Primary(string text, string? icon, Action clicked) =>
        Blueprint.Wrap(
            Build(text, icon, Tokens.AccentBrush, Tokens.BgBrush, Tokens.AccentBrush, Tokens.Accent700Brush, clicked),
            drawFrame: false);

    /// <summary>
    /// The primary action when that action destroys something.
    ///
    /// Red rather than the accent, and still the one solid object in view: a button that deletes
    /// should not look like a button that saves.
    /// </summary>
    public static Control Danger(string text, string? icon, Action clicked) =>
        Blueprint.Wrap(
            Build(text, icon, Danger_, Tokens.BgBrush, Danger_, DangerPressed, clicked),
            drawFrame: false);

    static readonly IBrush Danger_ = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(0xC5, 0x30, 0x30));
    static readonly IBrush DangerHover = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(0xB0, 0x2A, 0x2A));
    static readonly IBrush DangerPressed = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(0x96, 0x24, 0x24));

    /// <summary>Everything that is not the one primary action.</summary>
    public static Control Secondary(string text, string? icon, Action clicked) =>
        Build(text, icon, Tokens.BgBrush, Tokens.Neutral800Brush, Tokens.DividerBrush, Tokens.Neutral200Brush, clicked);

    static Button Build(string text, string? icon, IBrush background, IBrush foreground, IBrush border,
        IBrush pressed, Action clicked)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = IconGap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (icon is not null)
        {
            content.Children.Add(Lucide.Icon(icon, IconSize, foreground));
        }

        content.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = Tokens.Fonts.Body,
            FontSize = 13.5,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center
        });

        var button = new Button
        {
            Theme = Bare,
            Content = content,
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            Padding = new Thickness(Tokens.Space.S4, 0),
            Height = Height,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // Hover and pressed are themed rather than left to the control theme, which would paint a
        // rounded Fluent highlight over a square blueprint object.
        button.PointerEntered += (_, _) => button.Background = Hover(background);
        button.PointerExited += (_, _) => button.Background = background;
        button.AddHandler(InputElement.PointerPressedEvent, (_, _) => button.Background = pressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        button.AddHandler(InputElement.PointerReleasedEvent, (_, _) => button.Background = Hover(background),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        button.Click += (_, _) => clicked();
        return button;
    }

    static IBrush Hover(IBrush background) =>
        ReferenceEquals(background, Tokens.AccentBrush) ? Tokens.Accent600Brush
        : ReferenceEquals(background, Danger_) ? DangerHover
        : Tokens.Neutral200Brush;
}
