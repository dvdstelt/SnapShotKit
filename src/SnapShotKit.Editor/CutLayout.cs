using Avalonia;

namespace SnapShotKit.Editor;

/// <summary>
/// Where the picture ends up once the bands cut out of it are closed up.
///
/// A cut is not a pixel edit. `original.png` is never touched, so a cut is geometry like a crop is:
/// the capture is drawn in pieces with the cut bands skipped, and everything after a cut moves up
/// or left by what the cut took. Pulling the cut back out of the document puts the picture back
/// exactly as it was.
///
/// That leaves two coordinate systems. Capture coordinates are what the document is written in and
/// what every annotation is positioned against, and they never renumber, so a cut moves nothing
/// that was drawn. Laid coordinates are what ends up on screen and in the export, with the cuts
/// taken out. Everything drawn goes one way through here and everything pointed at comes back the
/// other.
/// </summary>
public sealed class CutLayout
{
    /// <summary>Nothing cut. The mapping is then the identity, which is what almost every document wants.</summary>
    public static readonly CutLayout None = new([]);

    readonly List<(double At, double Extent)> rows = [];
    readonly List<(double At, double Extent)> columns = [];

    /// <summary>
    /// Bands are sorted and merged as they are taken in.
    ///
    /// Two cuts that touch or overlap are one band, which keeps every walk through them a single
    /// pass and means the same picture is described the same way however the cuts were made.
    /// </summary>
    public CutLayout(IEnumerable<CutBand> cuts)
    {
        foreach (var cut in cuts.OrderBy(cut => cut.At))
        {
            if (cut.Extent <= 0)
            {
                continue;
            }

            var bands = Bands(cut.Axis);

            if (bands.Count > 0 && cut.At <= bands[^1].At + bands[^1].Extent)
            {
                var last = bands[^1];
                var end = Math.Max(last.At + last.Extent, cut.At + cut.Extent);
                bands[^1] = (last.At, end - last.At);
            }
            else
            {
                bands.Add((cut.At, cut.Extent));
            }
        }
    }

    public bool Any => rows.Count > 0 || columns.Count > 0;

    List<(double At, double Extent)> Bands(CutAxis axis) => axis == CutAxis.Rows ? rows : columns;

    /// <summary>Where a capture coordinate ends up once the cuts before it are closed. Inside a cut it lands on the join.</summary>
    public double ToLaid(double value, CutAxis axis)
    {
        var removed = 0.0;

        foreach (var (at, extent) in Bands(axis))
        {
            if (value <= at)
            {
                break;
            }

            removed += Math.Min(value - at, extent);
        }

        return value - removed;
    }

    /// <summary>The capture coordinate a laid one came from. On a join it is the first row or column after the cut.</summary>
    public double ToCapture(double value, CutAxis axis)
    {
        foreach (var (at, extent) in Bands(axis))
        {
            if (value < at)
            {
                break;
            }

            value += extent;
        }

        return value;
    }

    public Point ToLaid(Point capture) =>
        new(ToLaid(capture.X, CutAxis.Columns), ToLaid(capture.Y, CutAxis.Rows));

    public Point ToCapture(Point laid) =>
        new(ToCapture(laid.X, CutAxis.Columns), ToCapture(laid.Y, CutAxis.Rows));

    public Rect ToLaid(Rect capture) => Between(ToLaid(capture.TopLeft), ToLaid(capture.BottomRight));

    public Rect ToCapture(Rect laid) => Between(ToCapture(laid.TopLeft), ToCapture(laid.BottomRight));

    static Rect Between(Point from, Point to) =>
        new(from, new Size(Math.Max(to.X - from.X, 0), Math.Max(to.Y - from.Y, 0)));

    /// <summary>
    /// The stretch of capture a region of the picture is drawn from, in pieces, with how far each
    /// piece moves when the cuts are closed.
    ///
    /// Drawing a piece at a time is what makes a cut cost nothing anywhere else: each piece is the
    /// whole picture drawn shifted and clipped to its own band, so an annotation that happens to
    /// straddle a cut comes out as its two halves in the right places without knowing a cut exists.
    /// </summary>
    public IEnumerable<(Rect Piece, Vector Shift)> Pieces(Rect region)
    {
        foreach (var (top, bottom) in Spans(region.Y, region.Bottom, CutAxis.Rows))
        {
            foreach (var (left, right) in Spans(region.X, region.Right, CutAxis.Columns))
            {
                yield return (
                    new Rect(left, top, right - left, bottom - top),
                    new Vector(left - ToLaid(left, CutAxis.Columns), top - ToLaid(top, CutAxis.Rows)));
            }
        }
    }

    /// <summary>What is left of a stretch once the bands are taken out of it.</summary>
    IEnumerable<(double From, double To)> Spans(double from, double to, CutAxis axis)
    {
        var at = from;

        foreach (var (start, extent) in Bands(axis))
        {
            var end = start + extent;

            if (end <= at)
            {
                continue;
            }

            if (start >= to)
            {
                break;
            }

            if (start > at)
            {
                yield return (at, Math.Min(start, to));
            }

            at = Math.Max(at, end);
        }

        if (at < to)
        {
            yield return (at, to);
        }
    }

    /// <summary>
    /// How much capture it takes to come out a given size.
    ///
    /// For the times a size is typed rather than dragged: the number in the field is what the file
    /// will be, and what the canvas has to cover to produce it is this.
    /// </summary>
    public double Widen(double from, double laidExtent, CutAxis axis) =>
        ToCapture(ToLaid(from, axis) + laidExtent, axis) - from;
}
