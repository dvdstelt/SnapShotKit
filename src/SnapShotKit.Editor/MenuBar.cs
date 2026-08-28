using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>One line in a menu: a label, an optional shortcut, and what it does. A null label is a separator.</summary>
public readonly record struct MenuEntry(string? Label, string? Shortcut, Action? Invoke)
{
    public static MenuEntry Separator => new(null, null, null);

    public static MenuEntry Item(string label, string? shortcut, Action invoke) => new(label, shortcut, invoke);
}

/// <summary>
/// The application's menu bar and the wordmark beside it.
///
/// Built out of a bar and popups rather than Avalonia's Menu, because the design specifies this
/// menu down to the width of the dropdown and the registration marks on its corners, and restyling
/// the stock control that far is more work than drawing it.
///
/// Saving and exporting live here and nowhere else. The alternative, a row of buttons above the
/// canvas, spends permanent screen space on commands that are pressed once at the end of a session
/// and are already on keys.
/// </summary>
public sealed class MenuBar : Border
{
    const double BarHeight = 34;
    const double MenuWidth = 252;

    readonly TextBlock fileName;
    readonly TextBlock dirtyState;
    readonly List<(Border Header, Popup Popup)> menus = [];

    public MenuBar(string wordmark)
    {
        Background = Tokens.BgBrush;
        BorderBrush = Tokens.DividerBrush;
        BorderThickness = new Thickness(0, 0, 0, 1);
        Height = BarHeight;

        Headers = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };

        Headers.Children.Add(new TextBlock
        {
            Text = wordmark.ToUpperInvariant(),
            FontFamily = Tokens.Fonts.Heading,
            FontWeight = Tokens.Fonts.HeadingWeight,
            FontSize = 13,
            LetterSpacing = Tokens.Tracking(13, 0.22),
            Foreground = Tokens.Neutral800Brush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Tokens.Space.S4, 0, Tokens.Space.S6, 0)
        });

        fileName = Labels.Body(string.Empty, 12.5, Tokens.Neutral600Brush);
        fileName.VerticalAlignment = VerticalAlignment.Center;

        dirtyState = Labels.Body(string.Empty, 12.5, Tokens.Accent700Brush);
        dirtyState.VerticalAlignment = VerticalAlignment.Center;

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S3,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, Tokens.Space.S4, 0),
            Children = { fileName, dirtyState }
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(Headers, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(Headers);
        grid.Children.Add(right);

        Child = grid;
    }

    StackPanel Headers { get; }

    /// <summary>Adds a menu and its contents. The entries are rebuilt on each open, so shortcuts and state stay current.</summary>
    public void Add(string title, Func<IReadOnlyList<MenuEntry>> entries)
    {
        var label = Labels.Body(title, 13.5, Tokens.Neutral800Brush);
        label.VerticalAlignment = VerticalAlignment.Center;

        var header = new Border
        {
            Child = label,
            Padding = new Thickness(Tokens.Space.S3, 0),
            Background = Tokens.BgBrush,
            CornerRadius = Tokens.Radius,
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var popup = new Popup
        {
            PlacementTarget = header,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = true
        };

        // Not a child of the header: a popup has to live in the visual tree to be hosted, and the
        // header is only 34 pixels tall.
        Headers.Children.Add(header);
        menus.Add((header, popup));

        header.PointerPressed += (_, _) =>
        {
            var wasOpen = popup.IsOpen;
            CloseAll();

            if (!wasOpen)
            {
                popup.Child = BuildMenu(entries(), popup);
                popup.IsOpen = true;
                Highlight(header, label, open: true);
            }
        };

        header.PointerEntered += (_, _) =>
        {
            if (!popup.IsOpen)
            {
                header.Background = Tokens.Neutral200Brush;
            }
        };

        header.PointerExited += (_, _) =>
        {
            if (!popup.IsOpen)
            {
                header.Background = Tokens.BgBrush;
            }
        };

        popup.Closed += (_, _) => Highlight(header, label, open: false);

        // The popup needs a place in the tree; a zero-sized cell in the bar is as good as any.
        Headers.Children.Add(new Panel { Width = 0, Children = { popup } });
    }

    static void Highlight(Border header, TextBlock label, bool open)
    {
        header.Background = open ? Tokens.AccentBrush : Tokens.BgBrush;
        label.Foreground = open ? Tokens.BgBrush : Tokens.Neutral800Brush;
    }

    /// <summary>Marks one menu as the view currently being looked at, the way a tab bar would.</summary>
    public void MarkActive(string title)
    {
        foreach (var (header, _) in menus)
        {
            if (header.Child is TextBlock label)
            {
                var active = string.Equals(label.Text, title, StringComparison.Ordinal);
                header.Background = active ? Tokens.Neutral200Brush : Tokens.BgBrush;
            }
        }
    }

    public void CloseAll()
    {
        foreach (var (_, popup) in menus)
        {
            popup.IsOpen = false;
        }
    }

    static Control BuildMenu(IReadOnlyList<MenuEntry> entries, Popup popup)
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

            var label = Labels.Body(entry.Label, 13.5, Tokens.Neutral900Brush);
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
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            var invoke = entry.Invoke;
            item.PointerPressed += (_, _) =>
            {
                popup.IsOpen = false;
                invoke?.Invoke();
            };

            item.PointerEntered += (_, _) => item.Background = Tokens.Accent100Brush;
            item.PointerExited += (_, _) => item.Background = Tokens.BgBrush;

            items.Children.Add(item);
        }

        return Blueprint.Wrap(new Border
        {
            Width = MenuWidth,
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            BoxShadow = Tokens.ShadowLg,
            Child = items
        }, drawFrame: false);
    }

    /// <summary>Shows which snapshot is open and whether it has been saved.</summary>
    public void Show(string name, bool dirty)
    {
        fileName.Text = name;
        dirtyState.Text = dirty ? "unsaved" : "saved";
        dirtyState.Foreground = dirty ? Tokens.Accent700Brush : Tokens.Neutral500Brush;
    }
}
