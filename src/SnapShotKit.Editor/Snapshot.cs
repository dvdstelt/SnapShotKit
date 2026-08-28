using System.IO.Compression;
using System.Text.Json;
using Avalonia.Media.Imaging;

namespace SnapShotKit.Editor;

/// <summary>
/// An open `.ssk` file: the untouched capture, plus the annotations layered over it.
///
/// The original PNG bytes are kept exactly as they were read and written back unchanged on save.
/// Re-encoding the capture every time it is saved would quietly degrade it, and the promise of the
/// format is that the capture never changes.
/// </summary>
public sealed class Snapshot : IDisposable
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    Snapshot(string path, SnapshotDocument document, byte[] originalPng, Bitmap bitmap, string? meta)
    {
        Path = path;
        Document = document;
        OriginalPng = originalPng;
        Bitmap = bitmap;
        Meta = meta;
    }

    public string Path { get; private set; }

    public SnapshotDocument Document { get; }

    public byte[] OriginalPng { get; }

    public Bitmap Bitmap { get; }

    /// <summary>Carried through untouched so saving never discards what the daemon recorded.</summary>
    public string? Meta { get; }

    public static Snapshot Open(string path)
    {
        using var archive = ZipFile.OpenRead(path);

        var originalPng = Read(archive, "original.png")
            ?? throw new InvalidDataException($"{path} has no original.png, so it is not a snapshot.");

        var documentJson = ReadText(archive, "document.json");
        var document = documentJson is null
            ? new SnapshotDocument()
            : JsonSerializer.Deserialize<SnapshotDocument>(documentJson, Json) ?? new SnapshotDocument();

        using var stream = new MemoryStream(originalPng);
        var bitmap = new Bitmap(stream);

        // A document written before the canvas was recorded, or by hand, still opens.
        if (document.Canvas.Width == 0 || document.Canvas.Height == 0)
        {
            document.Canvas = new CanvasSize { Width = bitmap.PixelSize.Width, Height = bitmap.PixelSize.Height };
        }

        Migrate(document);

        return new Snapshot(path, document, originalPng, bitmap, ReadText(archive, "meta.json"));
    }

    /// <summary>
    /// Brings an older document up to the current format.
    ///
    /// Version 1 drew every blur before everything else regardless of where it sat in the layers,
    /// so that a blur could never hide an arrow. Version 2 honours the order instead, which is what
    /// gives moving an object forward or back any meaning. Moving the blurs to the front as the
    /// document is opened reproduces exactly what version 1 drew, so nobody's saved work changes
    /// appearance the first time they open it in a newer build.
    /// </summary>
    static void Migrate(SnapshotDocument document)
    {
        if (document.Version >= SnapshotDocument.Current)
        {
            return;
        }

        var blurs = document.Layers.OfType<BlurAnnotation>().Cast<Annotation>().ToList();
        var rest = document.Layers.Where(layer => layer is not BlurAnnotation).ToList();

        document.Layers.Clear();
        document.Layers.AddRange(blurs);
        document.Layers.AddRange(rest);

        document.Version = SnapshotDocument.Current;
    }

    public void SaveAs(string path)
    {
        // Write to a temporary file and move it into place, so an interrupted save cannot leave a
        // half-written snapshot where the original used to be.
        var temporary = path + ".writing";

        using (var file = File.Create(temporary))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            WriteText(archive, "document.json", JsonSerializer.Serialize(Document, Json));

            if (Meta is not null)
            {
                WriteText(archive, "meta.json", Meta);
            }

            var original = archive.CreateEntry("original.png", CompressionLevel.NoCompression);
            using var stream = original.Open();
            stream.Write(OriginalPng);
        }

        File.Move(temporary, path, overwrite: true);
        Path = path;
    }

    public void Save() => SaveAs(Path);

    static byte[]? Read(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    static string? ReadText(ZipArchive archive, string name)
    {
        var bytes = Read(archive, name);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>The decoded capture is native memory the collector cannot see, so it is released deliberately.</summary>
    public void Dispose() => Bitmap.Dispose();

    static void WriteText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
    }
}
