using SnapShotKit.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SnapShotKit.Daemon;

/// <summary>Turns a raw capture into something on disk.</summary>
public static class CaptureWriter
{
    /// <summary>
    /// A screenshot tool that pauses for seconds to save is broken, and the last few percent of
    /// compression is not worth that. BestSpeed costs about 13% file size and saves several seconds.
    /// </summary>
    static PngEncoder FastPng { get; } = new()
    {
        CompressionLevel = PngCompressionLevel.BestSpeed,
        ColorType = PngColorType.Rgb,
        FilterMethod = PngFilterMethod.Sub
    };

    /// <summary>
    /// Builds the image the user actually selected. The caller owns the result.
    ///
    /// The frame arrives as BGRx: three colour bytes and a fourth that is undefined rather than
    /// alpha. Converting to Rgb24 both fixes the channel order and drops a quarter of the bytes any
    /// encoder has to deal with.
    /// </summary>
    public static Image<Rgb24> Compose(CaptureResult capture, CaptureRegion? region)
    {
        var image = new Image<Rgb24>(capture.Width, capture.Height);
        var pixels = capture.Pixels;
        var stride = capture.Stride;
        var width = capture.Width;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                // Reinterpret the raw row as Bgra32 and let ImageSharp's vectorised conversion do
                // the shuffle, rather than constructing eight million pixels one at a time.
                var source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Bgra32>(
                    pixels.AsSpan(y * stride, width * 4));

                PixelOperations<Bgra32>.Instance.ToRgb24(Configuration.Default, source, accessor.GetRowSpan(y));
            }
        });

        if (region is { } crop)
        {
            image.Mutate(context => context.Crop(Clamp(crop, image.Width, image.Height)));
        }

        return image;
    }

    public static async Task<string> SaveAsync(CaptureResult capture, CaptureRegion? region = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(SnapShotKitPaths.Exports);

        using var image = Compose(capture, region);
        var destination = NextAvailablePath();

        await image.SaveAsPngAsync(destination, FastPng, cancellationToken);
        return destination;
    }

    /// <summary>Keeps a selection inside the image, since the overlay works in its own coordinates.</summary>
    static Rectangle Clamp(CaptureRegion region, int width, int height)
    {
        var x = Math.Clamp(region.X, 0, Math.Max(width - 1, 0));
        var y = Math.Clamp(region.Y, 0, Math.Max(height - 1, 0));

        return new Rectangle(x, y,
            Math.Clamp(region.Width, 1, width - x),
            Math.Clamp(region.Height, 1, height - y));
    }

    static string NextAvailablePath()
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        var candidate = Path.Combine(SnapShotKitPaths.Exports, $"SnapShotKit {stamp}.png");

        // Two captures inside the same second is unlikely but not impossible, and silently
        // overwriting one of them would be the worst possible outcome.
        var attempt = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(SnapShotKitPaths.Exports, $"SnapShotKit {stamp} ({attempt++}).png");
        }

        return candidate;
    }
}
