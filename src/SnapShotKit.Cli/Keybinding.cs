using System.Diagnostics;
using SnapShotKit.Contracts;

namespace SnapShotKit.Cli;

/// <summary>
/// Hands Print over from GNOME to SnapShotKit, and gives it back.
///
/// Two rules govern everything here. The existing custom keybinding list is appended to, never
/// replaced, because clobbering someone's shortcuts is unforgivable. And whatever is changed is
/// recorded so that revert genuinely restores rather than guessing at defaults.
/// </summary>
internal static class Keybinding
{
    const string ShellSchema = "org.gnome.shell.keybindings";
    const string ShellKey = "show-screenshot-ui";
    const string MediaKeysSchema = "org.gnome.settings-daemon.plugins.media-keys";
    const string CustomListKey = "custom-keybindings";
    const string OurPath = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/snapshotkit/";
    const string OurSchema = "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding";

    static string BackupFile => Path.Combine(SnapShotKitPaths.State, "keybinding-backup");

    public static int Install()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the path of this executable.");

        SnapShotKitPaths.EnsureCreated();

        var previous = Get(ShellSchema, ShellKey);
        if (!File.Exists(BackupFile))
        {
            // Only record the first time, or a second run would back up our own empty value and
            // make revert a no-op.
            File.WriteAllText(BackupFile, previous);
        }

        Set(ShellSchema, ShellKey, "[]");

        var existing = ParseList(Get(MediaKeysSchema, CustomListKey));
        if (!existing.Contains(OurPath))
        {
            existing.Add(OurPath);
            Set(MediaKeysSchema, CustomListKey, FormatList(existing));
        }

        Set($"{OurSchema}:{OurPath}", "name", "SnapShotKit capture");
        Set($"{OurSchema}:{OurPath}", "command", $"{executable} capture");
        Set($"{OurSchema}:{OurPath}", "binding", "Print");

        ServiceUnit.Install();

        var extension = InstallExtension();

        Console.WriteLine();
        Console.WriteLine("Print is now bound to SnapShotKit.");
        Console.WriteLine($"  was  : {ShellSchema} {ShellKey} = {previous}");
        Console.WriteLine($"  runs : {executable} capture");
        if (extension)
        {
            Console.WriteLine();

            if (ShellExtension.IsLive)
            {
                Console.WriteLine("The GNOME Shell extension is loaded, so Print also works while a shell menu is open.");
            }
            else
            {
                Console.WriteLine("The GNOME Shell extension is installed but not loaded yet. GNOME only picks up new");
                Console.WriteLine("extensions at login, and Wayland cannot restart the shell in place, so log out and");
                Console.WriteLine("back in when convenient. Print works in the meantime; it just stays silent while a");
                Console.WriteLine("shell menu is open, which is the thing the extension fixes.");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Undo with: snapshotkit setup --revert");
        return 0;
    }

    public static int Revert()
    {
        var existing = ParseList(Get(MediaKeysSchema, CustomListKey));
        if (existing.Remove(OurPath))
        {
            Set(MediaKeysSchema, CustomListKey, FormatList(existing));
        }

        // Fall back to GNOME's default rather than leaving Print dead if the backup is missing.
        var previous = File.Exists(BackupFile) ? File.ReadAllText(BackupFile).Trim() : "['Print']";
        Set(ShellSchema, ShellKey, previous);

        if (File.Exists(BackupFile))
        {
            File.Delete(BackupFile);
        }

        ServiceUnit.Remove();

        try
        {
            ShellExtension.Remove();
            Console.WriteLine("GNOME Shell extension removed.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not remove the shell extension: {exception.Message}");
        }

        Console.WriteLine($"Print restored to GNOME: {ShellSchema} {ShellKey} = {previous}");
        return 0;
    }

    /// <summary>
    /// Installs the shell extension, and treats failure as a downgrade rather than an error: the
    /// settings-daemon binding already works, and the extension only widens where it fires.
    /// </summary>
    static bool InstallExtension()
    {
        try
        {
            ShellExtension.Install();
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not install the GNOME Shell extension: {exception.Message}");
            Console.Error.WriteLine("Print will still work, but not while a shell menu is open.");
            return false;
        }
    }

    static List<string> ParseList(string value)
    {
        // gsettings prints an empty list as "@as []" and a populated one as "['/a/', '/b/']".
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

    static string Get(string schema, string key) => Run("get", schema, key).Trim();

    static void Set(string schema, string key, string value) => Run("set", schema, key, value);

    static string Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("gsettings") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not run gsettings.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0
            ? output
            : throw new InvalidOperationException($"gsettings {string.Join(' ', arguments)} failed: {error.Trim()}");
    }
}
