using Avalonia;

namespace SnapShotKit.Editor;

/// <summary>
/// Where a picture's own pixels stand on the canvas, once it has been cropped, cut and flipped, and
/// the other way round.
///
/// Neither a crop nor a cut is a pixel edit. The picture keeps every pixel it arrived with, and the
/// crop and the cuts only say which of them are drawn: the crop keeps a rectangle of them, and each
/// cut leaves a band of rows or columns out and closes the gap. Both are kept in the picture's own
/// pixels, before any flip, so they go wherever the picture goes and survive it being stretched,
/// and flipping turns round the same part rather than showing a different one.
///
/// That leaves a picture with three kinds of coordinate. Its own pixels, which is what the crop and
/// the cuts are written in and which never renumber. Closed pixels, which are its own with the cuts
/// taken out, computed by a <see cref="CutLayout"/>. And the canvas, where the part the crop keeps
/// is stretched over the layer's X, Y, Width and Height and mirrored as the layer says. Everything
/// else in the document is on the canvas, and this is the one place that crosses between them.
///
/// Built from a picture as it stands, and not kept: a layout describes the picture at the moment it
/// was made, which is exactly what is wanted to say where something was before a change.
/// </summary>
public sealed class PictureLayout
{
    readonly ImageAnnotation picture;
    readonly PixelSize size;
    readonly CutLayout cuts;

    /// <summary>The part the crop keeps, in the picture's own pixels.</summary>
    readonly Rect kept;

    /// <summary>The same, with the cuts closed.</summary>
    readonly Rect closed;

    public PictureLayout(ImageAnnotation picture, PixelSize size)
    {
        // A copy, so that a layout taken before a change goes on describing the picture as it was.
        this.picture = (ImageAnnotation)picture.Copy();
        this.size = size;

        cuts = new CutLayout(picture.Cuts ?? []);
        kept = picture.Crop is { } crop ? new Rect(crop.X, crop.Y, crop.Width, crop.Height) : new Rect(0, 0, size.Width, size.Height);
        closed = cuts.ToLaid(kept);
    }

    /// <summary>Where the picture stands on the canvas, which is its layer's own rectangle.</summary>
    public Rect Bounds => new(picture.X, picture.Y, picture.Width, picture.Height);

    /// <summary>Canvas pixels to each closed pixel across, and down.</summary>
    double Across => closed.Width > 0 ? picture.Width / closed.Width : 1;

    double Down => closed.Height > 0 ? picture.Height / closed.Height : 1;

    /// <summary>The size the part on show is at one canvas pixel to a picture pixel: what "actual size" means.</summary>
    public Size Own => closed.Size;

    /// <summary>The picture pixel under a point on the canvas. Inside a cut there is none, and it is the first one after it.</summary>
    public Point ToSource(Point canvas)
    {
        var across = (canvas.X - picture.X) / Across;
        var down = (canvas.Y - picture.Y) / Down;

        if (picture.FlipHorizontal)
        {
            across = closed.Width - across;
        }

        if (picture.FlipVertical)
        {
            down = closed.Height - down;
        }

        return cuts.ToCapture(new Point(closed.X + across, closed.Y + down));
    }

    /// <summary>Where a picture pixel stands on the canvas. One that has been cut lands on the join.</summary>
    public Point ToCanvas(Point source)
    {
        var at = cuts.ToLaid(source);
        var across = at.X - closed.X;
        var down = at.Y - closed.Y;

        if (picture.FlipHorizontal)
        {
            across = closed.Width - across;
        }

        if (picture.FlipVertical)
        {
            down = closed.Height - down;
        }

        return new Point(picture.X + across * Across, picture.Y + down * Down);
    }

    /// <summary>A rectangle of the canvas as the picture pixels under it, whichever way the picture faces.</summary>
    Rect ToSource(Rect canvas) => new Rect(ToSource(canvas.TopLeft), ToSource(canvas.BottomRight)).Normalize();

    /// <summary>Where the whole picture would stand on the canvas with nothing cropped off it, in canvas pixels. The cuts stay closed.</summary>
    public Rect Whole => new Rect(ToCanvas(default), ToCanvas(new Point(size.Width, size.Height))).Normalize();

