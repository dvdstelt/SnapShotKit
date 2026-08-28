using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace SnapShotKit.Editor;

/// <summary>
/// A snapshot as a list shows it.
///
/// The thumbnail arrives later than the row does, which is the entire point: the list appears
/// immediately from file metadata and fills in as pictures are decoded, rather than making the user
/// wait for three hundred decodes before seeing anything.
/// </summary>
public sealed class SnapshotItem(SnapshotEntry entry) : INotifyPropertyChanged
{
    Bitmap? thumbnail;

    public SnapshotEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string Summary => Entry.Summary;

    /// <summary>The name without its extension, which is all a tile has room for.</summary>
    public string ShortName => Path.GetFileNameWithoutExtension(Entry.Name);

    /// <summary>When it was taken, to the second, which is what tells two captures of the same thing apart.</summary>
    public string Time => Entry.Modified.ToString("HH:mm:ss");

    /// <summary>
    /// What is drawn on the capture, in words: "no objects", "blur, 2 arrows".
    ///
    /// Read from the document in the background, the same way the picture is. It answers the
    /// question a name cannot — whether this is the shot already marked up or the untouched one.
    /// </summary>
    public string Contents
    {
        get;
        private set
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Contents)));
        }
    } = string.Empty;

    public Bitmap? Thumbnail
    {
        get => thumbnail;
        private set
        {
            thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadThumbnailAsync(ThumbnailCache cache, CancellationToken cancellationToken)
    {
        var loaded = await cache.GetAsync(Entry, cancellationToken);

        if (loaded is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // The binding is read on the UI thread, so the change has to be raised there.
        await Dispatcher.UIThread.InvokeAsync(() => Thumbnail = loaded);
    }

    /// <summary>Reads the document to find out what has been drawn on the capture.</summary>
    public async Task LoadContentsAsync(SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                var described = await Task.Run(() => SnapshotLibrary.DescribeContents(Entry), cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => Contents = described);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The list moved on. Nothing to report.
        }
        catch
        {
            // A capture that cannot be read still gets a row; the failure surfaces on opening it.
        }
    }

    /// <summary>Builds the rows for a folder and starts filling in their pictures.</summary>
    public static List<SnapshotItem> Build(IEnumerable<SnapshotEntry> entries, ThumbnailCache cache,
        CancellationToken cancellationToken)
    {
        var items = entries.Select(entry => new SnapshotItem(entry)).ToList();

        foreach (var item in items)
        {
            _ = item.LoadThumbnailAsync(cache, cancellationToken);
        }

        return items;
    }
}
