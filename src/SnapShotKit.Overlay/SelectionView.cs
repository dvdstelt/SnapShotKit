using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SnapShotKit.Ui;

namespace SnapShotKit.Overlay;

/// <summary>Which part of the preview box the pointer is acting on.</summary>
public enum Grip
{
    None,
    Inside,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left
}

/// <summary>
/// The selection surface, in three phases.
///
/// Idle    a crosshair spanning the whole screen, waiting for a drag.
/// Drawing dragging out the first box, with live dimensions.
/// Preview the box is provisional: it can be moved by its middle and resized by eight grips, and
///         nothing is captured until the toolbar says so.
///
/// All geometry is kept in image pixels, because that rectangle is the answer this process exists to
/// produce. Only hit testing works in control units, so that grips stay a constant size on screen.
/// </summary>
public sealed class SelectionView : Control
{
    // The dim is the accent's deepest step rather than black: the overlay is part of the
    // application, and a neutral black veil would read as the desktop dimming itself.
    static readonly IBrush Dim = new SolidColorBrush(Tokens.Accent900, 0.55);

    static readonly IBrush GripFill = Tokens.BgBrush;
    static readonly IPen GripBorder = new Pen(Tokens.Accent700Brush, 1);

    // The crosshair is a single hairline in the accent's light step, which stays visible against
    // both the dimmed ground and the undimmed region without competing with either.
    static readonly IPen Crosshair = new Pen(Tokens.Accent200Brush, 1);

    static readonly IPen RegionEdge = new Pen(Tokens.Accent200Brush, 1);

