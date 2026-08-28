using System.Diagnostics;

namespace SnapShotKit.Cli;

/// <summary>
/// Installs the GNOME Shell extension.
///
/// The extension exists because a keybinding owned by gnome-settings-daemon is not delivered while
/// GNOME Shell holds a keyboard grab, which it does whenever a panel or extension menu is open.
/// Registered inside the shell, the same shortcut fires there too.
///
/// Installing is only half of it. A running GNOME Shell does not rescan its extension directories,
/// so on Wayland, where the shell cannot be restarted in place, a newly installed extension stays
/// dormant until the next login. That is why SnapShotKit installs the settings-daemon binding as well:
/// it works immediately, and the extension takes the shortcut over the first time it runs.
/// </summary>
internal static class ShellExtension
{
    public const string Uuid = "snapshotkit@dvdstelt.github.io";

    static string InstalledPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
            ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
        "gnome-shell", "extensions", Uuid);

    public static bool IsInstalled => File.Exists(Path.Combine(InstalledPath, "metadata.json"));

    /// <summary>True when the running shell has actually loaded it, rather than merely having it on disk.</summary>
    public static bool IsLive
    {
        get
        {
            try
            {
                return Run("gnome-extensions", "list").Split('\n').Any(line => line.Trim() == Uuid);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Install()
    {
        var source = Locate();

        Directory.CreateDirectory(InstalledPath);

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(InstalledPath, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        CompileSchemas();

        // Enabling through gnome-extensions fails until the shell has seen it, so the uuid goes
        // straight into the shell's own list. It then enables itself at the next login.
        AddToEnabledExtensions();
    }

    public static void Remove()
    {
        RemoveFromEnabledExtensions();

        if (Directory.Exists(InstalledPath))
        {
            Directory.Delete(InstalledPath, recursive: true);
        }
    }

    static void CompileSchemas()
    {
        var schemas = Path.Combine(InstalledPath, "schemas");

        if (Directory.Exists(schemas))
        {
            Run("glib-compile-schemas", schemas);
        }
    }

    static void AddToEnabledExtensions()
    {
        var current = ParseList(Run("gsettings", "get", "org.gnome.shell", "enabled-extensions"));

        if (current.Contains(Uuid))
        {
            return;
        }

        current.Add(Uuid);
        Run("gsettings", "set", "org.gnome.shell", "enabled-extensions", FormatList(current));
    }

    static void RemoveFromEnabledExtensions()
    {
        var current = ParseList(Run("gsettings", "get", "org.gnome.shell", "enabled-extensions"));

        if (current.Remove(Uuid))
        {
            Run("gsettings", "set", "org.gnome.shell", "enabled-extensions", FormatList(current));
        }
    }

    static List<string> ParseList(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.StartsWith("@as", StringComparison.Ordinal))
        {
            trimmed = trimmed[3..].Trim();
        }

        trimmed = trimmed.Trim('[', ']').Trim();

        return trimmed.Length == 0
            ? []
            : [.. trimmed.Split(',').Select(item => item.Trim().Trim('\'')).Where(item => item.Length > 0)];
    }

    static string FormatList(IEnumerable<string> items) => $"[{string.Join(", ", items.Select(item => $"'{item}'"))}]";

    static string Locate()
    {
        if (Environment.GetEnvironmentVariable("SNAPSHOTKIT_EXTENSION") is { Length: > 0 } configured)
        {
            return configured;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "extension", Uuid);
        if (Directory.Exists(beside))
        {
            return beside;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "extension", Uuid);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find the {Uuid} source directory.");
    }

    static string Run(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not run {fileName}.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0
            ? output
            : throw new InvalidOperationException($"{fileName} {string.Join(' ', arguments)} failed: {error.Trim()}");
    }
}
