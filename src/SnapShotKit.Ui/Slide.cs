using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace SnapShotKit.Ui;

/// <summary>
/// A slider drawn to match the rest of the system: a hairline track, an accent fill, and a square
/// thumb of the same shape as the handles on the canvas.
///
/// Drawn rather than restyled. The stock slider's thumb is a filled circle in the toolkit's own
/// blue, which is both the wrong shape and the wrong colour here, and reaching every part of its
/// template is more work than painting three rectangles.
/// </summary>
public sealed class Slide : Control
{
    const double ThumbWidth = 9;
    const double ThumbHeight = 16;
    const double TrackHeight = 2;

    readonly double minimum;
    readonly double maximum;

    public Slide(double minimum, double maximum, double value)
    {
        this.minimum = minimum;
        this.maximum = maximum;

        Value = Math.Clamp(value, minimum, maximum);
        Height = 20;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public double Value
    {
        get;
        private set
        {
            field = value;
            InvalidateVisual();
        }
    }

    public event Action<double>? Moved;

    /// <summary>Sets the value without raising <see cref="Moved"/>, for syncing to something else.</summary>
    public void Show(double value)
    {
        Value = Math.Clamp(value, minimum, maximum);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        e.Pointer.Capture(this);
        Drag(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (Equals(e.Pointer.Captured, this))
        {
            Drag(e.GetPosition(this));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) => e.Pointer.Capture(null);

    void Drag(Point at)
    {
        var travel = Math.Max(Bounds.Width - ThumbWidth, 1);
        var fraction = Math.Clamp((at.X - ThumbWidth / 2) / travel, 0, 1);

        Value = minimum + fraction * (maximum - minimum);
        Moved?.Invoke(Value);
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        var middle = Bounds.Height / 2;
        var travel = Math.Max(Bounds.Width - ThumbWidth, 1);
        var fraction = maximum > minimum ? (Value - minimum) / (maximum - minimum) : 0;
        var thumbX = ThumbWidth / 2 + fraction * travel;

        var track = new Rect(ThumbWidth / 2, middle - TrackHeight / 2, travel, TrackHeight);

        context.FillRectangle(Tokens.Neutral300Brush, track);
        context.FillRectangle(Tokens.AccentBrush, track.WithWidth(Math.Max(thumbX - ThumbWidth / 2, 0)));

        var thumb = new Rect(thumbX - ThumbWidth / 2, middle - ThumbHeight / 2, ThumbWidth, ThumbHeight);

        context.FillRectangle(Tokens.BgBrush, thumb);
        context.DrawRectangle(null, new Pen(Tokens.Accent700Brush, 1), thumb.Deflate(0.5));
    }
}
