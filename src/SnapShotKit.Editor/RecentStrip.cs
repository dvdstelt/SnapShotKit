using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>
/// The band of recent captures along the bottom of the editor.
///
/// A plain strip rather than a panel that can be collapsed or rearranged: it answers one question,
/// which is "which of the last few captures did I mean", and the picture is the only thing that
/// answers it. Names and times are there to separate two similar shots, not to be read.
///
/// Newest sits on the left, because that is where the eye starts and the newest capture is almost
/// always the one being looked for.
///
/// The tiles are built by hand rather than through a ListBox. A list control brings a selection
/// visual of its own — a filled rounded block — which is precisely what this design does not want,
/// and suppressing it is more work than laying out a row of tiles.
///
/// The strip asks rather than acts: it raises what the user chose and leaves opening, copying and
/// deleting to the window, which is the only thing that knows whether the capture in question is
/// the one on the canvas.
/// </summary>
public sealed class RecentStrip : Border
{
    const double TileWidth = 116;
    const double TileHeight = 52;

    /// <summary>The delete control on a tile's corner. Small, because the tile it sits on is 116 by 52.</summary>
    const double BadgeSize = 20;

    readonly StackPanel tiles;
    readonly TextBlock caption;
    readonly TextBlock current;

    /// <summary>
    /// The context menu, built once and re-anchored to whichever tile it is asked for.
    ///
    /// One popup for the whole strip rather than one per tile, and never moved in the tree: a popup
    /// put under a new parent stops opening and says nothing about it, and the tiles are thrown away
    /// and rebuilt every time the strip is filled. Only its placement target changes.
    /// </summary>
    readonly Popup menu;

