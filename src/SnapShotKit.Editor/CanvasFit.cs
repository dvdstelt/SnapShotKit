using Avalonia;

namespace SnapShotKit.Editor;

/// <summary>
/// Where the canvas belongs, given the pictures on it and whatever was set by hand.
///
/// Worked out from the document alone rather than from how it got there, so the answer is the same
/// after a drag, a paste, a delete or an undo, and nothing has to remember which of those happened.
///
/// Left alone, the canvas is exactly what the pictures cover. Paste a large picture and it grows;
/// shrink that picture and it shrinks back, since room nothing stands in is not something anybody
/// asked for.
///
/// Set by hand, the canvas is what was set, and it grows past that only where a picture is pushed
/// past it. The pictures are measured against where each one stood when the canvas was set rather
/// than against the canvas alone. That is what lets a crop survive: a canvas pulled in over the
/// capture has the capture reaching past it on purpose, and nudging the capture a few pixels is not
/// a change of mind about the crop. A picture that was not there when the canvas was set has no
/// such standing, and the canvas grows to take in whatever of it falls outside.
/// </summary>
public static class CanvasFit
{
    /// <summary>The canvas the document should have now, in capture pixels, on whole pixels.</summary>
    /// <param name="capture">What to fit when there are no pictures at all: the capture where it was taken.</param>
    public static Rect For(SnapshotDocument document, Size capture)
    {
        var pictures = document.Layers.OfType<ImageAnnotation>().ToList();

        if (document.ManualCanvas is not { } manual)
        {
            var covered = pictures
                .Select(Bounds)
                .Aggregate((Rect?)null, (union, next) => union?.Union(next) ?? next)
                ?? new Rect(capture);

            return Whole(covered.Left, covered.Top, covered.Right, covered.Bottom);
        }

        double left = manual.X, top = manual.Y, right = manual.X + manual.Width, bottom = manual.Y + manual.Height;
        double pastLeft = 0, pastTop = 0, pastRight = 0, pastBottom = 0;

        foreach (var picture in pictures)
        {
            var now = Bounds(picture);

            // Where it stood when the canvas was set, or the canvas itself for one that was not
            // there yet, which leaves only the canvas's own edges to be pushed past.
            var then = manual.Pictures.TryGetValue(picture.Id, out var stood)
                ? new Rect(stood.X, stood.Y, stood.Width, stood.Height)
                : new Rect(left, top, right - left, bottom - top);

            pastLeft = Math.Max(pastLeft, Math.Min(left, then.Left) - now.Left);
            pastTop = Math.Max(pastTop, Math.Min(top, then.Top) - now.Top);
            pastRight = Math.Max(pastRight, now.Right - Math.Max(right, then.Right));
            pastBottom = Math.Max(pastBottom, now.Bottom - Math.Max(bottom, then.Bottom));
        }

        return Whole(left - pastLeft, top - pastTop, right + pastRight, bottom + pastBottom);
    }

    /// <summary>
    /// Records a canvas set by hand, along with where every picture stands as it is set.
    ///
    /// The pictures are recorded now because this is the moment the canvas and the pictures were
    /// seen together and found right: whatever reaches past the canvas at this point was meant to.
    /// </summary>
    public static void SetByHand(SnapshotDocument document, Rect canvas)
    {
        document.ManualCanvas = new ManualCanvas
        {
            X = (int)canvas.X,
            Y = (int)canvas.Y,
            Width = (int)canvas.Width,
            Height = (int)canvas.Height,
            Pictures = document.Layers
                .OfType<ImageAnnotation>()
                .ToDictionary(picture => picture.Id, picture => new PictureBounds
                {
                    X = picture.X, Y = picture.Y, Width = picture.Width, Height = picture.Height
                })
        };

        Apply(document, canvas);
    }

    /// <summary>Writes a rectangle into the document's canvas. True when that changed anything.</summary>
    public static bool Apply(SnapshotDocument document, Rect canvas)
    {
        var area = document.Canvas;

        if (area.X == (int)canvas.X && area.Y == (int)canvas.Y
            && area.Width == (int)canvas.Width && area.Height == (int)canvas.Height)
        {
            return false;
        }

        area.X = (int)canvas.X;
        area.Y = (int)canvas.Y;
        area.Width = (int)canvas.Width;
        area.Height = (int)canvas.Height;
        return true;
    }

    static Rect Bounds(ImageAnnotation picture) => new(picture.X, picture.Y, picture.Width, picture.Height);

    /// <summary>Outward to whole pixels, since the canvas is measured in them and a picture between them is still wholly on it.</summary>
    static Rect Whole(double left, double top, double right, double bottom)
    {
        var x = Math.Floor(left);
        var y = Math.Floor(top);

        return new Rect(x, y, Math.Ceiling(right) - x, Math.Ceiling(bottom) - y);
    }
}
