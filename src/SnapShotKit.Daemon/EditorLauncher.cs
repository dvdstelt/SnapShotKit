using System.Diagnostics;

namespace SnapShotKit.Daemon;

/// <summary>
/// Opens a snapshot in the editor.
///
/// Fire and forget: the editor is a standalone tool with its own lifetime, and the daemon has no
/// business waiting on it. A capture is finished the moment the snapshot is on disk.
/// </summary>
public static class EditorLauncher
{
    /// <param name="snapshotPath">The snapshot to open, or null to open the library instead.</param>
    public static bool TryOpen(string? snapshotPath = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo(Locate()) { UseShellExecute = false };

            if (snapshotPath is not null)
            {
                startInfo.ArgumentList.Add(snapshotPath);
            }

            Process.Start(startInfo);
            return true;
        }
        catch (Exception exception)
        {
            // The snapshot is already written, so a missing editor is worth reporting but not worth
            // failing the capture over.
            Log.Warn($"Could not open the editor: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Shows a folder in whatever the desktop uses for folders.
    ///
    /// Through xdg-open rather than a hardcoded file manager, which is the whole point of it: the
    /// daemon has no business knowing whether this desktop runs Files, Dolphin or something else.
    /// </summary>
    public static bool TryOpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            var startInfo = new ProcessStartInfo("xdg-open")
            {
                UseShellExecute = false,
                // The handler it launches can outlive us and would otherwise hold these open.
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn($"Could not open {path}: {exception.Message}");
            return false;
        }
    }

    static string Locate()
    {
        const string fileName = "snapshotkit-editor";

        if (Environment.GetEnvironmentVariable("SNAPSHOTKIT_EDITOR") is { Length: > 0 } configured)
        {
            return configured;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(beside))
        {
            return beside;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent?.Name ?? "Debug";
        var framework = directory.Name;

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "SnapShotKit.Editor", "bin", configuration, framework, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {fileName}. Build src/SnapShotKit.Editor.");
    }
}
