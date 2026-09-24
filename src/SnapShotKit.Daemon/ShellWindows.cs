using SnapShotKit.Contracts;
using Tmds.DBus.Protocol;

namespace SnapShotKit.Daemon;

/// <summary>
/// Where the windows are, asked of the shell extension.
///
/// Wayland tells a client nothing about any window but its own, so the only thing that knows where
/// the windows are is the compositor, and the only way to ask it is from inside: the extension
/// answers on the shell's own bus connection. Without the extension there is no answer, which is
/// not a failure. The overlay then offers what it always has, a region drawn by hand, and picking
/// a window is simply not among the things on offer.
///
/// Asked straight after the frame is taken, so the rectangles describe the same moment the picture
/// does. A window moved a second later is a window in the wrong place on a frozen screen.
/// </summary>
public static class ShellWindows
{
    /// <summary>
    /// How long the shell gets. It answers in a millisecond or two when it is going to answer at
    /// all, and the overlay is waiting behind this, so a shell busy with something else costs the
    /// window picking rather than the capture.
    /// </summary>
    static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(300);

    readonly record struct Box(int X, int Y, int Width, int Height);

    /// <summary>
    /// The windows on the captured monitor in frame pixels, topmost first, each clipped to the
    /// frame. Empty when the extension is not there to ask.
    /// </summary>
    public static async Task<IReadOnlyList<CaptureRegion>> InFrameAsync(DBusConnection connection,
        int frameWidth, int frameHeight, CancellationToken cancellationToken = default)
    {
        try
        {
            var (windows, monitors) = await connection
                .CallMethodAsync(CreateMessage(), static (Message message, object? _) => Read(message), null)
                .WaitAsync(Patience, cancellationToken);

            return Place(windows, monitors, frameWidth, frameHeight);
        }
        catch (Exception exception) when (exception is DBusErrorReplyException or DBusMessageException or DBusReadException or TimeoutException)
        {
            Log.Info($"No window rectangles from the shell ({exception.Message}), so windows cannot be picked");
            return [];
        }

        MessageBuffer CreateMessage()
        {
            using var writer = connection.GetMessageWriter();

            writer.WriteMethodCallHeader(
                SnapShotKitDBus.ShellService,
                SnapShotKitDBus.ShellObjectPath,
                SnapShotKitDBus.ShellInterface,
                SnapShotKitDBus.ShellWindows);

            return writer.CreateMessage();
        }
    }

    static (List<Box> Windows, List<Box> Monitors) Read(Message message)
    {
        var reader = message.GetBodyReader();
        return (ReadBoxes(ref reader), ReadBoxes(ref reader));
    }

    static List<Box> ReadBoxes(ref Reader reader)
    {
        List<Box> boxes = [];
        var end = reader.ReadArrayStart(DBusType.Struct);

        while (reader.HasNext(end))
        {
            reader.AlignStruct();
            boxes.Add(new Box(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
        }

        return boxes;
    }

    /// <summary>
    /// Turns the shell's rectangles into the frame's.
    ///
    /// The shell measures in its own logical pixels across every monitor at once; the frame is one
    /// monitor in device pixels. Which monitor is not something the capture stream says, so it is
    /// taken to be the first whose shape matches the frame, and the shell lists the primary one
    /// first. The scale between the two then follows from the widths, which also covers fractional
    /// scaling without anybody having to say what the factor is.
    /// </summary>
    static IReadOnlyList<CaptureRegion> Place(List<Box> windows, List<Box> monitors, int frameWidth, int frameHeight)
    {
        var shape = (double)frameWidth / frameHeight;

        var monitor = monitors.FirstOrDefault(candidate => candidate is { Width: > 0, Height: > 0 }
            && Math.Abs((double)candidate.Width / candidate.Height - shape) < 0.01);

        if (monitor.Width == 0)
        {
            return [];
        }

        var scale = (double)frameWidth / monitor.Width;
        List<CaptureRegion> placed = [];

        foreach (var window in windows)
        {
            var left = Math.Max((int)Math.Round((window.X - monitor.X) * scale), 0);
            var top = Math.Max((int)Math.Round((window.Y - monitor.Y) * scale), 0);
            var right = Math.Min((int)Math.Round((window.X + window.Width - monitor.X) * scale), frameWidth);
            var bottom = Math.Min((int)Math.Round((window.Y + window.Height - monitor.Y) * scale), frameHeight);

            // One on another monitor, or a sliver of one hanging onto this one, is not worth offering.
            if (right - left >= 16 && bottom - top >= 16)
            {
                placed.Add(new CaptureRegion(left, top, right - left, bottom - top));
            }
        }

        return placed;
    }
}
