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
/// </summary>
public sealed class RecentStrip : Border
{
    const double TileWidth = 116;
    const double TileHeight = 52;

    readonly StackPanel tiles;
    readonly TextBlock caption;
    readonly TextBlock current;

    public RecentStrip()
    {
        Background = Tokens.BgBrush;
        BorderBrush = Tokens.DividerBrush;
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(Tokens.Space.S6, Tokens.Space.S3);

        caption = Labels.Heading("TODAY", 11.5, 0.18);
        current = Labels.Body(string.Empty, 12.5, Tokens.Neutral700Brush);
        current.HorizontalAlignment = HorizontalAlignment.Right;

        var captionRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(caption, 0);
        Grid.SetColumn(current, 1);
        captionRow.Children.Add(caption);
        captionRow.Children.Add(current);

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

    public event Action<string>? Chosen;

    /// <summary>Fills the strip, leaving the open snapshot in place and marked.</summary>
    public void Show(IReadOnlyList<SnapshotItem> items, string openPath)
    {
        caption.Text = Caption(items);

        var open = items.FirstOrDefault(item => string.Equals(item.Entry.Path, openPath, StringComparison.Ordinal));
        current.Text = open is null ? string.Empty : $"{open.Time} — {open.ShortName}";

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

        var frame = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            Background = Tokens.Neutral200Brush,
            CornerRadius = Tokens.Radius,
            ClipToBounds = true,
            Child = picture,
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

        // One click opens. These are cheap to switch between and the whole point of the strip is to
        // move quickly, so asking for a double click would be friction for its own sake.
        tile.PointerPressed += (_, _) => Chosen?.Invoke(item.Entry.Path);

        return tile;
    }
}
