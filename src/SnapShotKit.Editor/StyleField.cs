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
/// cell it is given, so the row can be resized without every sample being redrawn by hand.
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
/// The row of ready-made looks for whatever tool is in hand.
///
/// It leads the settings because it is the coarse choice the fine ones refine: pick the look, then
/// adjust it if this one is the exception. The chosen style is ringed the way a chosen colour is,
/// and nothing is ringed once a setting has been changed away from it, which is the honest answer
/// to "which of these am I wearing".
/// </summary>
public sealed class StyleField : StackPanel
{
    const double CellWidth = 32;
    const double CellHeight = 24;

    readonly Action<AnnotationStyle> chosen;
    readonly List<(AnnotationStyle Style, Border Ring)> cells = [];

    /// <summary>The set on show, so that the row is rebuilt when the tool changes and not on every repaint.</summary>
    IReadOnlyList<AnnotationStyle>? showing;

    public StyleField(Action<AnnotationStyle> chosen)
    {
        this.chosen = chosen;

        Orientation = Orientation.Horizontal;
        Spacing = 4;
        VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>Shows a tool's styles, with the one currently worn marked, or none of them.</summary>
    public void Show(IReadOnlyList<AnnotationStyle> styles, AnnotationStyle? worn)
    {
        if (!ReferenceEquals(showing, styles))
        {
            showing = styles;
            Rebuild(styles);
        }

        foreach (var (style, ring) in cells)
        {
            ring.BorderBrush = ReferenceEquals(style, worn) ? Tokens.AccentBrush : Brushes.Transparent;
        }
    }

    void Rebuild(IReadOnlyList<AnnotationStyle> styles)
    {
        cells.Clear();
        Children.Clear();

        foreach (var style in styles)
        {
            var cell = new Border
            {
                Width = CellWidth,
                Height = CellHeight,
                // Not the band's own ground: half of these styles are white, and white on white is
                // a style nobody can see. A shade of the neutral ramp stands in for the screenshot.
                Background = Tokens.Neutral200Brush,
                BorderBrush = Tokens.DividerBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = Tokens.Radius,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StylePreview(style.Look)
            };

            // Ringed at an offset rather than given a heavier border, so choosing one never changes
            // the size or the position of the row.
            var ring = new Border
            {
                Padding = new Thickness(2),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                CornerRadius = Tokens.Radius,
                Child = cell
            };

            ToolTip.SetTip(cell, style.Name);

            var picked = style;
            cell.PointerPressed += (_, _) => chosen(picked);

            cells.Add((style, ring));
            Children.Add(ring);
        }
    }
}
