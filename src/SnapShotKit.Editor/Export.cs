using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SnapShotKit.Editor;

/// <summary>
/// Renders a snapshot to a flat image.
///
/// Export goes through the same renderer the canvas uses, so what lands in the file is what was on
/// screen. Anything else invites the two drifting apart.
/// </summary>
public static class Export
{
    /// <summary>Renders to PNG bytes, for handing to something that is not a file.</summary>
    public static byte[] ToPng(Snapshot snapshot, BlurCache blurs)
    {
        using var rendered = Render(snapshot, blurs);
        using var buffer = new MemoryStream();

        rendered.Save(buffer, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return buffer.ToArray();
    }

    /// <summary>
    /// The canvas, rendered.
    ///
    /// The target starts transparent and only what is drawn covers it, so a canvas pushed out past
    /// the capture comes out with real transparency around the picture rather than a colour someone
    /// has to guess at.
    /// </summary>
    static RenderTargetBitmap Render(Snapshot snapshot, BlurCache blurs)
    {
        var canvas = snapshot.Document.Canvas;

        // The canvas with its cuts closed up, which is what the file actually comes out as: a band
        // taken out of the middle makes the picture shorter, and the export is the picture.
        var area = snapshot.Layout.ToLaid(new Rect(canvas.X, canvas.Y, canvas.Width, canvas.Height));

        var size = new PixelSize(
            Math.Max((int)Math.Round(area.Width), 1),
            Math.Max((int)Math.Round(area.Height), 1));

        var rendered = new RenderTargetBitmap(size, new Vector(96, 96));

        using (var context = rendered.CreateDrawingContext())
        {
            SnapshotRenderer.Draw(context, snapshot, blurs, new Rect(0, 0, size.Width, size.Height), area);
        }

        return rendered;
    }

    /// <summary>
    /// Renders to a file, in whichever format the name asks for.
    ///
    /// The extension decides, because that is what the user typed into the save dialog and what
    /// every other tool on the desktop will read the file as. A name that asks for something not
    /// listed here is refused rather than quietly written as something else: a file called
    /// `shot.avif` holding a JPEG is worse than an error.
    /// </summary>
    public static void ToFile(Snapshot snapshot, BlurCache blurs, string path)
    {
        using var rendered = Render(snapshot, blurs);

        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            rendered.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            return;
        }

        // Avalonia writes PNG only, so everything else goes out through ImageSharp.
        using var image = ToImage(rendered);

        if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            // JPEG has no alpha. A canvas larger than its capture is transparent where the capture
            // is not, and transparency dropped rather than filled comes out black, so it is filled
            // here instead. White, because that is what a screenshot pasted into a document sits on.
            image.Mutate(context => context.BackgroundColor(Color.White));
            image.SaveAsJpeg(path);
            return;
        }

        if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
        {
            // Lossless, and so nothing is filled in: WebP carries alpha, so a canvas pushed out
            // past its capture comes out transparent exactly as the PNG does.
            //
            // Lossy would make the file smaller again, but a screenshot is text and hairlines
            // rather than a photograph, and that is the one thing lossy WebP smears. What this
            // format is being asked for here is a PNG at half the size, not a smaller JPEG.
            image.SaveAsWebp(path, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
            return;
        }

        throw new NotSupportedException(
            $"{System.IO.Path.GetExtension(path)} is not a format SnapShotKit writes. Use .png, .jpg or .webp.");
    }

    /// <summary>The rendered canvas as an ImageSharp image, which is where every format but PNG is written from.</summary>
    static Image<Bgra32> ToImage(RenderTargetBitmap rendered)
    {
        var size = rendered.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[(long)stride * size.Height];

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            rendered.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        Unpremultiply(pixels);

        return Image.LoadPixelData<Bgra32>(pixels, size.Width, size.Height);
    }

    /// <summary>
    /// Divides the colour back out by the alpha it was multiplied by.
    ///
    /// What comes out of the renderer is premultiplied: a half-transparent red is stored with its
    /// red already halved, which is the form a compositor wants because blending is then a multiply
    /// and an add rather than a division per pixel. ImageSharp expects the other form, and reading
    /// one as the other is silent: every fully opaque pixel is identical either way, so a picture
    /// looks perfect and only its soft edges are wrong. An arrow crossing the transparent margin
    /// comes out with a dark fringe along it, darkest where the edge is faintest.
    ///
    /// Avalonia's own PNG encoder does this on the way out, which is why exports were right until
    /// a second encoder was given the same buffer.
    ///
    /// A pixel with no alpha at all keeps no colour to recover. It is written as transparent black
    /// rather than divided by zero, which is what it already was on screen.
    /// </summary>
    static void Unpremultiply(byte[] pixels)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];

            if (alpha == 255)
            {
                continue;
            }

            if (alpha == 0)
            {
                pixels[index] = 0;
                pixels[index + 1] = 0;
                pixels[index + 2] = 0;
                continue;
            }

            // Rounded rather than truncated, and clamped: a channel can exceed its alpha by a step
            // through the renderer's own rounding, and 256 written into a byte would wrap to 0,
            // turning the brightest edge pixel into the darkest.
            pixels[index] = Recover(pixels[index], alpha);
            pixels[index + 1] = Recover(pixels[index + 1], alpha);
            pixels[index + 2] = Recover(pixels[index + 2], alpha);
        }
    }

    static byte Recover(byte channel, byte alpha) => (byte)Math.Min(255, (channel * 255 + alpha / 2) / alpha);
}
