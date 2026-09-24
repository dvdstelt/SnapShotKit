using Avalonia;

namespace SnapShotKit.Editor;

/// <summary>
/// Where a cropped picture's pixels stand on the canvas, and the other way round.
///
/// A crop is not a pixel edit, any more than a cut is. The picture keeps every pixel it arrived
/// with, and the crop only says which of them are drawn. So a picture has two rectangles on the
/// canvas: the one it shows, which is its layer's own X, Y, Width and Height and is what gets
/// selected, moved and stretched, and the whole of it, which is where the pixels it is not showing
/// would be if they were. Cropping moves the first inside the second; the second is only ever seen
/// while a crop is being made, so the part being taken off stays in sight.
///
/// Flipping is the one thing that makes this more than a subtraction. A crop is kept in the
/// picture's own pixels, before the flip, so on a picture turned round left to right the pixels
/// cropped off its left are the ones missing from the right of what is on the canvas.
/// </summary>
public static class PictureCrop
{
    /// <summary>The part of the picture drawn, in its own pixels: the crop, or all of it when there is none.</summary>
    public static Rect Source(ImageAnnotation picture, PixelSize size) => picture.Crop is { } crop
        ? new Rect(crop.X, crop.Y, crop.Width, crop.Height)
        : new Rect(0, 0, size.Width, size.Height);

    /// <summary>Where the whole picture would stand on the canvas with nothing cropped off it, in capture pixels.</summary>
    public static Rect Whole(ImageAnnotation picture, PixelSize size)
    {
        var source = Source(picture, size);

        if (source.Width <= 0 || source.Height <= 0)
        {
            return new Rect(picture.X, picture.Y, picture.Width, picture.Height);
        }

        var across = picture.Width / source.Width;
        var down = picture.Height / source.Height;

        // How much of the picture is missing before the part on show, on the canvas. Mirrored, what
        // is missing before it on the canvas is what the crop took off the far side.
        var before = (picture.FlipHorizontal ? size.Width - source.Right : source.X) * across;
        var above = (picture.FlipVertical ? size.Height - source.Bottom : source.Y) * down;

        return new Rect(picture.X - before, picture.Y - above, size.Width * across, size.Height * down);
    }

    /// <summary>
    /// Shows only the part of the picture that falls in <paramref name="shown"/>, which is in capture
    /// pixels and held inside <see cref="Whole"/>. The picture stays where it is on the canvas: what
    /// is left of it does not move, and the crop is simply dropped when nothing is left out.
    /// </summary>
    public static void Show(ImageAnnotation picture, PixelSize size, Rect shown)
    {
        var whole = Whole(picture, size);
        shown = shown.Intersect(whole);

        if (whole.Width <= 0 || whole.Height <= 0 || shown.Width <= 0 || shown.Height <= 0)
        {
            return;
        }

        var across = whole.Width / size.Width;
        var down = whole.Height / size.Height;

        var width = shown.Width / across;
        var height = shown.Height / down;

        var x = picture.FlipHorizontal ? (whole.Right - shown.Right) / across : (shown.X - whole.X) / across;
        var y = picture.FlipVertical ? (whole.Bottom - shown.Bottom) / down : (shown.Y - whole.Y) / down;

        picture.X = shown.X;
        picture.Y = shown.Y;
        picture.Width = shown.Width;
        picture.Height = shown.Height;

        // All of it, near enough, is no crop at all. Kept as one it would say the picture had been
        // cropped, and the document would carry a crop that crops nothing.
        const double near = 0.01;

        picture.Crop = x < near && y < near && Math.Abs(width - size.Width) < near && Math.Abs(height - size.Height) < near
            ? null
            : new CropArea { X = x, Y = y, Width = width, Height = height };
    }
}
