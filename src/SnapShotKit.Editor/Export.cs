using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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

    static RenderTargetBitmap Render(Snapshot snapshot, BlurCache blurs)
    {
        var canvas = snapshot.Document.Canvas;
        var rendered = new RenderTargetBitmap(new PixelSize(canvas.Width, canvas.Height), new Vector(96, 96));

        using (var context = rendered.CreateDrawingContext())
        {
            SnapshotRenderer.Draw(context, snapshot, blurs, new Rect(0, 0, canvas.Width, canvas.Height));
        }

        return rendered;
    }

    public static void ToFile(Snapshot snapshot, BlurCache blurs, string path)
    {
        var canvas = snapshot.Document.Canvas;
        var size = new PixelSize(canvas.Width, canvas.Height);

        using var rendered = new RenderTargetBitmap(size, new Vector(96, 96));

        using (var context = rendered.CreateDrawingContext())
        {
            SnapshotRenderer.Draw(context, snapshot, blurs, new Rect(0, 0, canvas.Width, canvas.Height));
        }

        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            rendered.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            return;
        }

        // Avalonia writes PNG only, so JPEG goes out through ImageSharp. JPEG has no alpha, which
        // suits a screenshot: there is nothing transparent in it to lose.
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

        using var image = Image.LoadPixelData<Bgra32>(pixels, size.Width, size.Height);
        image.SaveAsJpeg(path);
    }
}
