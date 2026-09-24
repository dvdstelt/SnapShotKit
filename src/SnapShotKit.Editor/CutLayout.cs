using Avalonia;

namespace SnapShotKit.Editor;

/// <summary>
/// Where a picture's own pixels end up once the bands cut out of it are closed up.
///
/// A cut is not a pixel edit. The picture's pixels are never touched, so a cut is geometry like a
/// crop is: the picture is drawn in pieces with the cut bands skipped, and everything after a cut
/// moves up or left by what the cut took. Taking the cut back off puts the picture back exactly as
/// it was.
///
/// One of these belongs to each picture, in that picture's own pixels, and <see cref="PictureLayout"/>
/// is what puts the result on the canvas. The picture's own pixels never renumber, so a cut moves
/// nothing that was measured against them; closed pixels are what is drawn.
///
/// It once did the same for the whole canvas, when a cut ran across everything, and still does for
/// the one moment that happens: opening a document written before cuts belonged to pictures.
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

    /// <summary>Where an uncut coordinate ends up once the cuts before it are closed. Inside a cut it lands on the join.</summary>
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

    /// <summary>The uncut coordinate a closed one came from. On a join it is the first row or column after the cut.</summary>
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
    /// The stretch of the uncut picture a region of it is drawn from, in pieces, with how far each
    /// piece moves when the cuts are closed.
    ///
    /// Each piece is drawn on its own, shifted by what the cuts before it took, which is all that
    /// closing a cut up amounts to.
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
}
