using System.Text.Json;
using SnapShotKit.Contracts;

namespace SnapShotKit.Editor;

/// <summary>
/// The handful of things the editor remembers between sessions.
///
/// A small JSON file rather than a database, deliberately. There is nothing here to query and
/// nothing relational: it is read once when a window opens and rewritten when a setting changes, and a
/// database would buy indexes and transactions this has no use for, at the price of a dependency,
/// a schema and its migrations. The library index is where a database earns its place, because
/// searching hundreds of snapshots is a real question to ask; how the last export was written is
/// not.
///
/// It lives in the state directory rather than beside the snapshots, because it is not data the
/// user would miss. Losing it costs a few clicks, which is the test for what belongs there.
///
/// Nothing here is important enough to interrupt anyone over. A file that cannot be read leaves the
/// editor with its defaults, and one that cannot be written leaves the session as it was: both are
/// what would have happened before any of this existed.
/// </summary>
public sealed class EditorState
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// How the last export was written, which is how the next one is offered.
    ///
    /// Remembered because exporting is rarely done once. Somebody who has settled on lossless WebP
    /// beside the original wants that again tomorrow, and a dialog that opens on the defaults every
    /// time is one they have to correct every time.
    /// </summary>
    public ExportSettings Export { get; set; } = new();

    /// <summary>
    /// How the last page was set up for printing: the printer, the paper, which way up, the margins.
    /// Those belong to the printer on the desk rather than to any one picture, so they are the same
    /// next time.
    /// </summary>
    public PrintSettings Print { get; set; } = new();

    /// <summary>What the strip of recent captures along the bottom of the window was told to do.</summary>
    public StripSettings Strip { get; set; } = new();

    /// <summary>Reads what was remembered, or hands back the defaults when there is nothing to read.</summary>
    public static EditorState Load()
    {
        try
        {
            return File.Exists(SnapShotKitPaths.EditorStateFile)
                ? JsonSerializer.Deserialize<EditorState>(File.ReadAllText(SnapShotKitPaths.EditorStateFile), Json) ?? new EditorState()
                : new EditorState();
        }
        catch (Exception)
        {
            // Unreadable, half-written, or written by a version that meant something else by it.
            // Starting fresh is the same outcome as a first run, which is not a failure.
            return new EditorState();
        }
    }

    /// <summary>Records how a page was set up for printing, and writes it out.</summary>
    public void RememberPrint(PrintSettings settings)
    {
        Print = settings;
        Save();
    }

    /// <summary>Records how an export was written, and writes it out.</summary>
    public void RememberExport(ExportSettings settings)
    {
        Export = settings;
        Save();
    }

    /// <summary>
    /// Changes what the strip remembers, and writes it out.
    ///
    /// Applied to what is on disk rather than to what this window read when it opened. Two editor
    /// windows each holding their own copy would otherwise undo each other: a capture taken off
    /// the strip in one would come back the moment the other pinned something. For an export
    /// setting the last window winning is right; for a list, it loses the other window's work.
    /// </summary>
    public void ChangeStrip(Action<StripSettings> change)
    {
        var current = Load().Strip;
        change(current);

        // Captures deleted since, or deleted by another window, are dropped rather than carried
        // forward, so the file does not grow by a line for every capture ever pinned.
        current.PinnedCaptures.RemoveAll(path => !File.Exists(path));
        current.RemovedCaptures.RemoveAll(path => !File.Exists(path));

        Strip = current;
        Save();
    }

    /// <summary>
    /// Writes the file, or does not.
    ///
    /// Through a temporary file moved into place, so an interrupted write cannot leave half a file
    /// where a whole one was. Two editor windows both saving means the last one wins, which for a
    /// remembered export setting is the right answer and not worth a lock.
    /// </summary>
    void Save()
    {
        try
        {
            Directory.CreateDirectory(SnapShotKitPaths.State);

            var temporary = SnapShotKitPaths.EditorStateFile + ".writing";

            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
            File.Move(temporary, SnapShotKitPaths.EditorStateFile, overwrite: true);
        }
        catch (Exception)
        {
            // A read-only home, a full disk, or a directory somebody removed. None of it is worth
            // an interruption: the session carries on with what it has, and forgets it afterwards.
        }
    }
}

/// <summary>
/// What the strip of recent captures has been told.
///
/// Captures are named by path, which is what the library knows them by. A capture saved under a
/// new name is a new file, and starts out neither pinned nor removed, which is what a new file is.
/// </summary>
public sealed class StripSettings
{
    /// <summary>
    /// Whether the strip stays up, taking its share of the window, or waits below the edge for the
    /// pointer. Off to begin with: the picture is what the window is for, and the strip is one
    /// movement of the mouse away.
    /// </summary>
    public bool Pinned { get; set; }

    /// <summary>Captures kept at the front of the strip however many newer ones arrive, in the order they were pinned.</summary>
    public List<string> PinnedCaptures { get; set; } = [];

    /// <summary>
    /// Captures taken off the strip without being deleted. Still in the library, and back on the
    /// strip as soon as they are opened from it.
    /// </summary>
    public List<string> RemovedCaptures { get; set; } = [];
}
