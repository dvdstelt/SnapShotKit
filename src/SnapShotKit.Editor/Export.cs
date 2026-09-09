using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
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
    /// <summary>
    /// Renders to PNG bytes, for handing to something that is not a file.
    ///
    /// Avalonia's encoder rather than the one every file goes through, because there is nothing to
    /// ask for here: the clipboard wants a PNG with its transparency, which is what this writes,
    /// and going the long way round would copy the buffer to gain a setting nobody is offered.
    /// </summary>
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
    ///
    /// An export that is not keeping its transparency is flattened here, by painting the ground
    /// before anything stands on it, rather than by dropping the alpha channel afterwards. The
    /// renderer is the one thing that knows how a half-covered pixel should meet what is under it,
    /// and letting it answer means a soft edge over the margin lands on white the way it looks on
    /// screen instead of being composited a second time by hand.
    /// </summary>
    static RenderTargetBitmap Render(Snapshot snapshot, BlurCache blurs, Avalonia.Media.Color? ground = null)
    {
        var canvas = snapshot.Document.Canvas;

        // The canvas with its cuts closed up, which is what the file actually comes out as: a band
        // taken out of the middle makes the picture shorter, and the export is the picture.
        var area = snapshot.Layout.ToLaid(new Rect(canvas.X, canvas.Y, canvas.Width, canvas.Height));

        var size = new PixelSize(
            Math.Max((int)Math.Round(area.Width), 1),
            Math.Max((int)Math.Round(area.Height), 1));

        var rendered = new RenderTargetBitmap(size, new Vector(96, 96));
        var bounds = new Rect(0, 0, size.Width, size.Height);

        using (var context = rendered.CreateDrawingContext())
        {
            if (ground is { } fill)
            {
                context.FillRectangle(new Avalonia.Media.SolidColorBrush(fill), bounds);
            }

            SnapshotRenderer.Draw(context, snapshot, blurs, bounds, area);
        }

        return rendered;
    }

    /// <summary>
    /// Renders to a file, in whichever format the name asks for, with default settings for it.
    ///
    /// This is the command line's way in. The extension decides, and a name that asks for something
    /// not on the list is refused rather than quietly written as something else: a file called
    /// `shot.avif` holding a JPEG is worse than an error.
    /// </summary>
    public static void ToFile(Snapshot snapshot, BlurCache blurs, string path) =>
        ToFile(snapshot, blurs, path, ExportSettings.ForExtension(path));

    /// <summary>
    /// Renders to a file exactly as asked.
    ///
    /// The settings say the format rather than the name doing it, because by this point somebody
    /// has chosen one in a dialog and the file was named to match. Everything goes out through
    /// ImageSharp: Avalonia writes PNG and nothing else, and one encoder that can be told what to
    /// do beats two that agree only by inspection.
    /// </summary>
    public static void ToFile(Snapshot snapshot, BlurCache blurs, string path, ExportSettings settings)
    {
        using var rendered = Render(snapshot, blurs, settings.Transparent ? null : Avalonia.Media.Colors.White);
        using var image = ToImage(rendered);

        switch (settings.Format)
        {
            case ExportFormat.Jpeg:
                image.SaveAsJpeg(path, new JpegEncoder { Quality = settings.JpegQuality });
                return;

            case ExportFormat.Webp:
                image.SaveAsWebp(path, new WebpEncoder
                {
                    FileFormat = settings.WebpLossless ? WebpFileFormatType.Lossless : WebpFileFormatType.Lossy,
                    Quality = settings.WebpQuality,

                    // Nothing to compress away when the ground was painted in, and saying so keeps
                    // the encoder from carrying an alpha channel that is 255 everywhere.
                    TransparentColorMode = WebpTransparentColorMode.Clear
                });
                return;

            default:
                image.SaveAsPng(path, new PngEncoder
                {
                    // Written without an alpha channel when there is no transparency to keep, which
                    // is a quarter of the pixel data gone for a picture that looks identical.
                    ColorType = settings.Transparent ? PngColorType.RgbWithAlpha : PngColorType.Rgb
                });
                return;
        }
    }

    /// <summary>The rendered canvas as an ImageSharp image, which is what every file is written from.</summary>
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
