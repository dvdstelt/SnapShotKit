using Avalonia;
using Avalonia.Controls;
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

        SnapshotRenderer.DrawAnnotation(context, sample, origin, scale);
    }
}

/// <summary>
/// The ready-made looks for whatever tool is in hand, all of them, as a grid.
///
/// It leads the sidebar because it is the coarse choice the settings under it refine: pick the look,
/// then adjust it if this one is the exception. The chosen style is ringed the way a chosen colour
/// is, and nothing is ringed once a setting has been changed away from it, which is the honest answer
/// to "which of these am I wearing".
///
/// Every style is on show rather than the few used last with the rest a click away. That rationing
/// was for a band that had other work to do; a sidebar has the room, and a choice that can be made on
/// sight beats one that has to be remembered to be in a gallery.
/// </summary>
public sealed class StyleGrid : WrapPanel
{
    const double CellWidth = 52;
    const double CellHeight = 36;

    readonly Action<AnnotationStyle> chosen;

    readonly List<(AnnotationStyle Style, Border Ring)> cells = [];

    IReadOnlyList<AnnotationStyle> showing = [];

    public StyleGrid(Action<AnnotationStyle> chosen)
    {
        this.chosen = chosen;
        Orientation = Orientation.Horizontal;
    }

    /// <summary>Shows a tool's styles, with the one currently worn marked, or none of them.</summary>
    public void Show(IReadOnlyList<AnnotationStyle> styles, AnnotationStyle? worn)
    {
        if (!ReferenceEquals(showing, styles))
        {
            showing = styles;
            Rebuild();
        }

        foreach (var (style, ring) in cells)
        {
            ring.BorderBrush = ReferenceEquals(style, worn) ? Tokens.AccentBrush : Brushes.Transparent;
        }
    }

    void Rebuild()
    {
        cells.Clear();
        Children.Clear();

        foreach (var style in showing)
        {
            var ring = Ringed(style);
            cells.Add((style, ring));
            Children.Add(ring);
        }
    }

    /// <summary>A style in its cell, ringed when it is the one being worn.</summary>
    Border Ringed(AnnotationStyle style)
    {
        var cell = new Border
        {
            Width = CellWidth,
            Height = CellHeight,
            // Not the sidebar's own ground: half of these styles are white, and white on white is a
            // style nobody can see. A shade of the neutral ramp stands in for the screenshot.
            Background = Tokens.Neutral200Brush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StylePreview(style.Look)
        };

        ToolTip.SetTip(cell, style.Name);
        cell.PointerPressed += (_, _) => chosen(style);

        // Ringed at an offset rather than given a heavier border, so choosing one never changes the
        // size or the position of anything in the grid.
        return new Border
        {
            Padding = new Thickness(2),
            Margin = new Thickness(0, 0, 4, 4),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = Tokens.Radius,
            Child = cell
        };
    }
}
