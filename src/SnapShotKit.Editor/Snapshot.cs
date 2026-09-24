using System.IO.Compression;
using System.Text.Json;
using Avalonia.Media.Imaging;

namespace SnapShotKit.Editor;

/// <summary>
/// An open `.ssk` file: the untouched capture, any pictures pasted in beside it, and the annotations
/// layered over them.
///
/// The original PNG bytes are kept exactly as they were read and written back unchanged on save,
/// and so are those of every pasted picture. Re-encoding them every time the document is saved
/// would quietly degrade them, and the promise of the format is that a picture never changes.
/// </summary>
public sealed class Snapshot : IDisposable
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Where pasted pictures are kept inside the archive, each named after its own contents.</summary>
    const string ImageFolder = "images/";

    /// <summary>
    /// How large a blank canvas starts, before anything is put on it.
    ///
    /// Only a starting point. The canvas fits the first picture pasted onto it, so this is the size
    /// of the empty space somebody is looking at while they reach for Ctrl+V, and nothing more.
    /// </summary>
    public static readonly Avalonia.PixelSize BlankSize = new(800, 600);

    /// <summary>What a blank snapshot is called until somebody saves it as something else.</summary>
    const string BlankName = "untitled";

    Snapshot(string path, SnapshotDocument document, byte[]? originalPng, Bitmap? bitmap, string? meta, bool written)
    {
        Path = path;
        Document = document;
        Bitmap = bitmap;
        Meta = meta;
        OriginFolder = FolderOf(meta);
        this.written = written;

        if (originalPng is not null)
        {
            pictures[ImageAnnotation.Capture] = new Picture(originalPng, bitmap);
        }
    }

    /// <summary>
    /// Whether this snapshot has ever been on disk at <see cref="Path"/>.
    ///
    /// Only a blank one has not. Its name was chosen when it was made, and another blank may have
    /// been saved under that same name since: saving over it would quietly replace somebody's work.
    /// </summary>
    bool written;

    /// <summary>Whether <see cref="Path"/> names a file this snapshot is in, rather than one it has yet to be saved as.</summary>
    public bool OnDisk => written;

    /// <summary>A picture's bytes as they arrived, and the decoded bitmap drawn from them, which is null when they would not decode.</summary>
    sealed record Picture(byte[] Png, Bitmap? Bitmap);

    /// <summary>
    /// Every picture this snapshot holds, by the entry it is kept in.
    ///
    /// Only ever added to while the document is open. A picture pasted and then deleted is still
    /// wanted by the undo history, which holds layers rather than pixels, so dropping it here would
    /// leave an undone paste pointing at nothing. What is written out is only what the layers still
    /// use, so nothing deleted survives a save.
    /// </summary>
    readonly Dictionary<string, Picture> pictures = new(StringComparer.Ordinal);

    public string Path { get; private set; }

    public SnapshotDocument Document { get; }

    /// <summary>
    /// The capture, decoded, or null for a blank snapshot. Wherever its layer has been moved to, this
    /// is the picture as taken.
    /// </summary>
    public Bitmap? Bitmap { get; }

    /// <summary>
    /// The canvas when there are no pictures on it: the capture where it was taken, or the size a
    /// blank canvas starts at.
    /// </summary>
    public Avalonia.Size EmptySize => Bitmap is { } capture
        ? new Avalonia.Size(capture.PixelSize.Width, capture.PixelSize.Height)
        : new Avalonia.Size(BlankSize.Width, BlankSize.Height);

    /// <summary>Carried through untouched so saving never discards what the daemon recorded.</summary>
    public string? Meta { get; }

    /// <summary>
    /// The folder holding the picture this snapshot was made out of, when there was one.
    ///
    /// Only an imported snapshot has one. A capture came off a screen, and a screen is not a folder
    /// anybody wants an export written back into.
    ///
    /// Read once, when the snapshot is opened, and only believed if the folder is still there. A
    /// picture opened off a memory stick last month is a path that resolves to nothing now, and an
    /// export dialog offering to start somewhere that does not exist is worse than not offering.
    /// </summary>
    public string? OriginFolder { get; }

    /// <summary>
    /// Digs the imported picture's folder out of the recorded metadata.
    ///
    /// Hand-parsed rather than deserialised into a type, because this reads one field out of a
    /// document the daemon and the importer each write differently and neither promises to keep.
    /// Anything unexpected in there means there is no origin, which is the same answer a capture
    /// gives, and no reason to fail opening a snapshot that is otherwise perfectly good.
    /// </summary>
    static string? FolderOf(string? meta)
    {
        if (meta is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(meta);

            if (document.RootElement.TryGetProperty("imported", out var imported)
                && imported.TryGetProperty("from", out var from)
                && from.GetString() is { Length: > 0 } origin
                && System.IO.Path.GetDirectoryName(origin) is { Length: > 0 } folder
                && Directory.Exists(folder))
            {
                return folder;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// Where the picture ends up once its cuts are closed up.
    ///
    /// Worked out from the document and kept until the cuts change, because it is asked for on
    /// every repaint and every pointer movement, and walking the bands to build it each time would
    /// be work done thousands of times for an answer that changes when somebody makes a cut.
    /// </summary>
    public CutLayout Layout => layout ??= new CutLayout(Document.Cuts);

    CutLayout? layout;

    /// <summary>Called when the cuts have changed, so the layout is worked out again.</summary>
    public void Recut() => layout = null;

    /// <summary>The decoded picture kept under <paramref name="source"/>, or null when there is none to draw.</summary>
    public Bitmap? BitmapOf(string source) => pictures.GetValueOrDefault(source)?.Bitmap;

    /// <summary>The bytes of the picture kept under <paramref name="source"/>, exactly as they arrived.</summary>
    public byte[]? PngOf(string source) => pictures.GetValueOrDefault(source)?.Png;

    /// <summary>
    /// Takes a picture in, and returns the entry it is kept under.
    ///
    /// Named after a hash of its bytes, so pasting the same picture twice keeps one copy of it, and
    /// a name can never come to mean a different picture from the one it meant when a layer was
    /// pointed at it. Decoded here rather than when first drawn, so a clipboard holding something
    /// that is not a picture after all is refused at the paste instead of leaving an empty layer.
    /// </summary>
    public string AddImage(byte[] png)
    {
        var name = ImageFolder + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png))[..16] + ".png";

        if (!pictures.ContainsKey(name))
        {
            using var stream = new MemoryStream(png);
            pictures[name] = new Picture(png, new Bitmap(stream));
        }

        return name;
    }

    public static Snapshot Open(string path)
    {
        using var archive = ZipFile.OpenRead(path);

        // Either may be missing, but not both. A snapshot started blank has a document and no
        // capture; one from before documents were written has a capture and no document.
        var originalPng = Read(archive, ImageAnnotation.Capture);
        var documentJson = ReadText(archive, "document.json");

        if (originalPng is null && documentJson is null)
        {
            throw new InvalidDataException($"{path} has neither a capture nor a document, so it is not a snapshot.");
        }

        // A snapshot with no document at all predates every version, and is migrated from nothing.
        // Given the current version instead it would be taken as already having its capture layer,
        // and open as an empty canvas.
        var document = documentJson is null
            ? new SnapshotDocument { Version = 0 }
            : JsonSerializer.Deserialize<SnapshotDocument>(documentJson, Json) ?? new SnapshotDocument { Version = 0 };

        Bitmap? bitmap = null;

        if (originalPng is not null)
        {
            using var stream = new MemoryStream(originalPng);
            bitmap = new Bitmap(stream);
        }

        var size = bitmap?.PixelSize ?? BlankSize;

        // A document written before the canvas was recorded, or by hand, still opens: the canvas is
        // then exactly the capture, which is what it was before a canvas could be anything else.
        if (document.Canvas.Width == 0 || document.Canvas.Height == 0)
        {
            document.Canvas = new CanvasArea { Width = size.Width, Height = size.Height };
        }

        Migrate(document, bitmap?.PixelSize);

        var snapshot = new Snapshot(path, document, originalPng, bitmap, ReadText(archive, "meta.json"), written: true);

        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith(ImageFolder, StringComparison.Ordinal)))
        {
            snapshot.pictures[entry.FullName] = Load(Read(archive, entry.FullName)!);
        }

        return snapshot;
    }

    /// <summary>
    /// A pasted picture as it was kept.
    ///
    /// One that will not decode still opens, and simply is not drawn. It would be a poor trade to
    /// refuse a whole document, capture and annotations and all, over one damaged paste, and its
    /// bytes are kept so saving does not finish off what might yet be recovered.
    /// </summary>
    static Picture Load(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            return new Picture(png, new Bitmap(stream));
        }
        catch (Exception)
        {
            return new Picture(png, null);
        }
    }

    /// <summary>
    /// Builds a snapshot around a picture that was not captured here, to be saved at
    /// <paramref name="path"/>.
    ///
    /// It goes out through the same writer a save uses, so a snapshot made this way is the same
    /// file in every respect as one the daemon wrote. There is nothing in the format that records
    /// where a picture came from, and there should not be: once it is in, it is a snapshot.
    ///
    /// Not written here, because not every picture brought in is kept. One being converted from
    /// the command line is rendered and done with, and written first, every conversion would leave
    /// another snapshot in the library that nobody asked for.
    /// </summary>
    public static Snapshot Wrap(string path, byte[] originalPng, string? meta)
    {
        using var stream = new MemoryStream(originalPng);
        var bitmap = new Bitmap(stream);

        var document = new SnapshotDocument
        {
            Canvas = new CanvasArea { Width = bitmap.PixelSize.Width, Height = bitmap.PixelSize.Height },
            Layers = [CaptureLayer(bitmap.PixelSize)]
        };

        return new Snapshot(path, document, originalPng, bitmap, meta, written: false);
    }

    /// <summary>
    /// A snapshot with nothing in it, for pasting into.
    ///
    /// Not written anywhere until it is saved. A blank opened and closed again has nothing in it
    /// worth keeping, and writing it straight away, the way an import is, would leave an empty
    /// "untitled" in the library every time somebody changed their mind.
    /// </summary>
    public static Snapshot Blank() => new(
        SnapshotLibrary.FreePath(BlankName),
        new SnapshotDocument { Canvas = new CanvasArea { Width = BlankSize.Width, Height = BlankSize.Height } },
        originalPng: null,
        bitmap: null,
        meta: null,
        written: false);

    /// <summary>
    /// Brings an older document up to the current format.
    ///
    /// One step per version rather than one fix-up for everything older than the current one. Each
    /// step names the version it repairs, so adding a version later cannot silently re-run a fix
    /// that has already been applied.
    ///
    /// Version 1 drew every blur before everything else regardless of where it sat in the layers,
    /// so that a blur could never hide an arrow. Version 2 honours the order instead, which is what
    /// gives moving an object forward or back any meaning. Moving the blurs to the front as the
    /// document is opened reproduces exactly what version 1 drew, so nobody's saved work changes
    /// appearance the first time they open it in a newer build.
    ///
    /// Version 3 gave the canvas an offset, so that it can be cropped in past the capture or pushed
    /// out beyond it. An older document has no offset, and zero is exactly what it meant: the canvas
    /// was the capture. Version 4 added the bands cut out of the picture, and an older document has
    /// none. Neither needs a fix-up, only the version.
    ///
    /// Version 5 made the capture a layer. An older document drew it underneath everything at its
    /// own size and at the origin, so that is where its layer goes: the bottom of the stack, at the
    /// origin, at its own size. A canvas that was not exactly the capture had been sized by hand,
    /// and is recorded as such.
    ///
    /// Version 6 let a picture be cropped, and an older picture has no crop, which is all of it.
    ///
    /// A document from further ahead than this build is left exactly as it is, version and all.
    /// There is nothing here that could repair one, and stamping it back down to this version would
    /// be this build telling a later one that migrations it has never heard of have already run.
    /// </summary>
    /// <param name="capture">The capture's size, or null for a snapshot without one, which has none to migrate into a layer.</param>
    static void Migrate(SnapshotDocument document, Avalonia.PixelSize? capture)
    {
        if (document.Version >= SnapshotDocument.Current)
        {
            return;
        }

        if (document.Version < 2)
        {
            var blurs = document.Layers.OfType<BlurAnnotation>().Cast<Annotation>().ToList();
            var rest = document.Layers.Where(layer => layer is not BlurAnnotation).ToList();

            document.Layers.Clear();
            document.Layers.AddRange(blurs);
            document.Layers.AddRange(rest);
        }

        if (document.Version < 5 && capture is { } size)
        {
            document.Layers.Insert(0, CaptureLayer(size));

            // Cropped or padded by hand, since before now the only canvas nobody had touched was
            // the capture exactly. Left as it is, rather than snapping to the capture the first time
            // anything on it moves.
            var canvas = document.Canvas;

            if (canvas.X != 0 || canvas.Y != 0 || canvas.Width != size.Width || canvas.Height != size.Height)
            {
                CanvasFit.SetByHand(document, new Avalonia.Rect(canvas.X, canvas.Y, canvas.Width, canvas.Height));
            }
        }

        document.Version = SnapshotDocument.Current;
    }

    /// <summary>The capture where it was taken: at the origin, at its own size, facing the way it did.</summary>
    static ImageAnnotation CaptureLayer(Avalonia.PixelSize capture) => new()
    {
        Source = ImageAnnotation.Capture,
        Width = capture.Width,
        Height = capture.Height
    };

    public void SaveAs(string path)
    {
        // Write to a temporary file and move it into place, so an interrupted save cannot leave a
        // half-written snapshot where the original used to be.
        var temporary = path + ".writing";

        // A blank canvas can be the first thing anybody ever saves, before the daemon has made the
        // folder for a capture.
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        using (var file = File.Create(temporary))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            WriteText(archive, "document.json", JsonSerializer.Serialize(Document, Json));

            if (Meta is not null)
            {
                WriteText(archive, "meta.json", Meta);
            }

            // Only the pictures something still stands on. One pasted and then deleted is kept in
            // memory for the undo history's sake, but a saved document has no history to want it.
            // The capture is no exception: somebody who deleted it, quite possibly because of what
            // was in it, is not expecting to hand it over inside the file anyway.
            var used = Document.Layers
                .OfType<ImageAnnotation>()
                .Select(image => image.Source)
                .Distinct(StringComparer.Ordinal);

            foreach (var source in used)
            {
                if (PngOf(source) is { } png)
                {
                    WriteBytes(archive, source, png);
                }
            }
        }

        File.Move(temporary, path, overwrite: true);
        Path = path;
        written = true;
    }

    /// <summary>
    /// Writes the snapshot where it already lives.
    ///
    /// A blank one being saved for the first time takes the next free name instead, if the one it
    /// was given has been taken since. See <see cref="written"/>.
    /// </summary>
    public void Save() => SaveAs(!written && File.Exists(Path) ? SnapshotLibrary.FreePath(BlankName) : Path);

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

    /// <summary>Already compressed by the PNG encoder, so the zip is not asked to try again.</summary>
    static void WriteBytes(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    /// <summary>Decoded pictures are native memory the collector cannot see, so they are released deliberately.</summary>
    public void Dispose()
    {
        foreach (var picture in pictures.Values)
        {
            picture.Bitmap?.Dispose();
        }
    }

    static void WriteText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
    }
}
