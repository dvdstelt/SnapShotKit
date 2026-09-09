using System.Text.Json;
using Avalonia.Platform.Storage;
using SnapShotKit.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace SnapShotKit.Editor;

/// <summary>
/// Brings an ordinary image in as a snapshot.
///
/// A picture that arrives from somewhere else, whether through a file manager, the Open dialog or a
/// colleague, has to become a `.ssk` before it can be annotated, because annotations live in a
/// document beside the picture rather than in the picture. Nothing else in the editor knows the
/// difference afterwards, and that is the point: there is one kind of open document.
///
/// The imported file is only ever read. What lands in the library is a new snapshot beside the
/// captures; the picture the user pointed at stays exactly where it was and exactly as it was.
/// </summary>
public static class ImageImport
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// What can be brought in, as extension and media type.
    ///
    /// These are the formats ImageSharp decodes without an extra package, which is also very nearly
    /// the list a file manager will offer a screenshot in. Kept here rather than spread between the
    /// file picker, the desktop entry and the argument handling, since all three have to agree or
    /// the file manager offers something the editor then refuses.
    /// </summary>
    public static readonly (string Extension, string MediaType)[] Formats =
    [
        (".png", "image/png"),
        (".jpg", "image/jpeg"),
        (".jpeg", "image/jpeg"),
        (".webp", "image/webp"),
        (".bmp", "image/bmp"),
        (".gif", "image/gif"),
        (".tif", "image/tiff"),
        (".tiff", "image/tiff")
    ];

    /// <summary>
    /// The one entry an Open dialog needs: every picture SnapShotKit can bring in, in one line.
    ///
    /// One filter rather than a row of them, because nobody opening a screenshot wants to first
    /// say which kind of screenshot it is. The media types are there for the portal's file chooser,
    /// which filters on those rather than on the patterns.
    /// </summary>
    public static FilePickerFileType Filter() => new("Images")
    {
        Patterns = [.. Formats.Select(format => $"*{format.Extension}")],
        MimeTypes = [.. Formats.Select(format => format.MediaType).Distinct()]
    };

    /// <summary>Whether the name says this is a picture to bring in rather than a snapshot to open.</summary>
    public static bool Handles(string path) =>
        Formats.Any(format => path.EndsWith(format.Extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Wraps the image at <paramref name="imagePath"/> in a new snapshot, and returns where it was
    /// written.
    /// </summary>
    public static string Create(string imagePath)
    {
        var full = Path.GetFullPath(imagePath);

        Directory.CreateDirectory(SnapShotKitPaths.Snapshots);

        var (png, width, height) = ToPng(full);
        var target = NextAvailablePath(Path.GetFileNameWithoutExtension(full));

        // Disposed straight away: this is the writing of a file, and whoever asked for it opens it
        // for themselves afterwards. Keeping the decoded picture alive here would be a second copy
        // of it in native memory for as long as the import was remembered.
        using var snapshot = Snapshot.Create(target, png, Meta(full, width, height));
        return target;
    }

    /// <summary>
    /// The picture as PNG bytes, which is the only thing a snapshot holds, and its size.
    ///
    /// A PNG is taken as it stands, bytes and all, rather than being decoded and encoded again.
    /// Re-encoding it would cost time and could only lose something, a colour profile or a bit
    /// depth, for a file that is already in exactly the format the container wants.
    ///
    /// What decides is what the file turns out to be, not what it is called. A JPEG somebody has
    /// named `.png` is common enough, and passing its bytes through unread would put a JPEG in an
    /// entry called `original.png`, where every reader afterwards would be entitled to be surprised.
    /// </summary>
    static (byte[] Png, int Width, int Height) ToPng(string path)
    {
        var bytes = File.ReadAllBytes(path);

        using var source = new MemoryStream(bytes);
        var info = Image.Identify(source);

        if (info.Metadata.DecodedImageFormat is PngFormat)
        {
            return (bytes, info.Width, info.Height);
        }

        source.Position = 0;

        // An animated GIF or a multi-page TIFF comes in as its first frame. A snapshot is one
        // picture, and the first frame is the one the file manager showed as the thumbnail.
        using var image = Image.Load(source);
        using var buffer = new MemoryStream();

        image.SaveAsPng(buffer, new PngEncoder());
        return (buffer.ToArray(), image.Width, image.Height);
    }

    /// <summary>
    /// What is recorded about where the picture came from.
    ///
    /// The same shape the daemon writes for a capture, minus the region, because there was no
    /// screen and no rectangle dragged out of one. Where it came from is worth keeping: it is the
    /// only way back to the original once the snapshot has been annotated for a week.
    /// </summary>
    static string Meta(string path, int width, int height) => JsonSerializer.Serialize(new
    {
        created = DateTimeOffset.Now,
        source = new { width, height },
        imported = new { from = path }
    }, Json);

    /// <summary>
    /// Where the snapshot goes.
    ///
    /// Named after the file it came from rather than given the next capture number. An imported
    /// picture already has a name the user chose and recognises, and "diagram.ssk" beside
    /// "diagram.png" is the connection the numbering would throw away.
    /// </summary>
    static string NextAvailablePath(string name)
    {
        // A file whose whole name is its extension leaves nothing to name the snapshot after, and
        // it still has to land somewhere.
        var stem = string.IsNullOrWhiteSpace(name) ? "image" : name;

        var candidate = Path.Combine(SnapShotKitPaths.Snapshots, $"{stem}.ssk");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (var number = 2; number < 10000; number++)
        {
            candidate = Path.Combine(SnapShotKitPaths.Snapshots, $"{stem}-{number}.ssk");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find a free name for {stem}.");
    }
}
