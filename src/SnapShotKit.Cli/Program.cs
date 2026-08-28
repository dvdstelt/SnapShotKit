using SnapShotKit.Cli;
using SnapShotKit.Contracts;
using Tmds.DBus.Protocol;

// snapshotkit: the thin client. "capture" is on the hot path and does nothing but forward a D-Bus call,
// which is why this is compiled ahead of time.

return args.Length == 0 ? Usage() : args[0] switch
{
    "capture" => await CaptureAsync(Delay(args)),
    "status" => await StatusAsync(),
    "snapshots" => Snapshots(),
    "setup" when args.Contains("--revert") => Keybinding.Revert(),
    "setup" => Keybinding.Install(),
    "--help" or "-h" or "help" => Usage(),
    var unknown => Fail($"Unknown command '{unknown}'.")
};

/// <summary>
/// Seconds to wait before capturing.
///
/// A delay is the only way to capture something that closes when you touch the keyboard, which
/// includes every GNOME Shell menu: while one is open the shell holds a keyboard grab and
/// gnome-settings-daemon never sees the shortcut at all.
/// </summary>
static TimeSpan Delay(string[] args)
{
    var index = Array.IndexOf(args, "--after");

    return index >= 0 && index + 1 < args.Length && double.TryParse(args[index + 1], out var seconds)
        ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 60))
        : TimeSpan.Zero;
}

static async Task<int> CaptureAsync(TimeSpan delay)
{
    try
    {
        if (delay > TimeSpan.Zero)
        {
            for (var remaining = (int)Math.Ceiling(delay.TotalSeconds); remaining > 0; remaining--)
            {
                Console.Write($"\rCapturing in {remaining}s... ");
                await Task.Delay(1000);
            }

            Console.Write("\r                        \r");
        }

        using var client = await DaemonClient.ConnectAsync();
        Console.WriteLine(await client.CaptureAsync());
        return 0;
    }
    catch (DBusErrorReplyException exception) when (exception.ErrorName == "org.freedesktop.DBus.Error.ServiceUnknown")
    {
        return Fail("snapshotkitd is not running. Start it with: systemctl --user start snapshotkitd");
    }
    catch (Exception exception)
    {
        return Fail(exception.Message);
    }
}

static async Task<int> StatusAsync()
{
    try
    {
        using var client = await DaemonClient.ConnectAsync();
        var status = await client.StatusAsync();

        var fast = status.TryGetValue("fast", out var fastValue) && fastValue.GetBool();

        Console.WriteLine($"  backend      : {Read(status, "backend")}{(fast ? "  (fast path, ~35 ms)" : "  (fallback, ~700 ms)")}");
        Console.WriteLine($"  stream size  : {Read(status, "stream_size")}");
        Console.WriteLine($"  exports      : {Read(status, "exports")}");
        Console.WriteLine($"  snapshots    : {Read(status, "snapshots")}");

        var held = status.TryGetValue("session_held", out var session) && session.GetBool();
        Console.WriteLine($"  session      : {(held ? "held, so the desktop shows a sharing indicator" : "released, no sharing indicator")}");

        if (status.TryGetValue("consent_not_remembered", out var consent) && consent.GetBool())
        {
            Console.WriteLine();
            Console.WriteLine("  Screen sharing consent was not remembered, so restarting will ask again.");
            Console.WriteLine("  Tick the remember box in the dialog to make it stick.");
        }

        if (Read(status, "last_error") is { Length: > 0 } error)
        {
            Console.WriteLine();
            Console.WriteLine($"  last error   : {error}");
        }

        return 0;
    }
    catch (DBusErrorReplyException exception) when (exception.ErrorName == "org.freedesktop.DBus.Error.ServiceUnknown")
    {
        return Fail("snapshotkitd is not running. Start it with: systemctl --user start snapshotkitd");
    }
    catch (Exception exception)
    {
        return Fail(exception.Message);
    }
}

/// <summary>
/// Lists the working documents. They live out of the way in the data directory, which is right for
/// files the user should rarely think about, but it does mean something has to be able to show them
/// until the editor grows a library view.
/// </summary>
static int Snapshots()
{
    if (!Directory.Exists(SnapShotKitPaths.Snapshots))
    {
        Console.WriteLine("No snapshots yet.");
        return 0;
    }

    var files = new DirectoryInfo(SnapShotKitPaths.Snapshots)
        .GetFiles("*.ssk")
        .OrderBy(file => file.Name)
        .ToList();

    if (files.Count == 0)
    {
        Console.WriteLine($"No snapshots in {SnapShotKitPaths.Snapshots}");
        return 0;
    }

    foreach (var file in files)
    {
        Console.WriteLine($"  {file.Name,-22} {file.Length / 1024.0 / 1024.0,6:F2} MB   {file.LastWriteTime:yyyy-MM-dd HH:mm}");
    }

    Console.WriteLine();
    Console.WriteLine($"{files.Count} snapshot(s) in {SnapShotKitPaths.Snapshots}");
    Console.WriteLine("Delete any you do not want; nothing else refers to them.");
    return 0;
}

static string Read(Dictionary<string, VariantValue> status, string key)
    => status.TryGetValue(key, out var value) ? value.GetString() : string.Empty;

static int Usage()
{
    Console.WriteLine("snapshotkit - screenshot capture");
    Console.WriteLine();
    Console.WriteLine("  snapshotkit capture          capture the screen and choose a region");
    Console.WriteLine("  snapshotkit capture --after 5  wait 5 seconds first, for menus that close on a keypress");
    Console.WriteLine("  snapshotkit status           show which capture path is in use");
    Console.WriteLine("  snapshotkit snapshots        list saved .ssk working documents");
    Console.WriteLine("  snapshotkit setup            bind Print to SnapShotKit");
    Console.WriteLine("  snapshotkit setup --revert   give Print back to GNOME");
    Console.WriteLine();
    Console.WriteLine($"Exports:   {SnapShotKitPaths.Exports}");
    Console.WriteLine($"Snapshots: {SnapShotKitPaths.Snapshots}");
    return 0;
}

static int Fail(string message)
{
    Console.Error.WriteLine($"snapshotkit: {message}");
    return 1;
}
