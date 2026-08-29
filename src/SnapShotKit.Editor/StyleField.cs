using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>
/// One ready-made look, drawn.
///
/// Through the same renderer the canvas and the export use, so a preview cannot promise something
/// the tool then does not do. The sample is written in a nominal space and scaled into whatever
/// cell it is given, so the same drawing serves the band and the gallery at their different sizes.
/// </summary>
public sealed class StylePreview : Control
{
    readonly Annotation sample;

    public StylePreview(Annotation look) => sample = AnnotationStyles.Sample(look);

    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(
            Bounds.Width / AnnotationStyles.Nominal.Width,
            Bounds.Height / AnnotationStyles.Nominal.Height);

        if (scale <= 0)
        {
            return;
        }

        var origin = new Point(
            (Bounds.Width - AnnotationStyles.Nominal.Width * scale) / 2,
            (Bounds.Height - AnnotationStyles.Nominal.Height * scale) / 2);

        // Text is placed by what it measures rather than by a coordinate written down in advance:
        // the words are the shape, and how much room they take is the font's business.
        if (sample is TextAnnotation text)
        {
            var measured = SnapshotRenderer.Format(text, scale);
            origin = new Point((Bounds.Width - measured.Width) / 2, (Bounds.Height - measured.Height) / 2);
        }

        SnapshotRenderer.DrawAnnotation(context, sample, null, origin, scale);
    }
}

/// <summary>
/// The ready-made looks for whatever tool is in hand: the last few used, and a gallery of the rest.
///
/// It leads the settings because it is the coarse choice the fine ones refine: pick the look, then
/// adjust it if this one is the exception. The chosen style is ringed the way a chosen colour is,
/// and nothing is ringed once a setting has been changed away from it, which is the honest answer
/// to "which of these am I wearing".
///
/// Only three are on the band because the band has other work to do. They are the three used most
/// recently, and a fourth one picked from the gallery takes the place of whichever of them has gone
/// longest unused rather than pushing the row along: the hand learns where a style sits, and a row
/// that reshuffled itself after every click would teach it nothing.
/// </summary>
public sealed class StyleField : StackPanel
{
    const double CellWidth = 32;
    const double CellHeight = 24;

    /// <summary>How many stay on the band. The rest are one click further on.</summary>
    const int Slots = 3;

    /// <summary>The gallery draws them larger: it is a place to choose from rather than to reach for.</summary>
    const double GalleryWidth = 46;

    const double GalleryHeight = 34;
    const int GalleryColumns = 4;

    readonly Action<AnnotationStyle> chosen;
    readonly Popup gallery;
    readonly Border opener;

    /// <summary>Holds the cells on the band. Rebuilt as the row changes, while everything beside it stays put.</summary>
    readonly StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>What each tool has on the band, and how recently each of its styles was used.</summary>
    readonly Dictionary<IReadOnlyList<AnnotationStyle>, Recents> recents = [];

    readonly List<(AnnotationStyle Style, Border Ring)> cells = [];

    IReadOnlyList<AnnotationStyle> showing = [];
    AnnotationStyle? worn;

    public StyleField(Action<AnnotationStyle> chosen)
    {
        this.chosen = chosen;

        Orientation = Orientation.Horizontal;
        Spacing = 4;
        VerticalAlignment = VerticalAlignment.Center;

        opener = Cell(Lucide.Icon(Lucide.More, 14, Tokens.Neutral700Brush), CellWidth, CellHeight);
        ToolTip.SetTip(opener, "More styles");
        opener.PointerPressed += (_, _) => Open();

        gallery = new Popup
        {
            PlacementTarget = opener,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true
        };

        // Put together once and never taken apart. A popup moved to a new parent as the row is
        // rebuilt is a popup that quietly stops opening.
        Children.Add(row);
        Children.Add(opener);
        Children.Add(new Panel { Width = 0, Children = { gallery } });
    }

    /// <summary>Shows a tool's styles, with the one currently worn marked, or none of them.</summary>
    public void Show(IReadOnlyList<AnnotationStyle> styles, AnnotationStyle? worn)
    {
        this.worn = worn;

        var slots = Remembered(styles).Slots;

        if (!ReferenceEquals(showing, styles) || !cells.Select(cell => cell.Style).SequenceEqual(slots))
        {
            showing = styles;
            Rebuild(slots, styles.Count);
        }

        foreach (var (style, ring) in cells)
        {
            ring.BorderBrush = ReferenceEquals(style, worn) ? Tokens.AccentBrush : Brushes.Transparent;
        }
    }

