using System.IO.Compression;
using System.Text.Json;
using SnapShotKit.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace SnapShotKit.Daemon;

/// <summary>
/// Writes a `.ssk` snapshot: a SnapShotKit snapshot, which is a zip container in the manner of ODF or OOXML.
///
/// The point of the format is that editing stays non-destructive. `original.png` is the capture as
/// taken and is never modified; annotations live in `document.json` as objects with their own
/// coordinates, so an arrow drawn today can be moved or deleted next week. Exporting to PNG or JPEG
/// renders the document, rather than being the document.
/// </summary>
public static class SnapshotWriter
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    static readonly PngEncoder Encoder = new()
    {
        CompressionLevel = PngCompressionLevel.BestSpeed,
        ColorType = PngColorType.Rgb
    };

    public static async Task<string> WriteAsync(Image<Rgb24> image, CaptureRegion region,
        int sourceWidth, int sourceHeight, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(SnapShotKitPaths.Snapshots);

        var path = NextAvailablePath();

        // Write beside and move into place, the same way the editor saves: a crash mid-write must
        // not leave a truncated snapshot sitting in the library looking like a real one.
        var temporary = path + ".writing";

        await using (var file = File.Create(temporary))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, "document.json", new
            {
                version = 1,
                canvas = new { width = image.Width, height = image.Height },
                // Empty for now. The editor fills this in, and everything in it is an object with
                // geometry rather than pixels burned into the image.
                layers = Array.Empty<object>()
            }, cancellationToken);

            await WriteEntryAsync(archive, "meta.json", new
            {
                created = DateTimeOffset.Now,
                source = new { width = sourceWidth, height = sourceHeight },
                region = new { x = region.X, y = region.Y, width = region.Width, height = region.Height }
            }, cancellationToken);

            var original = archive.CreateEntry("original.png", CompressionLevel.NoCompression);
            await using var stream = original.Open();

            // Already compressed by the PNG encoder, so the zip should not try again.
            await image.SaveAsPngAsync(stream, Encoder, cancellationToken);
        }

        File.Move(temporary, path, overwrite: true);
        return path;
    }

    static async Task WriteEntryAsync(ZipArchive archive, string name, object content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, content, Json, cancellationToken);
    }

    static string NextAvailablePath()
    {
        // Numbered rather than timestamped: these are working documents the user will reopen and
        // refer to, and "snapshot-04" is easier to say out loud than a timestamp.
        for (var number = 1; number < 10000; number++)
        {
            var candidate = Path.Combine(SnapShotKitPaths.Snapshots, $"snapshot-{number:00}.ssk");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find a free snapshot number.");
    }
}
