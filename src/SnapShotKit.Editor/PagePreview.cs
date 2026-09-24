using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>
/// The sheet of paper, with the picture on it to be put where it is wanted.
///
/// Dragging the picture moves it and dragging a corner sizes it, from the corner opposite, which is
/// how a picture is handled on the editing canvas and so is how it is handled here. Everything
/// goes through <see cref="PrintLayout"/>, the same object the fields beside this type into, so the
/// preview and the numbers cannot come to disagree about where the picture is.
///
/// A control of a fixed size that draws the page as large as fits inside it. A preview that took
/// its size from the page would make the dialog jump every time the paper was turned round.
/// </summary>
public sealed class PagePreview : Control
{
    /// <summary>How near the middle of the page, in millimetres, a dragged picture is pulled onto it.</summary>
    const double SnapWithin = 2.5;

    const double HandleSize = 7;

    /// <summary>How far from a corner a press still takes hold of it, which is more than the handle drawn there.</summary>
    const double HandleReach = 9;

    static readonly IPen PageEdge = new Pen(Tokens.Neutral400Brush, 1);
    static readonly IPen MarginGuide = new Pen(Tokens.Neutral300Brush, 1, new DashStyle([3, 3], 0));
    static readonly IPen CentreGuide = new Pen(Tokens.AccentBrush, 1);
    static readonly IPen Outline = new Pen(Tokens.Accent700Brush, 1);
    static readonly IPen HandleBorder = new Pen(Tokens.Accent700Brush, 1);

    readonly PrintLayout layout;
    readonly Bitmap picture;

    enum Hold
    {
        Nothing,
        Picture,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    Hold held;

    /// <summary>Where the press landed, in page millimetres, and where the picture stood at the time.</summary>
    Point pressedAt;

    Rect pictureWhenPressed;

    /// <summary>Which of the page's centre lines the picture is sitting on, shown only while it is being dragged onto them.</summary>
    bool onCentreAcross;

    bool onCentreDown;

    public PagePreview(PrintLayout layout, Bitmap picture, double size)
    {
        this.layout = layout;
        this.picture = picture;

        Width = size;
        Height = size;
        ClipToBounds = true;
    }

    /// <summary>Raised whenever a drag has changed the layout, so whatever shows it in numbers can catch up.</summary>
    public event Action? Changed;

    // ---- Page millimetres to control pixels and back -------------------------------------------

    /// <summary>Control pixels to the millimetre, with a little room left round the sheet for its shadow and for a picture hanging over the edge.</summary>
    double Scale => Math.Min((Width - 2 * Tokens.Space.S6) / layout.PageWidth, (Height - 2 * Tokens.Space.S6) / layout.PageHeight);

    Point PageOrigin => new((Width - layout.PageWidth * Scale) / 2, (Height - layout.PageHeight * Scale) / 2);

    Rect ToView(Rect page) => new(
        PageOrigin.X + page.X * Scale,
        PageOrigin.Y + page.Y * Scale,
        page.Width * Scale,
        page.Height * Scale);

    Point ToPage(Point view) => new((view.X - PageOrigin.X) / Scale, (view.Y - PageOrigin.Y) / Scale);

    // ---- Drawing -------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Tokens.Neutral100Brush, new Rect(Bounds.Size));

        var page = ToView(new Rect(0, 0, layout.PageWidth, layout.PageHeight));
        var shown = ToView(layout.Picture);

        context.FillRectangle(Brushes.White, page);

        // Only what lands on the sheet is printed, so only that is drawn at full strength. What
        // hangs over is left faint rather than hidden: a picture that vanished at the edge of the
        // page would be a picture nobody could find the corner of to pull back.
        using (context.PushOpacity(0.25))
        {
            context.DrawImage(picture, shown);
        }

        using (context.PushClip(page))
        {
            context.DrawImage(picture, shown);
            context.DrawRectangle(null, MarginGuide, ToView(layout.Printable));

            if (held == Hold.Picture && onCentreAcross)
            {
                context.DrawLine(CentreGuide, new Point(page.Center.X, page.Top), new Point(page.Center.X, page.Bottom));
            }

            if (held == Hold.Picture && onCentreDown)
            {
                context.DrawLine(CentreGuide, new Point(page.Left, page.Center.Y), new Point(page.Right, page.Center.Y));
            }
        }

        context.DrawRectangle(null, PageEdge, page);
        context.DrawRectangle(null, Outline, shown);