    public RecentStrip()
    {
        Background = Tokens.BgBrush;
        BorderBrush = Tokens.DividerBrush;
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(Tokens.Space.S6, Tokens.Space.S3);

        caption = Labels.Heading("TODAY", 11.5, 0.18);
        current = Labels.Body(string.Empty, 12.5, Tokens.Neutral700Brush);
        current.HorizontalAlignment = HorizontalAlignment.Right;

        menu = new Popup
        {
            // Above the tile it was asked for. The strip is the bottom band of the window, so there
            // is never room below it, and a menu anchored to the tile lands in the same place every
            // time rather than wherever the pointer happened to be.
            Placement = PlacementMode.TopEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = true
        };

        var captionRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(caption, 0);
        Grid.SetColumn(current, 1);
        captionRow.Children.Add(caption);
        captionRow.Children.Add(current);

        // A popup has to live in the visual tree to be hosted at all. A zero-sized cell sharing the
        // caption row asks for no space of its own, which a row in the stack above the tiles would.
        var host = new Panel { Width = 0, Height = 0, Children = { menu } };
        Grid.SetColumn(host, 0);
        captionRow.Children.Add(host);

        tiles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.S4 };

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = tiles
        };

        Child = new StackPanel
        {
            Spacing = Tokens.Space.S2,
            Children = { captionRow, scroller }
        };
    }

    /// <summary>The capture to put on the canvas.</summary>
    public event Action<string>? Chosen;

    /// <summary>The capture to put on the clipboard, annotations and all.</summary>
    public event Action<string>? CopyRequested;

    /// <summary>The capture to delete from disk.</summary>
    public event Action<string>? DeleteRequested;

    /// <summary>Fills the strip, leaving the open snapshot in place and marked.</summary>
    public void Show(IReadOnlyList<SnapshotItem> items, string openPath)
    {
        caption.Text = Caption(items);

        var open = items.FirstOrDefault(item => string.Equals(item.Entry.Path, openPath, StringComparison.Ordinal));
        current.Text = open is null ? string.Empty : $"{open.Time} — {open.ShortName}";

        // Whatever it was offered on is about to be thrown away, and a menu left standing over a
        // rebuilt strip would act on a tile that is no longer there.
        menu.IsOpen = false;

        tiles.Children.Clear();

        Control? openTile = null;

        foreach (var item in items)
        {
            var tile = Tile(item, ReferenceEquals(item, open));
            tiles.Children.Add(tile);

            if (ReferenceEquals(item, open))
            {
                openTile = tile;
            }
        }

        // The open capture is not necessarily among the newest, so it can sit off the end of the
        // strip. Bringing it into view means the accent ring is actually visible saying so.
        openTile?.BringIntoView();
    }

    static string Caption(IReadOnlyList<SnapshotItem> items)
    {
        var today = items.Count(item => item.Entry.Modified.Date == DateTime.Today);

        return today > 0
            ? $"TODAY · {DateTime.Today:d MMM} · {today} CAPTURE{(today == 1 ? string.Empty : "S")}".ToUpperInvariant()
            : $"{items.Count} CAPTURE{(items.Count == 1 ? string.Empty : "S")}";
    }

    /// <summary>
    /// One tile: the picture, framed and marked, with its time underneath.
    ///
    /// The open capture is ringed in the accent and lifted off the ground, so the strip always says
    /// which one is on the canvas without having to hide it from the list.
    /// </summary>
    Control Tile(SnapshotItem item, bool open)
    {
        var picture = new Image { Stretch = Stretch.UniformToFill };
        picture.Bind(Image.SourceProperty, new Avalonia.Data.Binding(nameof(SnapshotItem.Thumbnail)) { Source = item });

        var badge = DeleteBadge(item);

        var frame = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            Background = Tokens.Neutral200Brush,
            CornerRadius = Tokens.Radius,
            ClipToBounds = true,
            Child = new Panel { Children = { picture, badge } },
            BorderBrush = open ? Tokens.AccentBrush : Brushes.Transparent,
            BorderThickness = new Thickness(open ? 2 : 0),
            BoxShadow = open ? Tokens.ShadowMd : default
        };

        var time = Labels.Body(item.Time, 11.5, open ? Tokens.Accent800Brush : Tokens.Neutral600Brush);
        time.TextAlignment = TextAlignment.Center;
        time.HorizontalAlignment = HorizontalAlignment.Stretch;

        var tile = new StackPanel
        {
            Spacing = Tokens.Space.S1,
            Cursor = new Cursor(StandardCursorType.Hand),
            Children = { Blueprint.Wrap(frame, drawFrame: !open), time }
        };

        ToolTip.SetTip(tile, item.Name);

        // The badge is a child of the tile, so moving onto it is not leaving the tile: the pointer
        // can travel from the picture to the button without the button disappearing under it.
        tile.PointerEntered += (_, _) => badge.IsVisible = true;
        tile.PointerExited += (_, _) => badge.IsVisible = false;

        tile.PointerPressed += (_, e) =>
        {
            var properties = e.GetCurrentPoint(tile).Properties;

            if (properties.IsRightButtonPressed)
            {
                ShowMenu(item, tile);
                return;
            }

            // One click opens. These are cheap to switch between and the whole point of the strip is
            // to move quickly, so asking for a double click would be friction for its own sake.
            if (properties.IsLeftButtonPressed)
            {
                Chosen?.Invoke(item.Entry.Path);
            }
        };

        return tile;
    }

    /// <summary>
    /// The delete control that appears on a tile while the pointer is over it.
    ///
    /// Hidden until then, because the strip is a row of pictures and a permanent row of buttons over
    /// them would compete with the only thing the strip is for. It fills with the system's one red
    /// under the pointer, so the control that destroys a capture never looks like the tile that
    /// opens it, and it swallows its own click: a press that both deleted a capture and opened it
    /// would be the worst of both.
    /// </summary>
    Border DeleteBadge(SnapshotItem item)
    {
        var quiet = Lucide.Icon(Lucide.Delete, 13, Tokens.Neutral800Brush);
        var alarmed = Lucide.Icon(Lucide.Delete, 13, Tokens.BgBrush);

        var badge = new Border
        {
            Width = BadgeSize,
            Height = BadgeSize,
            IsVisible = false,
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Tokens.Space.S1, Tokens.Space.S1, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = quiet
        };

        ToolTip.SetTip(badge, $"Delete {item.Name}");

        badge.PointerEntered += (_, _) =>
        {
            badge.Background = Tokens.DangerBrush;
            badge.BorderBrush = Tokens.DangerBrush;
            badge.Child = alarmed;
        };

        badge.PointerExited += (_, _) =>
        {
            badge.Background = Tokens.BgBrush;
            badge.BorderBrush = Tokens.DividerBrush;
            badge.Child = quiet;
        };

        badge.PointerPressed += (_, e) =>
        {
            e.Handled = true;

            if (e.GetCurrentPoint(badge).Properties.IsLeftButtonPressed)
            {
                DeleteRequested?.Invoke(item.Entry.Path);
            }
        };

        return badge;
    }

    void ShowMenu(SnapshotItem item, Control tile)
    {
        menu.IsOpen = false;
        menu.PlacementTarget = tile;

        menu.Child = PopupMenu.Build(
        [
            MenuEntry.Item("Open", null, () => Chosen?.Invoke(item.Entry.Path)),
            MenuEntry.Separator,
            MenuEntry.Item("Copy", null, () => CopyRequested?.Invoke(item.Entry.Path)),
            MenuEntry.Item("Delete", null, () => DeleteRequested?.Invoke(item.Entry.Path))
        ], menu);

        menu.IsOpen = true;
    }
}
