namespace SnapShotKit.Cli;

/// <summary>
/// What changes when SnapShotKit runs from an AppImage.
///
/// An AppImage is mounted somewhere new every time it starts, and unmounted as soon as the process
/// that started it exits. Every path this process can see of itself is inside that mount, so
/// anything setup writes down for later (the keybinding's command, the unit's ExecStart, a desktop
/// entry) has to name the AppImage file and a verb its AppRun understands instead. Persisting a
/// path from inside the mount produces something that works until the next login and never again.
/// </summary>
internal static class AppImage
{
    /// <summary>
    /// The AppImage file, when running from one. Its runtime sets APPIMAGE for everything it starts,
    /// and checking that the file exists keeps a stray variable in someone's shell from counting.
    /// </summary>
    public static string? File =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } path && System.IO.File.Exists(path)
            ? path
            : null;

    /// <summary>
    /// Quoted for g_shell_parse_argv, which is how GNOME reads a custom keybinding's command:
    /// single quotes, with any single quote inside closed, escaped and reopened.
    /// </summary>
    public static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''")}'";

    /// <summary>
    /// Quoted for a systemd ExecStart. Inside double quotes a backslash and a quote need escaping,
    /// and systemd expands % specifiers and $ variables even there, so those are doubled.
    /// </summary>
    public static string SystemdQuote(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%").Replace("$", "$$")}\"";

    /// <summary>
    /// Quoted for a desktop entry's Exec key, whose rules differ again: four characters are escaped
    /// inside double quotes, and a literal percent sign is always doubled.
    /// </summary>
    public static string DesktopExecQuote(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("`", "\\`")
            .Replace("$", "\\$")
            .Replace("%", "%%");

        return $"\"{escaped}\"";
    }
}
