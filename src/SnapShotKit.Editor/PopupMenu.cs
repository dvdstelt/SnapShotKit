using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>One line in a menu: a label, an optional shortcut, and what it does. A null label is a separator.</summary>
public readonly record struct MenuEntry(string? Label, string? Shortcut, Action? Invoke)
{
    public static MenuEntry Separator => new(null, null, null);

    public static MenuEntry Item(string label, string? shortcut, Action invoke) => new(label, shortcut, invoke);

    /// <summary>
    /// A line that says something rather than doing something.
    ///
    /// What a menu with nothing to offer shows. An empty popup reads as a bug, and a live-looking
    /// item that does nothing when pressed reads as a worse one.
    /// </summary>
    public static MenuEntry Note(string label) => new(label, null, null);
}

/// <summary>
/// The panel of choices a menu drops down, wherever it is dropped from.
///
/// Shared by the menu bar and by the context menu on a recent capture, so the two are the same
/// object rather than two things that resemble each other. The design specifies this panel down to
/// its width and the registration marks on its corners, and a second implementation would drift
/// from the first the first time either was touched.
/// </summary>
public static class PopupMenu
{
    const double Width = 252;

    /// <summary>Builds the panel. Choosing anything closes the popup it was built for.</summary>
    public static Control Build(IReadOnlyList<MenuEntry> entries, Popup popup)
    {
        var items = new StackPanel { Margin = new Thickness(0, Tokens.Space.S1) };

        foreach (var entry in entries)
        {
            if (entry.Label is null)
            {
                items.Children.Add(new Border
                {
                    Height = 1,
                    Background = Tokens.DividerBrush,
                    Margin = new Thickness(0, Tokens.Space.S1)
                });

                continue;
            }

            // A note is a line of text in the menu's shape, not a control: no hover, no hand, and
            // the quieter ink the system uses for anything that is not to be acted on.
            var note = entry.Invoke is null;

            var label = Labels.Body(entry.Label, 13.5, note ? Tokens.Neutral500Brush : Tokens.Neutral900Brush);
            label.VerticalAlignment = VerticalAlignment.Center;

            var shortcut = Labels.Body(entry.Shortcut ?? string.Empty, 12.5, Tokens.Neutral500Brush);
            shortcut.VerticalAlignment = VerticalAlignment.Center;
            shortcut.HorizontalAlignment = HorizontalAlignment.Right;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(shortcut, 1);
            row.Children.Add(label);
            row.Children.Add(shortcut);

            var item = new Border
            {
                Child = row,
                Padding = new Thickness(Tokens.Space.S4, 5),
                Background = Tokens.BgBrush,
                Cursor = new Cursor(note ? StandardCursorType.Arrow : StandardCursorType.Hand)
            };

            if (!note)
            {
                var invoke = entry.Invoke;
                item.PointerPressed += (_, _) =>
                {
                    popup.IsOpen = false;
                    invoke?.Invoke();
                };

                item.PointerEntered += (_, _) => item.Background = Tokens.Accent100Brush;
                item.PointerExited += (_, _) => item.Background = Tokens.BgBrush;
            }

            items.Children.Add(item);
        }

        return Blueprint.Wrap(new Border
        {
            Width = Width,
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            BoxShadow = Tokens.ShadowLg,
            Child = items
        }, drawFrame: false);
    }
}
