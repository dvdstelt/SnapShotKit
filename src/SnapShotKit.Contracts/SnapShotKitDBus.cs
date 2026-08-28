namespace SnapShotKit.Contracts;

/// <summary>The wire contract between the daemon and the CLI. Both sides must agree, so it lives in neither.</summary>
public static class SnapShotKitDBus
{
    public const string ServiceName = "org.snapshotkit.Daemon";
    public const string ObjectPath = "/org/snapshotkit/Daemon";
    public const string Interface = "org.snapshotkit.Daemon";

    public const string Capture = "Capture";
    public const string CaptureFullScreen = "CaptureFullScreen";
    public const string Status = "Status";

    /// <summary>Capture after a delay, for menus and hovers that close the moment a key is touched.</summary>
    public const string CaptureDelayed = "CaptureDelayed";

    /// <summary>Open the editor on the library.</summary>
    public const string OpenEditor = "OpenEditor";

    /// <summary>Open the snapshots folder in the file manager.</summary>
    public const string OpenSnapshots = "OpenSnapshots";
}

/// <summary>Which capture path the daemon is actually using. Surfaced by Status so a silent fall back to the slow path is visible.</summary>
public enum CaptureBackend
{
    /// <summary>Nothing works. Capture will fail.</summary>
    Unavailable,

    /// <summary>The fast path: a parked PipeWire stream, roughly 35 ms per capture.</summary>
    ScreenCast,

    /// <summary>The fallback: the Screenshot portal, roughly 700 ms and a forced disk write.</summary>
    ScreenshotPortal
}

/// <summary>A rectangle in captured image pixels.</summary>
public readonly record struct CaptureRegion(int X, int Y, int Width, int Height);
