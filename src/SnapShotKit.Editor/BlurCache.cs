using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SnapShotKit.Editor;

/// <summary>
/// Blurred copies of the whole capture, one per radius in use.
///
/// Blurring is done once per radius rather than per annotation per frame: a blur region is then just
/// the corresponding patch of an already blurred image, which costs the same as drawing any other
/// bitmap. Doing it the other way round would mean a gaussian blur on every repaint.
/// </summary>
public sealed class BlurCache(byte[] originalPng) : IDisposable
{
    /// <summary>
    /// How many strengths to keep. Each one is a full-resolution copy of the capture, tens of
    /// megabytes at 4K, so an unbounded cache turns a strength slider into a memory leak. A few is
    /// enough for every blur on a typical document; a strength evicted early is simply regenerated.
    /// </summary>
    const int KeepAtMost = 3;

    readonly Dictionary<int, Bitmap> cache = [];

    /// <summary>Strengths in order of use, least recent first, so eviction drops the stalest.</summary>
    readonly List<int> recency = [];

    /// <param name="strength">1 to 100, as stored on the annotation rather than a gaussian sigma.</param>
    public Bitmap For(int strength)
    {
        strength = Math.Clamp(strength <= 0 ? 45 : strength, 1, 100);

        if (cache.TryGetValue(strength, out var existing))
        {
            recency.Remove(strength);
            recency.Add(strength);
            return existing;
        }

        using var stream = new MemoryStream(originalPng);
        using var image = Image.Load<Bgra32>(stream);

        image.Mutate(context => context.GaussianBlur(BlurAnnotation.Sigmaof(strength)));

        var bitmap = ToBitmap(image);
        cache[strength] = bitmap;
        recency.Add(strength);

        while (cache.Count > KeepAtMost)
        {
            var oldest = recency[0];
            recency.RemoveAt(0);
            cache[oldest].Dispose();
            cache.Remove(oldest);
        }

        return bitmap;
    }

    static WriteableBitmap ToBitmap(Image<Bgra32> image)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var locked = bitmap.Lock();

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var source = MemoryMarshal.AsBytes(accessor.GetRowSpan(y));

                unsafe
                {
                    var destination = new Span<byte>((byte*)locked.Address + (long)y * locked.RowBytes, locked.RowBytes);
                    source[..Math.Min(source.Length, destination.Length)].CopyTo(destination);
                }
            }
        });

        return bitmap;
    }

    public void Dispose()
    {
        foreach (var bitmap in cache.Values)
        {
            bitmap.Dispose();
        }

        cache.Clear();
    }
}