    /// <summary>
    /// The picture in pieces, each a stretch of its own pixels between the cuts and where on the
    /// canvas it is drawn, before any flip. Drawn inside a mirror about the middle of
    /// <see cref="Bounds"/>, they come out flipped where the layer says.
    /// </summary>
    public IEnumerable<(Rect Source, Rect Canvas)> Pieces()
    {
        foreach (var (piece, shift) in cuts.Pieces(kept))
        {
            var x = piece.X - shift.X - closed.X;
            var y = piece.Y - shift.Y - closed.Y;

            yield return (piece, new Rect(
                picture.X + x * Across,
                picture.Y + y * Down,
                piece.Width * Across,
                piece.Height * Down));
        }
    }

    /// <summary>
    /// Shows only the part of the picture that falls in <paramref name="shown"/>, which is in canvas
    /// pixels and held inside <see cref="Whole"/>. What is left does not move, and the crop is
    /// dropped when nothing is left out.
    /// </summary>
    public static void Crop(ImageAnnotation picture, PixelSize size, Rect shown)
    {
        var layout = new PictureLayout(picture, size);
        shown = shown.Intersect(layout.Whole);

        if (shown.Width <= 0 || shown.Height <= 0)
        {
            return;
        }

        var source = layout.ToSource(shown);

        picture.X = shown.X;
        picture.Y = shown.Y;
        picture.Width = shown.Width;
        picture.Height = shown.Height;

        // All of it, near enough, is no crop at all. Kept as one it would say the picture had been
        // cropped, and the document would carry a crop that crops nothing.
        const double near = 0.01;

        picture.Crop = source.X < near && source.Y < near
                       && Math.Abs(source.Right - size.Width) < near && Math.Abs(source.Bottom - size.Height) < near
            ? null
            : new CropArea { X = source.X, Y = source.Y, Width = source.Width, Height = source.Height };
    }

    /// <summary>
    /// Takes a band out of the picture, from <paramref name="from"/> to <paramref name="to"/> along
    /// the axis in canvas pixels, and closes it up. The picture's top-left corner stays where it is
    /// and the rest comes up or across to meet it, at the same scale.
    /// </summary>
    /// <returns>The picture as it was, for moving what stands on it; null when nothing was taken.</returns>
    public static PictureLayout? Cut(ImageAnnotation picture, PixelSize size, CutAxis axis, double from, double to)
    {
        var before = new PictureLayout(picture, size);
        var bounds = before.Bounds;

        var (start, end) = axis == CutAxis.Rows
            ? (Math.Clamp(Math.Min(from, to), bounds.Top, bounds.Bottom), Math.Clamp(Math.Max(from, to), bounds.Top, bounds.Bottom))
            : (Math.Clamp(Math.Min(from, to), bounds.Left, bounds.Right), Math.Clamp(Math.Max(from, to), bounds.Left, bounds.Right));

        if (end - start <= 0)
        {
            return null;
        }

        // Both ends as picture pixels, which a flip may have put the other way round. Measured
        // across the middle of the picture, since only the one coordinate is wanted.
        var middle = bounds.Center;

        var (a, b) = axis == CutAxis.Rows
            ? (before.ToSource(new Point(middle.X, start)).Y, before.ToSource(new Point(middle.X, end)).Y)
            : (before.ToSource(new Point(start, middle.Y)).X, before.ToSource(new Point(end, middle.Y)).X);

        var band = new CutBand { Axis = axis, At = Math.Min(a, b), Extent = Math.Abs(b - a) };

        if (band.Extent <= 0)
        {
            return null;
        }

        picture.Cuts = [.. picture.Cuts ?? [], band];

        // Smaller by what was closed up, and nothing else: the same picture pixels to a canvas pixel.
        var after = new PictureLayout(picture, size).closed;

        if (axis == CutAxis.Rows)
        {
            picture.Height = after.Height * before.Down;
        }
        else
        {
            picture.Width = after.Width * before.Across;
        }

        return before;
    }

