using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace SnapShotKit.Editor;

/// <summary>
/// Thumbnails for the snapshot lists.
///
/// Decoding a few hundred full captures to fill a list is not viable, so thumbnails are generated
/// once and kept on disk. This is the one part of SnapShotKit that genuinely belongs in
/// XDG_CACHE_HOME: every thumbnail can be rebuilt from its snapshot, so losing the whole folder
/// costs time and nothing else.
///
/// The cache key includes the snapshot's timestamp and length, so editing a snapshot produces a new
/// key rather than a stale picture. Old keys are pruned rather than tracked.
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    /// <summary>Long edge in pixels. Large enough to recognise a screenshot, small enough to decode instantly.</summary>
    public const int MaxSize = 320;

    const int KeepAtMost = 600;

    static readonly WebpEncoder Encoder = new() { Quality = 78 };

    static string Folder { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } cache
            ? cache
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "snapshotkit", "thumbnails");

    // Three at a time: enough to fill a visible list quickly, few enough that opening the library
    // does not saturate the machine decoding pictures nobody has scrolled to.
    readonly SemaphoreSlim gate = new(3);
    readonly Dictionary<string, Bitmap> memory = [];
    readonly Lock guard = new();

    public async Task<Bitmap?> GetAsync(SnapshotEntry entry, CancellationToken cancellationToken = default)
    {
        var key = KeyFor(entry);

        lock (guard)
        {
            if (memory.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var acquired = false;

        try
        {
            // Waiting for the gate can itself be cancelled, and these calls are fire-and-forget:
            // an exception escaping here lands in a discarded task, so the wait sits inside the
            // same try as the work.
            await gate.WaitAsync(cancellationToken);
            acquired = true;

            return await Task.Run(() => Load(entry, key, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // A snapshot that cannot be read gets no thumbnail. The list still shows its name, and
            // the failure surfaces when the user tries to open it.
            return null;
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }
        }
    }

    Bitmap? Load(SnapshotEntry entry, string key, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Folder, key + ".webp");

        if (!File.Exists(path))
        {
            Generate(entry, path, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(path))
        {
            return null;
        }

        var bitmap = new Bitmap(path);

        lock (guard)
        {
            // Another caller may have finished first; keep theirs so the list holds one instance.
            if (memory.TryGetValue(key, out var existing))
            {
                bitmap.Dispose();
                return existing;
            }

            memory[key] = bitmap;
        }

        return bitmap;
    }

    static void Generate(SnapshotEntry entry, string destination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(entry.Path);
        var original = archive.GetEntry("original.png");

        if (original is null)
        {
            return;
        }

        using var stream = original.Open();
        using var image = Image.Load(stream);

        cancellationToken.ThrowIfCancellationRequested();

        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(MaxSize, MaxSize),
            Mode = ResizeMode.Max
        }));

        Directory.CreateDirectory(Folder);

        // Write beside and move, so an interrupted generation cannot leave a truncated thumbnail
        // that would then be treated as valid forever.
        var temporary = destination + ".writing";
        image.Save(temporary, Encoder);
        File.Move(temporary, destination, overwrite: true);
    }

    static string KeyFor(SnapshotEntry entry)
    {
        var identity = $"{entry.Path}|{entry.Modified.Ticks}|{entry.Size}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
    }

    /// <summary>
    /// Drops the oldest thumbnails when the folder grows past its cap.
    ///
    /// Editing a snapshot changes its key and orphans the old thumbnail, so the folder grows without
    /// this. Pruning by age is enough: anything still in use is regenerated on the next look.
    /// </summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            var files = new DirectoryInfo(Folder).GetFiles("*.webp");

            foreach (var file in files.OrderByDescending(file => file.LastWriteTimeUtc).Skip(KeepAtMost))
            {
                file.Delete();
            }
        }
        catch
        {
            // Housekeeping. Nothing depends on it succeeding.
        }
    }

    public void Dispose()
    {
        lock (guard)
        {
            foreach (var bitmap in memory.Values)
            {
                bitmap.Dispose();
            }

            memory.Clear();
        }

        gate.Dispose();
    }
}
