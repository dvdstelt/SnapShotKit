using System.Diagnostics;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SnapShotKit.Daemon;

/// <summary>
/// Turns an encoded capture into the same raw BGRA frame file the capture helper produces, so that
/// everything downstream, the overlay included, sees one shape of capture.
/// </summary>
public static class FrameFile
{
    public static async Task<CaptureResult> MaterialiseAsync(string pngPath, string framePath,
        Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync<Bgra32>(pngPath, cancellationToken);

        var stride = image.Width * 4;
        var pixels = new byte[(long)stride * image.Height];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                MemoryMarshal.AsBytes(row).CopyTo(pixels.AsSpan(y * stride));
            }
        });

        await File.WriteAllBytesAsync(framePath, pixels, cancellationToken);

        return CaptureResult.Raw(pixels, new Frame(image.Width, image.Height, stride, pixels.Length), stopwatch.Elapsed);
    }
}