        foreach (var corner in new[] { shown.TopLeft, shown.TopRight, shown.BottomLeft, shown.BottomRight })
        {
            var handle = new Rect(corner.X - HandleSize / 2, corner.Y - HandleSize / 2, HandleSize, HandleSize);
            context.DrawRectangle(Tokens.BgBrush, HandleBorder, handle);
        }
    }

    // ---- Dragging ------------------------------------------------------------------------------

    Hold HoldAt(Point view)
    {
        var shown = ToView(layout.Picture);

        bool Near(Point corner) => Math.Abs(view.X - corner.X) <= HandleReach && Math.Abs(view.Y - corner.Y) <= HandleReach;

        if (Near(shown.TopLeft))
        {
            return Hold.TopLeft;
        }

        if (Near(shown.TopRight))
        {
            return Hold.TopRight;
        }

        if (Near(shown.BottomLeft))
        {
            return Hold.BottomLeft;
        }

        if (Near(shown.BottomRight))
        {
            return Hold.BottomRight;
        }

        return shown.Contains(view) ? Hold.Picture : Hold.Nothing;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        held = HoldAt(e.GetPosition(this));

        if (held == Hold.Nothing)
        {
            return;
        }

        pressedAt = ToPage(e.GetPosition(this));
        pictureWhenPressed = layout.Picture;

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var view = e.GetPosition(this);

        if (held == Hold.Nothing)
        {
            Cursor = HoldAt(view) switch
            {
                Hold.TopLeft or Hold.BottomRight => new Cursor(StandardCursorType.TopLeftCorner),
                Hold.TopRight or Hold.BottomLeft => new Cursor(StandardCursorType.TopRightCorner),
                Hold.Picture => new Cursor(StandardCursorType.SizeAll),
                _ => Cursor.Default
            };

            return;
        }

        var at = ToPage(view);

        if (held == Hold.Picture)
        {
            Move(at - pressedAt, snap: !e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        }
        else
        {
            Size(at);
        }

        Changed?.Invoke();
        InvalidateVisual();
    }

    /// <summary>
    /// Moves the picture by how far the pointer has gone since the press, and onto the middle of
    /// the page when it comes close.
    ///
    /// The middle is the one place on a page that is hard to hit by eye and is wanted more than
    /// any other, so it pulls. Alt lets go of it, for the picture wanted a millimetre off centre.
    /// </summary>
    void Move(Vector delta, bool snap)
    {
        var x = pictureWhenPressed.X + delta.X;
        var y = pictureWhenPressed.Y + delta.Y;

        var across = (layout.PageWidth - pictureWhenPressed.Width) / 2;
        var down = (layout.PageHeight - pictureWhenPressed.Height) / 2;

        onCentreAcross = snap && Math.Abs(x - across) <= SnapWithin;
        onCentreDown = snap && Math.Abs(y - down) <= SnapWithin;

        layout.MoveTo(onCentreAcross ? across : x, onCentreDown ? down : y);
    }

    /// <summary>
    /// Sizes the picture from the corner opposite the one held, by whichever way the pointer has
    /// gone further, so the corner stays under the pointer rather than one axis lagging behind it.
    /// </summary>
    void Size(Point at)
    {
        var leftwards = held is Hold.TopLeft or Hold.BottomLeft;
        var upwards = held is Hold.TopLeft or Hold.TopRight;

        var anchor = new Point(
            leftwards ? pictureWhenPressed.Right : pictureWhenPressed.Left,
            upwards ? pictureWhenPressed.Bottom : pictureWhenPressed.Top);

        var across = leftwards ? anchor.X - at.X : at.X - anchor.X;
        var down = upwards ? anchor.Y - at.Y : at.Y - anchor.Y;

        var factor = Math.Max(across / pictureWhenPressed.Width, down / pictureWhenPressed.Height);

        layout.ResizeFrom(anchor, pictureWhenPressed.Width * Math.Max(factor, 0), leftwards, upwards);
    }

    // Released and capture lost both end the drag the same way, since letting go of the capture is
    // itself reported as capture lost and there is no saying which arrives first.

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        EndDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    void EndDrag()
    {
        if (held == Hold.Nothing)
        {
            return;
        }

        held = Hold.Nothing;
        onCentreAcross = false;
        onCentreDown = false;

        InvalidateVisual();
    }
}
