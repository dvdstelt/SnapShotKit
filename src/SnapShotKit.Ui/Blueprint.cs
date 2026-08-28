using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SnapShotKit.Ui;

/// <summary>
/// The wireframe frame every framed object in the system wears: a hairline border with a small
/// crosshair registration mark at each corner.
///
/// This is the design system's signature and it is not optional trim. Its own guidance is blunt
/// about it: do not drop the registration marks from a framed element. Windows, the canvas,
/// thumbnails, dropdowns and the primary button all carry them, which is why this is one control
/// used everywhere rather than four lines copied into each.
///
/// The marks are drawn straddling the frame's corners, so the control reserves <see cref="Reach"/>
/// around its child for them. Wrapping a child in one of these therefore grows it by 11 pixels in
/// each direction; that space is part of the object as the design draws it.
/// </summary>
public sealed class Blueprint : Decorator
{
    /// <summary>Half a mark. The arms are 11 pixels long and centred on the corner, so this much sticks out beyond the frame.</summary>
    public const double Reach = 5.5;

    static readonly IPen MarkPen = new Avalonia.Media.Immutable.ImmutablePen(
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(0x8C, 0x1D, 0x1F, 0x20)), 1);

    public Blueprint() => Padding = new Thickness(Reach);

    // The reserved space is applied here rather than left to Decorator, which carries a Padding
    // property but does not measure or arrange with it. Without this the child fills the whole
    // control and the marks are drawn on top of it instead of around it.
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return new Size(2 * Reach, 2 * Reach);
        }

        child.Measure(availableSize.Deflate(Padding));
        return child.DesiredSize.Inflate(Padding);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize).Deflate(Padding));
        return finalSize;
    }

    /// <summary>
    /// Whether to draw the hairline border itself.
    ///
    /// Off for objects that already carry their own edge, such as the accent-filled primary button
    /// or a thumbnail that draws its own border, where a second hairline would double up.
    /// </summary>
    public bool DrawFrame { get; set; } = true;

    /// <summary>The frame's own fill, if it has one. Cards and figures are transparent line drawings; the primary button is not.</summary>
    public IBrush? Fill { get; set; }

    public override void Render(DrawingContext context)
    {
        // A one pixel pen straddles the coordinate it is given, so the frame sits on half pixels to
        // land on a whole device pixel instead of smeared across two.
        var frame = new Rect(
            Reach + 0.5,
            Reach + 0.5,
            Math.Max(Bounds.Width - 2 * Reach - 1, 0),
            Math.Max(Bounds.Height - 2 * Reach - 1, 0));

        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        if (Fill is not null || DrawFrame)
        {
            context.DrawRectangle(Fill, DrawFrame ? Tokens.DividerPen : null, frame);
        }

        foreach (var corner in new[] { frame.TopLeft, frame.TopRight, frame.BottomLeft, frame.BottomRight })
        {
            context.DrawLine(MarkPen, new Point(corner.X, corner.Y - Reach), new Point(corner.X, corner.Y + Reach));
            context.DrawLine(MarkPen, new Point(corner.X - Reach, corner.Y), new Point(corner.X + Reach, corner.Y));
        }
    }

    /// <summary>Wraps a control in a blueprint frame.</summary>
    public static Blueprint Wrap(Control child, bool drawFrame = true, IBrush? fill = null) => new()
    {
        Child = child,
        DrawFrame = drawFrame,
        Fill = fill
    };
}
