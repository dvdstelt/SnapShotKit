using System.IO.Compression;
using System.Text.Json;
using SnapShotKit.Contracts;

namespace SnapShotKit.Editor;

/// <summary>One snapshot on disk, without opening it.</summary>
public sealed record SnapshotEntry(string Path, string Name, long Size, DateTime Modified)
{
    public string Summary => $"{Modified:yyyy-MM-dd HH:mm}   {Size / 1024.0 / 1024.0:F2} MB";
}

/// <summary>
/// The snapshots folder.
///
/// Listing is deliberately cheap: file metadata only, no zips opened. With a few hundred snapshots,
/// reading inside every one of them to build a list would make opening the library feel broken.
/// Anything more detailed is loaded for the one snapshot the user actually selects.
/// </summary>
public static class SnapshotLibrary
{
    public static string Folder => SnapShotKitPaths.Snapshots;

    public static IReadOnlyList<SnapshotEntry> List()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        return
        [
            .. new DirectoryInfo(Folder)
                .GetFiles("*.ssk")
                // .sks is what snapshots were called before the tool was renamed. Still listed, so
                // nobody's existing work quietly disappears from the library.
                .Concat(new DirectoryInfo(Folder).GetFiles("*.sks"))
                .OrderByDescending(file => file.LastWriteTime)
                .Select(file => new SnapshotEntry(file.FullName, file.Name, file.Length, file.LastWriteTime))
        ];
    }

    /// <summary>Reads just enough out of a snapshot to describe it, without decoding the capture.</summary>
    public static string Describe(SnapshotEntry entry)
    {
        try
        {
            using var archive = ZipFile.OpenRead(entry.Path);
            var document = archive.GetEntry("document.json");

            if (document is null)
            {
                return "not a snapshot";
            }

            using var stream = document.Open();
            using var json = JsonDocument.Parse(stream);

            var canvas = json.RootElement.GetProperty("canvas");
            var layers = json.RootElement.TryGetProperty("layers", out var value) ? Objects(value).Count() : 0;

            return $"{canvas.GetProperty("width").GetInt32()} x {canvas.GetProperty("height").GetInt32()}"
                + $"   {layers} annotation(s)";
        }
        catch (Exception exception)
        {
            return $"could not be read: {exception.Message}";
        }
    }

    /// <summary>
    /// The layers worth describing, which is every one but the capture's own.
    ///
    /// A snapshot written since the capture became a layer has one before anything is drawn on it,
    /// and "1 image" under every capture in the library says nothing about any of them. A picture
    /// pasted in is something somebody did, and is counted like an arrow is.
    /// </summary>
    static IEnumerable<JsonElement> Objects(JsonElement layers) => layers.EnumerateArray().Where(layer =>
        !(layer.TryGetProperty("type", out var type) && type.GetString() == "image"
            && layer.TryGetProperty("source", out var source) && source.GetString() == ImageAnnotation.Capture));

    /// <summary>
    /// What has been drawn on a capture, in words.
    ///
    /// Reads only document.json, which is a few hundred bytes, and never touches the capture
    /// itself: the whole point of listing cheaply is not to decode four hundred screenshots.
    /// </summary>
    public static string DescribeContents(SnapshotEntry entry)
    {
        try
        {
            using var archive = ZipFile.OpenRead(entry.Path);

            if (archive.GetEntry("document.json") is not { } document)
            {
                return "not a snapshot";
            }

            using var stream = document.Open();
            using var json = JsonDocument.Parse(stream);

            var objects = json.RootElement.TryGetProperty("layers", out var layers) ? Objects(layers).ToList() : [];

            if (objects.Count == 0)
            {
                return "no objects";
            }

            var counts = new Dictionary<string, int>();

            foreach (var layer in objects)
            {
                var kind = layer.TryGetProperty("type", out var type) ? type.GetString() ?? "object" : "object";
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
            }

            // Past a couple of kinds the list is longer than the space it sits in and less useful
            // than the total, so it collapses to a count.
            if (counts.Count > 2)
            {
                return $"{objects.Count} objects";
            }

            // Ordered, so the same document always describes itself the same way: a dictionary's
            // own order would have one capture read "blur, arrow" and its twin "arrow, blur".
            return string.Join(", ", counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value == 1 ? pair.Key : $"{pair.Value} {pair.Key}s"));
        }
        catch
        {
            return "could not be read";
        }
    }

    /// <summary>
    /// A name in the snapshots folder that nothing has taken yet, made from <paramref name="name"/>.
    ///
    /// Named after something the user recognises rather than given the next capture number: an
    /// imported picture after its own file, a blank canvas after being untitled. Colliding names
    /// take a suffix.
    /// </summary>
    public static string FreePath(string name)
    {
        // A file whose whole name is its extension leaves nothing to name the snapshot after, and
        // it still has to land somewhere.
        var stem = string.IsNullOrWhiteSpace(name) ? "image" : name;

        var candidate = Path.Combine(Folder, $"{stem}.ssk");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (var number = 2; number < 10000; number++)
        {
            candidate = Path.Combine(Folder, $"{stem}-{number}.ssk");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find a free name for {stem}.");
    }

    public static void Delete(SnapshotEntry entry) => Delete(entry.Path);

    public static void Delete(string path) => File.Delete(path);
}
