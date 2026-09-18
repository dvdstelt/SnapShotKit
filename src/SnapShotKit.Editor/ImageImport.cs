using System.IO.Compression;
using System.Text.Json;
using Avalonia.Platform.Storage;
using SnapShotKit.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

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
    /// Wraps the image at <paramref name="imagePath"/> in a snapshot, and returns where that is.
    ///
    /// A new one the first time, and the same one after that. Somebody opening `diagram.png` from
    /// the file manager a second time is going back to their diagram, arrows and all, and handing
    /// them a bare copy called `diagram-2.ssk` would both hide the work they came back for and
    /// leave one more snapshot in the library for every time they did it. It is the same picture
    /// only while the file is the same file: once its contents change it is brought in afresh,
    /// since what was annotated before is then a picture that no longer exists on disk.
    /// </summary>
    public static string Create(string imagePath)
    {
        var full = Path.GetFullPath(imagePath);
        var bytes = File.ReadAllBytes(full);

        if (Existing(full, Fingerprint(bytes)) is { } already)
        {
            return already;
        }

        Directory.CreateDirectory(SnapShotKitPaths.Snapshots);

        // Disposed straight away: this is the writing of a file, and whoever asked for it opens it
        // for themselves afterwards. Keeping the decoded picture alive here would be a second copy
        // of it in native memory for as long as the import was remembered.
        using var snapshot = Wrap(full, bytes);
        snapshot.Save();
        return snapshot.Path;
    }

    static string Fingerprint(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>
    /// The snapshot already made out of this file with these contents, if there is one.
    ///
    /// Looked for under the names an import of this file would have been given, which is the
    /// file's own name and that name with a number after it, rather than by opening every snapshot
    /// in the library. One that has since been renamed is not found, and the cost of that is one
    /// more import, which is what happened every time before this.
    ///
    /// A snapshot that cannot be read is passed over rather than reported. This is a search for
    /// something to reuse, and a damaged file is simply not it.
    /// </summary>
    static string? Existing(string full, string fingerprint)
    {
        if (!Directory.Exists(SnapShotKitPaths.Snapshots))
        {
            return null;
        }

        // The same name FreePath gives a file whose whole name is its extension.
        var stem = Path.GetFileNameWithoutExtension(full) is { Length: > 0 } name && !string.IsNullOrWhiteSpace(name) ? name : "image";

        foreach (var candidate in Directory.EnumerateFiles(SnapShotKitPaths.Snapshots, stem + "*.ssk").Order(StringComparer.Ordinal))
        {
            // The pattern has wildcards of its own, which a file name is free to contain.
            if (!Path.GetFileName(candidate).StartsWith(stem, StringComparison.Ordinal))
            {
                continue;
            }

            var numbered = Path.GetFileNameWithoutExtension(candidate)[stem.Length..];

            if (numbered.Length > 0 && !(numbered[0] == '-' && numbered.Length > 1 && numbered[1..].All(char.IsAsciiDigit)))
            {
                continue;
            }

            try
            {
                using var archive = ZipFile.OpenRead(candidate);

                if (archive.GetEntry("meta.json") is not { } entry)
                {
                    continue;
                }

                using var meta = JsonDocument.Parse(entry.Open());

                if (meta.RootElement.TryGetProperty("imported", out var imported)
                    && imported.TryGetProperty("from", out var from) && from.GetString() == full
                    && imported.TryGetProperty("sha256", out var hash) && hash.GetString() == fingerprint)
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// The image at <paramref name="imagePath"/> as a snapshot that has not been written anywhere,
    /// which is all an export from the command line wants of it.
    /// </summary>
    public static Snapshot Wrap(string imagePath)
    {
        var full = Path.GetFullPath(imagePath);
        return Wrap(full, File.ReadAllBytes(full));
    }

    static Snapshot Wrap(string full, byte[] bytes)
    {
        var (png, width, height) = ToPng(bytes);
        // Named after the file it came from rather than given the next capture number. An imported
        // picture already has a name the user chose and recognises, and "diagram.ssk" beside
        // "diagram.png" is the connection the numbering would throw away.
        var target = SnapshotLibrary.FreePath(Path.GetFileNameWithoutExtension(full));

        return Snapshot.Wrap(target, png, Meta(full, Fingerprint(bytes), width, height));
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
    ///
    /// Taken from memory rather than from a path, since a picture on the clipboard has no path and
    /// one on disk has already been read to see whether it was brought in before.
    ///
    /// Throws when the bytes turn out not to be a picture ImageSharp can read, whatever they claimed
    /// to be.
    /// </summary>
    public static (byte[] Png, int Width, int Height) ToPng(byte[] bytes)
    {
        using var source = new MemoryStream(bytes);
        var info = Image.Identify(source);

        if (info.Metadata.DecodedImageFormat is PngFormat)
        {
            return (bytes, info.Width, info.Height);
        }

        source.Position = 0;

        // An animated GIF or a multi-page TIFF comes in as its first frame. A snapshot is one
        // picture, and the first frame is the one the file manager showed as the thumbnail. The
        // decoder has to be told, because the PNG encoder writes every frame it is handed, as an
        // animated PNG, and two hundred frames would then ride along in the file for good.
        using var image = Image.Load(new DecoderOptions { MaxFrames = 1 }, source);

        // A camera writes the pixels the way the sensor lay and records which way up it was held.
        // Everything that showed this picture before now turned it accordingly, and a PNG drawn
        // here is drawn as its pixels lie, so the turn is made once, in the pixels, on the way in.
        image.Mutate(turn => turn.AutoOrient());

        using var buffer = new MemoryStream();

        image.SaveAsPng(buffer, new PngEncoder());
        return (buffer.ToArray(), image.Width, image.Height);
    }

    /// <summary>
    /// What is recorded about where the picture came from.
    ///
    /// The same shape the daemon writes for a capture, minus the region, because there was no
    /// screen and no rectangle dragged out of one. Where it came from is worth keeping: it is the
    /// only way back to the original once the snapshot has been annotated for a week. What the
    /// file held is kept beside it, as a hash, so opening the same file again can be told from
    /// opening a different picture that has since been saved under the same name.
    /// </summary>
    static string Meta(string path, string sha256, int width, int height) => JsonSerializer.Serialize(new
    {
        created = DateTimeOffset.Now,
        source = new { width, height },
        imported = new { from = path, sha256 }
    }, Json);
}
