namespace SnapShotKit.Contracts;

/// <summary>
/// Where SnapShotKit is allowed to write. The rule that matters: nothing lands in the user's Pictures
/// folder except captures they deliberately kept.
/// </summary>
public static class SnapShotKitPaths
{
    /// <summary>
    /// Exports: the images the user deliberately kept. The only directory they are expected to
    /// browse, which is why nothing else is allowed to write here.
    /// </summary>
    public static string Exports { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "snapshotkit");

    /// <summary>
    /// Snapshots: `.ssk` working documents.
    ///
    /// XDG_DATA_HOME rather than Pictures, because these are application data, not photographs, and
    /// a folder of them would bury the images the user actually wants. Not cache either: a snapshot
    /// cannot be regenerated, so losing one loses work. It stays an ordinary folder so anyone can
    /// open it and delete from it, and so backups already cover it.
    /// </summary>
    public static string Snapshots { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
            ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
        "snapshotkit", "snapshots");

    /// <summary>Restore token and library index. State, not data: losing it costs a consent dialog, not a capture.</summary>
    public static string State { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state
            ? state
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"),
        "snapshotkit");

    /// <summary>
    /// Transient frames. This is tmpfs, so it is RAM and is cleared on logout. The Screenshot portal
    /// fallback insists on writing to disk, and this is where those files are moved to immediately.
    /// </summary>
    public static string Runtime { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(), "snapshotkit");

    public static string RestoreTokenFile => Path.Combine(State, "screencast.token");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Exports);
        Directory.CreateDirectory(Snapshots);
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Runtime);
    }
}