    void Rebuild(IReadOnlyList<AnnotationStyle> slots, int available)
    {
        cells.Clear();
        row.Children.Clear();

        foreach (var style in slots)
        {
            var ring = Ringed(style, CellWidth, CellHeight);
            cells.Add((style, ring));
            row.Children.Add(ring);
        }

        // Nothing to open when everything a tool has is already on the band.
        opener.IsVisible = available > slots.Count;
    }

    /// <summary>A style in its cell, ringed when it is the one being worn.</summary>
    Border Ringed(AnnotationStyle style, double width, double height)
    {
        var cell = Cell(new StylePreview(style.Look), width, height);
        ToolTip.SetTip(cell, style.Name);

        cell.PointerPressed += (_, _) => Choose(style);

        // Ringed at an offset rather than given a heavier border, so choosing one never changes the
        // size or the position of the row.
        return new Border
        {
            Padding = new Thickness(2),
            BorderThickness = new Thickness(1),
            BorderBrush = ReferenceEquals(style, worn) ? Tokens.AccentBrush : Brushes.Transparent,
            CornerRadius = Tokens.Radius,
            Child = cell
        };
    }

    static Border Cell(Control content, double width, double height) => new()
    {
        Width = width,
        Height = height,
        // Not the band's own ground: half of these styles are white, and white on white is a style
        // nobody can see. A shade of the neutral ramp stands in for the screenshot.
        Background = Tokens.Neutral200Brush,
        BorderBrush = Tokens.DividerBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = Tokens.Radius,
        Cursor = new Cursor(StandardCursorType.Hand),
        Child = content
    };

    void Open()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("Auto", GalleryColumns))) };

        for (var index = 0; index < showing.Count; index += GalleryColumns)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var index = 0; index < showing.Count; index++)
        {
            var ring = Ringed(showing[index], GalleryWidth, GalleryHeight);

            Grid.SetColumn(ring, index % GalleryColumns);
            Grid.SetRow(ring, index / GalleryColumns);
            grid.Children.Add(ring);
        }

        gallery.Child = Blueprint.Wrap(new Border
        {
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            BoxShadow = Tokens.ShadowLg,
            Padding = new Thickness(Tokens.Space.S3),
            Child = grid
        }, drawFrame: false);

        gallery.IsOpen = true;
    }

    void Choose(AnnotationStyle style)
    {
        gallery.IsOpen = false;

        Remembered(showing).Use(style);
        chosen(style);
    }

    Recents Remembered(IReadOnlyList<AnnotationStyle> styles)
    {
        if (!recents.TryGetValue(styles, out var remembered))
        {
            remembered = new Recents(styles, Slots);
            recents[styles] = remembered;
        }

        return remembered;
    }

    /// <summary>
    /// Which of a tool's styles are on the band, and how recently each was used.
    ///
    /// Kept for as long as the window is open and no longer. Carrying it between sessions would
    /// mean a file to write, which is a thing to get wrong for a row that costs one click to put
    /// back.
    /// </summary>
    sealed class Recents
    {
        readonly Dictionary<AnnotationStyle, int> used = [];

        int clock;

        public Recents(IReadOnlyList<AnnotationStyle> styles, int slots) =>
            Slots = [.. styles.Take(slots)];

        /// <summary>The styles on the band, in the order they sit in.</summary>
        public List<AnnotationStyle> Slots { get; }

        public void Use(AnnotationStyle style)
        {
            used[style] = ++clock;

            if (Slots.Contains(style) || Slots.Count == 0)
            {
                return;
            }

            // Into the place of whichever is stalest, so the other two do not move. A style never
            // used at all is staler than one that has been, and among several never used it is the
            // rightmost that goes: the row fills up from the far end, leaving the one a tool opens
            // with in place the longest.
            var stalest = 0;

            for (var slot = 1; slot < Slots.Count; slot++)
            {
                if (Age(Slots[slot]) <= Age(Slots[stalest]))
                {
                    stalest = slot;
                }
            }

            Slots[stalest] = style;
        }

        int Age(AnnotationStyle style) => used.GetValueOrDefault(style, 0);
    }
}
