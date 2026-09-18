using Avalonia;

namespace SnapShotKit.Editor;

/// <summary>
/// Takes the cuts along with a picture that is moved, stretched or turned round.
///
/// Nothing else is positioned against a picture, and an arrow stays where it was drawn when the
/// picture under it moves. A cut is the exception because of what it is for. It takes a stretch of
/// a picture out, often because of what was in it, and a band left behind while the picture moves
/// on puts that stretch straight back into the export and takes out some other part nobody chose.
/// An arrow left behind points at the wrong thing; a cut left behind shows the thing it was hiding.
///
/// Always worked out from the bands as they were when the change began, rather than from wherever
/// the last pointer movement left them, so a long drag cannot drift by its rounding and dragging a
/// picture back to where it started puts every band back exactly where it started too.
///
/// Only the bands that cross the picture. One that misses it was made in some other picture, or in
/// none, and has no reason to go anywhere. One that crosses two pictures follows whichever is
/// moved, which is a guess, but the other guess is the one that uncovers what was cut.
/// </summary>
public static class CutFollow
{
    /// <summary>The bands as they are now, to be handed back to <see cref="Apply"/> as the picture changes.</summary>
    public static List<CutBand> Remember(SnapshotDocument document) => [.. document.Cuts.Select(cut => cut.Copy())];

    /// <summary>
    /// Places every band for a picture that stood at <paramref name="then"/> and stands at
    /// <paramref name="now"/>. Returns whether any band ended up somewhere it was not.
    /// </summary>
    /// <param name="from">The bands as <see cref="Remember"/> gave them, before the picture changed.</param>
    /// <param name="mirrorColumns">Whether the picture has been turned round left to right since.</param>
    /// <param name="mirrorRows">Whether it has been turned upside down since.</param>
    public static bool Apply(SnapshotDocument document, IReadOnlyList<CutBand> from, Rect then, Rect now,
        bool mirrorColumns = false, bool mirrorRows = false)
    {
        var changed = false;

        for (var index = 0; index < from.Count && index < document.Cuts.Count; index++)
        {
            var was = from[index];
            var band = document.Cuts[index];

            var (at, extent) = was.Axis == CutAxis.Rows
                ? Place(was, then.Y, then.Height, now.Y, now.Height, mirrorRows)
                : Place(was, then.X, then.Width, now.X, now.Width, mirrorColumns);

            changed |= band.At != at || band.Extent != extent;

            band.At = at;
            band.Extent = extent;
        }

        return changed;
    }

    /// <summary>
    /// One band along one axis: kept the same way along the picture, and the same share of it.
    ///
    /// On whole pixels when the picture is stretched, as the picture itself is, and never stretched
    /// down to nothing, because a band of no extent is a cut that has quietly stopped being one.
    /// </summary>
    static (double At, double Extent) Place(CutBand was, double thenStart, double thenSize, double nowStart,
        double nowSize, bool mirror)
    {
        var crosses = was.At < thenStart + thenSize && was.At + was.Extent > thenStart;

        if (!crosses || thenSize <= 0)
        {
            return (was.At, was.Extent);
        }

        var offset = mirror ? thenStart + thenSize - (was.At + was.Extent) : was.At - thenStart;

        if (nowSize == thenSize)
        {
            return (nowStart + offset, was.Extent);
        }

        var ratio = nowSize / thenSize;

        return (nowStart + Math.Round(offset * ratio), Math.Max(Math.Round(was.Extent * ratio), 1));
    }
}
