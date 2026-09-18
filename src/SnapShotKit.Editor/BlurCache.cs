using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SnapShotKit.Editor;

/// <summary>
/// Blurred copies of whole pictures, one per picture and radius in use.
///
/// Blurring is done once per radius rather than per annotation per frame: a blur region is then just
/// the corresponding patch of an already blurred image, which costs the same as drawing any other
/// bitmap. Doing it the other way round would mean a gaussian blur on every repaint.
///
/// Per picture rather than of the capture alone, because a blur hides whatever pictures are under
/// it, and since the capture can be moved and pictures pasted beside it, that is no longer only
/// ever the capture at the origin.
/// </summary>
public sealed class BlurCache(Snapshot snapshot) : IDisposable
{
    /// <summary>
    /// How many blurred copies to keep. Each one is a full-resolution copy of a picture, tens of
    /// megabytes for a 4K capture, so an unbounded cache turns a strength slider into a memory leak.
    /// A few is enough for every blur on a typical document, including one laid across a capture
    /// and a picture pasted beside it; a copy evicted early is simply regenerated.
    ///
    /// Not a limit on what the document itself needs. A copy some blur is drawn from right now is
    /// never dropped, however many of those there are: every repaint asks for all of them, so
    /// dropping one to make room for another would be a full-resolution gaussian inside every
    /// frame, for as long as the document stayed as it was. This bounds what is left over, which
    /// is mostly the strengths a slider passed through on its way somewhere else.
    /// </summary>
    const int KeepAtMost = 6;

    /// <summary>
    /// Pictures that would not decode, so they are not decoded again on every repaint only to fail
    /// the same way. A picture's bytes never change under the name it is kept by.
    /// </summary>
    readonly HashSet<string> undecodable = [];

    readonly Dictionary<(string Source, int Strength), Bitmap> cache = [];

    /// <summary>What was asked for, least recent first, so eviction drops the stalest.</summary>
    readonly List<(string Source, int Strength)> recency = [];

    /// <param name="source">The entry the picture is kept under.</param>
    /// <param name="strength">1 to 100, as stored on the annotation rather than a gaussian sigma.</param>
    /// <returns>Null when there is no such picture, or it would not decode.</returns>
    public Bitmap? For(string source, int strength)
    {
        var key = (source, Normalised(strength));

        if (cache.TryGetValue(key, out var existing))
        {
            recency.Remove(key);
            recency.Add(key);
            return existing;
        }

        if (undecodable.Contains(source) || snapshot.PngOf(source) is not { } png)
        {
            return null;
        }

        Image<Bgra32> image;

        try
        {
            using var stream = new MemoryStream(png);
            image = Image.Load<Bgra32>(stream);
        }
        catch (Exception)
        {
            // The same picture the snapshot could not draw either. Nothing under the blur to hide.
            undecodable.Add(source);
            return null;
        }

        using (image)
        {
            image.Mutate(context => context.GaussianBlur(BlurAnnotation.Sigmaof(key.Item2)));

            var bitmap = ToBitmap(image);
            cache[key] = bitmap;
            recency.Add(key);
        }

        if (cache.Count > KeepAtMost)
        {
            var wanted = Wanted();

            // Stalest first, and only what no blur is drawn from any more.
            foreach (var stale in recency.Where(stale => !wanted.Contains(stale)).Take(cache.Count - KeepAtMost).ToList())
            {
                recency.Remove(stale);
                cache[stale].Dispose();
                cache.Remove(stale);
            }
        }

        return cache[key];
    }

    static int Normalised(int strength) => Math.Clamp(strength <= 0 ? 45 : strength, 1, 100);

    /// <summary>Every copy the document as it stands is drawn from: each blur, and each picture under it.</summary>
    HashSet<(string Source, int Strength)> Wanted()
    {
        var wanted = new HashSet<(string, int)>();
        var layers = snapshot.Document.Layers;

        for (var index = 0; index < layers.Count; index++)
        {
            if (layers[index] is not BlurAnnotation blur)
            {
                continue;
            }

            foreach (var image in layers.Take(index).OfType<ImageAnnotation>().Where(image => SnapshotRenderer.Hides(blur, image)))
            {
                wanted.Add((image.Source, Normalised(blur.Strength)));
            }
        }

        return wanted;
    }

    static WriteableBitmap ToBitmap(Image<Bgra32> image)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            // Straight alpha, which is what ImageSharp holds. A capture is opaque and comes out the
            // same either way, but a pasted picture may have transparent corners, and read as
            // opaque those would blur into black.
            AlphaFormat.Unpremul);

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