    static readonly IPen ReticleShadow = new Pen(new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), 3);
    static readonly IPen Reticle = new Pen(new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)), 1);

    const double GripSize = 8;
    const double GripHitPadding = 5;
    const double MagnifierSize = 132;
    const double MagnifierZoom = 8;

    readonly WriteableBitmap bitmap;

    Point cursor;
    bool hasCursor;

    Grip dragging = Grip.None;
    Point dragOrigin;
    PixelRect dragStart;
    Point pointer;

    /// <summary>
    /// Keyboard offset applied on top of the pointer for the current drag.
    ///
    /// A mouse cannot reliably land on an exact pixel, so the arrow keys shift the drag point
    /// without moving the hand. It is an offset rather than an absolute position so that moving the
    /// mouse afterwards keeps the correction rather than throwing it away.
    /// </summary>
    Vector nudge;

    /// <summary>
    /// Pointer minus the grip's anchor at the moment of the press. Without it, grabbing a grip a few
    /// pixels off centre yanks the edge to the pointer, and the box jumps before it moves.
    /// </summary>
    Vector grabOffset;

    public SelectionView(WriteableBitmap bitmap)
    {
        this.bitmap = bitmap;
        Focusable = true;
    }

    /// <summary>The provisional capture region, in image pixels.</summary>
    public PixelRect? Selection { get; private set; }

    /// <summary>True while a grip or the box itself is being dragged.</summary>
    public bool IsAdjusting => dragging != Grip.None;

    /// <summary>On by default. It is the whole reason pixel-accurate selection is workable.</summary>
    public bool ShowMagnifier { get; set; } = true;

    public event Action? SelectionChanged;

    public PixelSize ImageSize => bitmap.PixelSize;

    public void SetSelection(PixelRect region)
    {
        Selection = Clamp(region);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void Clear()
    {
        Selection = null;
        dragging = Grip.None;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void ToggleMagnifier()
    {
        ShowMagnifier = !ShowMagnifier;
        InvalidateVisual();
    }

    /// <summary>Where the preview sits on screen, so the toolbar can place itself against it.</summary>
    public Rect SelectionInControl() => Selection is { } selection ? ToControl(selection) : default;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        var grip = HitTest(point);

        dragOrigin = ToImage(point);

        if (grip == Grip.None)
        {
            // Pressing outside an existing preview starts a new one rather than nudging the old.
            dragging = Grip.BottomRight;
            dragStart = new PixelRect((int)dragOrigin.X, (int)dragOrigin.Y, 0, 0);
            grabOffset = default;
            Selection = dragStart;
        }
        else
        {
            dragging = grip;
            dragStart = Selection!.Value;
            grabOffset = dragOrigin - AnchorOf(dragStart, grip);
        }

        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        pointer = ToImage(point);
        hasCursor = true;

        if (dragging == Grip.None)
        {
            cursor = pointer;
            Cursor = CursorFor(HitTest(point));
            InvalidateVisual();
            return;
        }

        ApplyDrag();
    }

    /// <summary>Nudges the drag point by whole pixels. Returns false when nothing is being dragged.</summary>
    public bool Nudge(int x, int y)
    {
        if (dragging == Grip.None)
        {
            return false;
        }

        nudge += new Vector(x, y);
        ApplyDrag();
        return true;
    }

    void ApplyDrag()
    {
        cursor = pointer + nudge;
        var delta = cursor - dragOrigin;

        Selection = dragging == Grip.Inside
            ? Clamp(new PixelRect(
                dragStart.X + (int)delta.X,
                dragStart.Y + (int)delta.Y,
                dragStart.Width,
                dragStart.Height))
            : Clamp(Resize(dragStart, dragging, cursor - grabOffset));

        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);

        // A click without a drag is a misclick, not a request for a one pixel capture.
        if (Selection is { } selection && (selection.Width < 8 || selection.Height < 8))
        {
            Clear();
        }

        dragging = Grip.None;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>The point a grip actually controls, which is what the magnifier should follow.</summary>
    static Point AnchorOf(PixelRect rect, Grip grip) => grip switch
    {
        Grip.TopLeft => new Point(rect.X, rect.Y),
        Grip.Top => new Point(rect.Center.X, rect.Y),
        Grip.TopRight => new Point(rect.Right, rect.Y),
        Grip.Right => new Point(rect.Right, rect.Center.Y),
        Grip.BottomRight => new Point(rect.Right, rect.Bottom),
        Grip.Bottom => new Point(rect.Center.X, rect.Bottom),
        Grip.BottomLeft => new Point(rect.X, rect.Bottom),
        Grip.Left => new Point(rect.X, rect.Center.Y),
        _ => new Point(rect.X, rect.Y)
    };

    /// <summary>
    /// What the magnifier should centre on. While a grip is being dragged that is the edge or corner
    /// under control, not the pointer: the pointer is offset from it, and the whole reason to look
    /// through a magnifier is to place that edge exactly.
    /// </summary>
    Point MagnifierFocus()
    {
        if (dragging is Grip.None or Grip.Inside || Selection is not { } selection)
        {
            return cursor;
        }

        // Side grips control one axis only, so the other follows the pointer and the magnifier
        // shows the edge at the height the hand is actually at.
        return dragging switch
        {
            Grip.Left => new Point(selection.X, cursor.Y),
            Grip.Right => new Point(selection.Right, cursor.Y),
            Grip.Top => new Point(cursor.X, selection.Y),
            Grip.Bottom => new Point(cursor.X, selection.Bottom),
            _ => AnchorOf(selection, dragging)
        };
    }

    static PixelRect Resize(PixelRect start, Grip grip, Point cursor)
    {
        var left = start.X;
        var top = start.Y;
        var right = start.Right;
        var bottom = start.Bottom;

        var x = (int)Math.Round(cursor.X);
        var y = (int)Math.Round(cursor.Y);

        // Side grips move one edge, corner grips move two. Dragging an edge past its opposite is
        // allowed and simply flips the rectangle, which is what every drawing tool does.
        if (grip is Grip.TopLeft or Grip.Left or Grip.BottomLeft) left = x;
        if (grip is Grip.TopRight or Grip.Right or Grip.BottomRight) right = x;
        if (grip is Grip.TopLeft or Grip.Top or Grip.TopRight) top = y;
        if (grip is Grip.BottomLeft or Grip.Bottom or Grip.BottomRight) bottom = y;

        return new PixelRect(
            Math.Min(left, right),
            Math.Min(top, bottom),
            Math.Abs(right - left),
            Math.Abs(bottom - top));
    }

    PixelRect Clamp(PixelRect region)
    {
        var x = Math.Clamp(region.X, 0, Math.Max(bitmap.PixelSize.Width - 1, 0));
        var y = Math.Clamp(region.Y, 0, Math.Max(bitmap.PixelSize.Height - 1, 0));

        return new PixelRect(x, y,
            Math.Clamp(region.Width, 0, bitmap.PixelSize.Width - x),
            Math.Clamp(region.Height, 0, bitmap.PixelSize.Height - y));
    }

    Grip HitTest(Point control)
    {
        if (Selection is not { } selection || selection.Width == 0)
        {
            return Grip.None;
        }

        var rect = ToControl(selection);
        var reach = GripSize / 2 + GripHitPadding;

        foreach (var (grip, centre) in Grips(rect))
        {
            if (Math.Abs(control.X - centre.X) <= reach && Math.Abs(control.Y - centre.Y) <= reach)
            {
                return grip;
            }
        }

        return rect.Contains(control) ? Grip.Inside : Grip.None;
    }

    static IEnumerable<(Grip Grip, Point Centre)> Grips(Rect rect)
    {
        yield return (Grip.TopLeft, rect.TopLeft);
        yield return (Grip.Top, new Point(rect.Center.X, rect.Top));
        yield return (Grip.TopRight, rect.TopRight);
        yield return (Grip.Right, new Point(rect.Right, rect.Center.Y));
        yield return (Grip.BottomRight, rect.BottomRight);
        yield return (Grip.Bottom, new Point(rect.Center.X, rect.Bottom));
        yield return (Grip.BottomLeft, rect.BottomLeft);
        yield return (Grip.Left, new Point(rect.Left, rect.Center.Y));
    }

    static Cursor CursorFor(Grip grip) => new(grip switch
    {
        Grip.Inside => StandardCursorType.SizeAll,
        Grip.TopLeft => StandardCursorType.TopLeftCorner,
        Grip.Top => StandardCursorType.TopSide,
        Grip.TopRight => StandardCursorType.TopRightCorner,
        Grip.Right => StandardCursorType.RightSide,
        Grip.BottomRight => StandardCursorType.BottomRightCorner,
        Grip.Bottom => StandardCursorType.BottomSide,
        Grip.BottomLeft => StandardCursorType.BottomLeftCorner,
        Grip.Left => StandardCursorType.LeftSide,
        _ => StandardCursorType.Cross
    });

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawImage(bitmap, bounds);

        if (Selection is { } selection && selection.Width > 0 && selection.Height > 0)
        {
            var rect = ToControl(selection);

            // Everything outside the region is dimmed, so the region itself shows the frozen screen
            // at full brightness. That is the whole promise of the preview: what you see inside the
            // box is exactly what you will get.
            context.FillRectangle(Dim, new Rect(0, 0, bounds.Width, rect.Top));
            context.FillRectangle(Dim, new Rect(0, rect.Bottom, bounds.Width, bounds.Height - rect.Bottom));
            context.FillRectangle(Dim, new Rect(0, rect.Top, rect.Left, rect.Height));
            context.FillRectangle(Dim, new Rect(rect.Right, rect.Top, bounds.Width - rect.Right, rect.Height));

            context.DrawRectangle(RegionEdge, rect);

            // The crosshair stays, running to the edges of the screen, so the region's edges can be
            // lined up against things outside it.
            DrawCrosshair(context, bounds, ToControl(MagnifierFocus()));

            DrawMeasurements(context, selection, rect);

            if (dragging == Grip.None)
            {
                foreach (var (_, centre) in Grips(rect))
                {
                    var grip = new Rect(centre.X - GripSize / 2, centre.Y - GripSize / 2, GripSize, GripSize);
                    context.FillRectangle(GripFill, grip);
                    context.DrawRectangle(GripBorder, grip);
                }
            }
        }
        else
        {
            context.FillRectangle(Dim, bounds);

            if (hasCursor)
            {
                var point = ToControl(cursor);
                DrawCrosshair(context, bounds, point);
                DrawPointerReadout(context, bounds, point);
            }
        }

        // While adjusting, and while there is nothing yet: both are moments of aiming. It is only in
        // the way once a preview exists and is being looked at rather than placed.
        if (hasCursor && ShowMagnifier && (dragging != Grip.None || Selection is null))
        {
            DrawMagnifier(context, bounds);
        }
    }

    static void DrawCrosshair(DrawingContext context, Rect bounds, Point at)
    {
        context.DrawLine(Crosshair, new Point(0, at.Y), new Point(bounds.Width, at.Y));
        context.DrawLine(Crosshair, new Point(at.X, 0), new Point(at.X, bounds.Height));
    }

    /// <summary>The pointer's position, in a small plate beside the crosshair's intersection.</summary>
    void DrawPointerReadout(DrawingContext context, Rect bounds, Point at)
    {
        var text = new FormattedText(
            $"{(int)cursor.X} · {(int)cursor.Y}",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(Tokens.Fonts.Body),
            12,
            Tokens.Neutral800Brush);

        // Beside the intersection, and flipped to the other side when there is no room, so the
        // readout never runs off the screen at the far edges.
        var x = at.X + 10 + text.Width + 16 > bounds.Width ? at.X - 10 - text.Width - 16 : at.X + 10;
        var y = at.Y + 10 + text.Height + 6 > bounds.Height ? at.Y - 10 - text.Height - 6 : at.Y + 10;

        var plate = new Rect(x, y, text.Width + 16, text.Height + 6);

        context.FillRectangle(Tokens.BgBrush, plate);
        context.DrawRectangle(null, Tokens.DividerPen, plate);
        context.DrawText(text, new Point(plate.X + 8, plate.Y + 3));
    }

    /// <summary>
    /// The region's size, set large in the middle of it, with its origin underneath.
    ///
    /// Inside the region rather than beside it: the region is undimmed and the numbers are what the
    /// user is adjusting, so this is where the eye already is. Dropped when the region is too small
    /// to hold them, where they would spill over the edges they are describing.
    /// </summary>
    void DrawMeasurements(DrawingContext context, PixelRect selection, Rect rect)
    {
        var size = new FormattedText(
            $"{selection.Width} × {selection.Height}",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(Tokens.Fonts.Heading, weight: FontWeight.SemiBold),
            34,
            Tokens.Accent900Brush);

        var origin = new FormattedText(
            $"x {selection.X} · y {selection.Y}",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(Tokens.Fonts.Body),
            12.5,
            Tokens.Neutral700Brush);

        var total = size.Height + 4 + origin.Height;

        if (size.Width + 24 > rect.Width || total + 16 > rect.Height)
        {
            return;
        }

        var top = rect.Center.Y - total / 2;

        context.DrawText(size, new Point(rect.Center.X - size.Width / 2, top));
        context.DrawText(origin, new Point(rect.Center.X - origin.Width / 2, top + size.Height + 4));
    }

    void DrawMagnifier(DrawingContext context, Rect bounds)
    {
        var focus = MagnifierFocus();

        // Positioned by the pointer so it stays near the hand, but showing the focus point, which
        // during a resize is the edge rather than the cursor.
        var point = ToControl(cursor);

        var x = point.X + 24 + MagnifierSize > bounds.Width ? point.X - 24 - MagnifierSize : point.X + 24;
        var y = point.Y + 24 + MagnifierSize > bounds.Height ? point.Y - 24 - MagnifierSize : point.Y + 24;
        var target = new Rect(x, y, MagnifierSize, MagnifierSize);

        var span = MagnifierSize / MagnifierZoom;
        var source = new Rect(focus.X - span / 2, focus.Y - span / 2, span, span);

        // Nearest neighbour: the point of a magnifier is to see the pixel grid, not a smooth blur.
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            context.DrawImage(bitmap, source, target);
        }

        context.DrawRectangle(GripBorder, target);
        DrawReticle(context, target);
    }

    /// <summary>
    /// A crosshair with a gap over the centre, so the pixel being aimed at stays visible rather than
    /// being covered by the very lines pointing at it.
    /// </summary>
    static void DrawReticle(DrawingContext context, Rect target)
    {
        var centre = target.Center;
        const double gap = MagnifierZoom / 2 + 2;

        Span<(Point From, Point To)> arms =
        [
            (new Point(target.Left, centre.Y), new Point(centre.X - gap, centre.Y)),
            (new Point(centre.X + gap, centre.Y), new Point(target.Right, centre.Y)),
            (new Point(centre.X, target.Top), new Point(centre.X, centre.Y - gap)),
            (new Point(centre.X, centre.Y + gap), new Point(centre.X, target.Bottom))
        ];

        foreach (var (from, to) in arms)
        {
            context.DrawLine(ReticleShadow, from, to);
        }

        foreach (var (from, to) in arms)
        {
            context.DrawLine(Reticle, from, to);
        }
    }

    Point ToImage(Point control) => new(
        control.X * bitmap.PixelSize.Width / Math.Max(Bounds.Width, 1),
        control.Y * bitmap.PixelSize.Height / Math.Max(Bounds.Height, 1));

    Point ToControl(Point image) => new(
        image.X * Bounds.Width / bitmap.PixelSize.Width,
        image.Y * Bounds.Height / bitmap.PixelSize.Height);

    Rect ToControl(PixelRect image) => new(
        ToControl(new Point(image.X, image.Y)),
        ToControl(new Point(image.Right, image.Bottom)));
}
