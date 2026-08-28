using System.IO.MemoryMappedFiles;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SnapShotKit.Overlay;

/// <summary>Describes the captured frame waiting in the shared file.</summary>
public readonly record struct FrameInfo(string Path, int Width, int Height, int Stride);

public static class Frame
{
    /// <summary>
    /// Maps the shared frame file and copies it into a bitmap the compositor can draw.
    ///
    /// The frame arrives as BGRx: three colour bytes and a fourth that is undefined rather than
    /// alpha. Copied verbatim into a bitmap that respects alpha, a frame whose fourth byte happens
    /// to be zero renders as nothing at all, so alpha is forced opaque on the way in.
    /// </summary>
    public static WriteableBitmap Load(FrameInfo info)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(info.Width, info.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var file = MemoryMappedFile.CreateFromFile(info.Path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        using var locked = bitmap.Lock();

        unsafe
        {
            byte* source = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref source);

            try
            {
                for (var y = 0; y < info.Height; y++)
                {
                    var sourceRow = source + (long)y * info.Stride;
                    var destinationRow = (byte*)locked.Address + (long)y * locked.RowBytes;

                    Buffer.MemoryCopy(sourceRow, destinationRow, locked.RowBytes, info.Width * 4);

                    for (var x = 3; x < info.Width * 4; x += 4)
                    {
                        destinationRow[x] = 255;
                    }
                }
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        return bitmap;
    }
}
