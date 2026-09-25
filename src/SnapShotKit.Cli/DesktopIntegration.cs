using System.Diagnostics;

namespace SnapShotKit.Cli;

/// <summary>
/// Gives an AppImage what a package gets from being installed: the two launcher entries, the icon,
/// and the .ssk file type that makes the file manager open snapshots in the editor.
///
/// A package puts these in /usr/share. An AppImage cannot, and its own copies are inside a mount
/// nobody else can see, so setup copies them into the user's data directory with every Exec line
/// rewritten to start the AppImage file. Each entry carries a marker, so revert removes only what
/// setup wrote and never a desktop file somebody made by hand.
/// </summary>
internal static class DesktopIntegration
{
    const string Marker = "X-SnapShotKit-AppImage=true";

    static readonly string[] DesktopFiles = ["snapshotkit-editor.desktop", "snapshotkit-capture.desktop"];

    static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
        ? data
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    /// <summary>The share directory inside the AppImage, laid out as the package lays out /usr/share.</summary>
    static string Bundled => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "share"));

    public static void Install()
    {
        var image = AppImage.File
            ?? throw new InvalidOperationException("Desktop integration is only for an AppImage.");
        var exec = AppImage.DesktopExecQuote(image);

        var applications = Path.Combine(DataHome, "applications");
        Directory.CreateDirectory(applications);

        foreach (var name in DesktopFiles)
        {
            var source = Path.Combine(Bundled, "applications", name);
            if (!File.Exists(source))
            {
                continue;
            }

            // The editor's entry runs the editor binary, which the AppImage reaches through a verb;
            // the capture entry runs the client, whose own verbs AppRun passes straight through.
            var lines = File.ReadAllLines(source)
                .Select(line => line.StartsWith("Exec=snapshotkit-editor", StringComparison.Ordinal)
                    ? $"Exec={exec} editor{line["Exec=snapshotkit-editor".Length..]}"
                    : line.StartsWith("Exec=snapshotkit ", StringComparison.Ordinal)
                        ? $"Exec={exec} {line["Exec=snapshotkit ".Length..]}"
                        : line)
                .Append(Marker);

            File.WriteAllLines(Path.Combine(applications, name), lines);
        }

        foreach (var (source, destination) in Icons())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }

        var mime = Path.Combine(Bundled, "mime", "packages", "snapshotkit.xml");
        if (File.Exists(mime))
        {
            var packages = Path.Combine(DataHome, "mime", "packages");
            Directory.CreateDirectory(packages);
            File.Copy(mime, Path.Combine(packages, "snapshotkit.xml"), overwrite: true);
        }

        RefreshCaches();
        Console.WriteLine($"Launcher entries and the .ssk file type installed for {image}");
    }

    public static void Remove()
    {
        var applications = Path.Combine(DataHome, "applications");
        var ours = DesktopFiles
            .Select(name => Path.Combine(applications, name))
            .Where(path => File.Exists(path) && File.ReadLines(path).Contains(Marker))
            .ToList();

        // No entries of ours means this was never an AppImage setup, and the icon and the file type
        // under the same names belong to somebody else: a package, or a development install.
        if (ours.Count == 0)
        {
            return;
        }

        ours.ForEach(File.Delete);

        foreach (var (_, destination) in Icons())
        {
            File.Delete(destination);
        }

        File.Delete(Path.Combine(DataHome, "mime", "packages", "snapshotkit.xml"));

        RefreshCaches();
        Console.WriteLine("Launcher entries and the .ssk file type removed.");
    }

    /// <summary>Every icon size the AppImage carries, paired with where it goes.</summary>
    static IEnumerable<(string Source, string Destination)> Icons()
    {
        var hicolor = Path.Combine(Bundled, "icons", "hicolor");
        if (!Directory.Exists(hicolor))
        {
            yield break;
        }

        foreach (var size in Directory.GetDirectories(hicolor))
        {
            var source = Path.Combine(size, "apps", "snapshotkit.png");
            if (File.Exists(source))
            {
                yield return (source, Path.Combine(DataHome, "icons", "hicolor", Path.GetFileName(size), "apps", "snapshotkit.png"));
            }
        }
    }

    /// <summary>
    /// The caches that decide what the launcher shows and which application a file opens in. Both
    /// tools are nearly always present, and a desktop without one loses only that cache, so neither
    /// is allowed to fail setup.
    /// </summary>
    static void RefreshCaches()
    {
        TryRun("update-desktop-database", Path.Combine(DataHome, "applications"));
        TryRun("update-mime-database", Path.Combine(DataHome, "mime"));
    }

    static void TryRun(string fileName, string argument)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true };
            startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            process?.StandardOutput.ReadToEnd();
            process?.StandardError.ReadToEnd();
            process?.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not installed.
        }
    }
}