    /// <summary>
    /// Puts every cut back and lets the picture grow to take them in again, from its top-left corner.
    /// </summary>
    /// <returns>The picture as it was, for moving what stands on it; null when it had no cuts.</returns>
    public static PictureLayout? Uncut(ImageAnnotation picture, PixelSize size)
    {
        if (picture.Cuts is not { Count: > 0 })
        {
            return null;
        }

        var before = new PictureLayout(picture, size);

        picture.Cuts = null;

        var after = new PictureLayout(picture, size).closed;

        picture.Width = after.Width * before.Across;
        picture.Height = after.Height * before.Down;

        return before;
    }

    /// <summary>
    /// Moves a point that stood on the picture as <paramref name="before"/> had it to wherever the
    /// same picture pixel stands now. Anything that was not on the picture stays where it is.
    /// </summary>
    /// <param name="probe">
    /// The point that decides whether it was on the picture, when that is not the point itself. A
    /// box is on the picture if its middle is, and then all of it goes with the picture, including a
    /// corner that hangs past the edge; otherwise one box would come apart at the picture's border.
    /// </param>
    public Point Follow(PictureLayout before, Point point, Point probe) =>
        before.Bounds.Contains(probe) ? ToCanvas(before.ToSource(point)) : point;

    /// <summary>
    /// Takes whatever stands on a picture along with a cut, or with a cut put back.
    ///
    /// A cut is made to a picture, and the part of it past the band comes up to meet the rest; an
    /// arrow drawn on that part pointing at something has to come up with it, or it points at
    /// whatever has moved in underneath. Nothing else goes: not an arrow beside the picture, and
    /// not another picture, which was not cut and has not changed. Something across the band keeps
    /// the end before it and loses what was in it, so a box over the band comes out shorter.
    ///
    /// Moving a picture is different, and takes nothing along. That is a change of mind about
    /// where the picture goes, while a cut is a change to what is in it.
    /// </summary>
    public void Carry(PictureLayout before, IEnumerable<Annotation> layers) =>
        MoveAll(layers, (point, probe) => Follow(before, point, probe));

    /// <summary>
    /// Moves every point of everything but the pictures through <paramref name="move"/>, which is
    /// handed each point and the point that says whether it is on something: itself for the end of
    /// an arrow, and the middle for a box or a drawing, which go as one.
    /// </summary>
    public static void MoveAll(IEnumerable<Annotation> layers, Func<Point, Point, Point> move)
    {
        Point Own(double x, double y) => move(new Point(x, y), new Point(x, y));

        foreach (var layer in layers)
        {
            switch (layer)
            {
                case ImageAnnotation:
                    break;

                case RectAnnotation rect:
                    var middle = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                    var from = move(new Point(rect.X, rect.Y), middle);
                    var to = move(new Point(rect.X + rect.Width, rect.Y + rect.Height), middle);

                    (rect.X, rect.Y) = (Math.Min(from.X, to.X), Math.Min(from.Y, to.Y));
                    (rect.Width, rect.Height) = (Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
                    break;

                case ArrowAnnotation arrow:
                    (arrow.X1, arrow.Y1) = Own(arrow.X1, arrow.Y1);
                    (arrow.X2, arrow.Y2) = Own(arrow.X2, arrow.Y2);
                    break;

                case TextAnnotation text:
                    (text.X, text.Y) = Own(text.X, text.Y);
                    (text.TailX, text.TailY) = Own(text.TailX, text.TailY);
                    break;

                case StepAnnotation step:
                    (step.X, step.Y) = Own(step.X, step.Y);
                    break;

                // A line goes as one, by its middle, so one drawn across the picture's edge is
                // not pulled apart where the edge runs through it.
                case PenAnnotation pen:
                    var drawn = pen.Bounds;
                    var centre = new Point(drawn.X + drawn.Width / 2, drawn.Y + drawn.Height / 2);

                    for (var index = 0; index + 1 < pen.Points.Count; index += 2)
                    {
                        (pen.Points[index], pen.Points[index + 1]) = move(new Point(pen.Points[index], pen.Points[index + 1]), centre);
                    }

                    break;
            }
        }
    }
}
