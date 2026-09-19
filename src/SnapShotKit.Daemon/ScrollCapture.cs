using System.Diagnostics;
using SnapShotKit.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SnapShotKit.Daemon;

/// <summary>
/// A capture of something taller than the screen: the region is watched while the user scrolls it,
/// and what passes through is joined into one picture.
///
/// The user does the scrolling because nothing else can. Wayland gives a client no way to send a
/// wheel event to another client's window, and the portal that can ask for that asks for control of
/// the whole desktop's input, which is a great deal to grant a screenshot tool for the sake of not
/// turning a wheel.
///
/// There is nothing on screen while it runs, and that is deliberate too. Nothing can be drawn over
/// the live desktop here, and the one thing that could say "recording", a notification, would slide
/// down over the top of the screen and be captured along with the page. So the overlay says what is
/// about to happen before it closes, and the end is either asked for, by pressing the capture key
/// again, or noticed: a page that has been scrolled and then left alone for a few seconds is a
/// page somebody has finished with.
/// </summary>
public static class ScrollCapture
{
    /// <summary>How long between looks. Often enough that an ordinary scroll moves well under a screenful between two of them.</summary>
    static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(90);

    /// <summary>How long a page that has been scrolled may sit still before it is taken to be finished.</summary>
    static readonly TimeSpan SettledAfter = TimeSpan.FromSeconds(3.5);

    /// <summary>How long to wait for the first scroll before concluding there is not going to be one.</summary>
    static readonly TimeSpan NeverStartedAfter = TimeSpan.FromSeconds(20);

    /// <summary>However it is going, it ends here, so a forgotten capture cannot hold the capture key for good.</summary>
    static readonly TimeSpan AtMost = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Watches the region until it is told to stop or the scrolling does, and returns the picture.
    /// </summary>
    /// <param name="first">The frame the overlay was shown, which is the one frame certain not to have the overlay in it.</param>
    /// <param name="stop">Completed when the capture key is pressed again.</param>
    public static async Task<Image<Rgb24>> RunAsync(CaptureEngine engine, CaptureResult first, CaptureRegion region,
        Task stop, CancellationToken cancellationToken)
    {
        var area = Clamp(region, first.Width, first.Height);
        var stitcher = new ScrollStitcher(area.Width, area.Height);

        stitcher.Add(Cut(first, area, null));

        // A frame the stitcher did not keep is a buffer to take the next one into. Most frames are
        // that, since most of the time nothing is moving, and at ten a second a fresh several
        // megabytes each would be a steady stream of garbage for nothing.
        byte[]? spare = null;

        var clock = Stopwatch.StartNew();
        var lastGrowth = TimeSpan.Zero;
        var outcomes = new Dictionary<Stitched, int>();

        // The overlay has only just gone, and the compositor may take a frame or two to take it off
        // the screen. A frame with the overlay in it would only be one that matches nothing, but
        // there is no reason to start by looking at several of those.
        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);

        while (!stop.IsCompleted && clock.Elapsed < AtMost)
        {
            var capture = await engine.CaptureAsync(cancellationToken);

            // A screen that has changed shape under the capture, a monitor unplugged or a
            // resolution changed, is not one the region means anything on any more.
            if (capture.Width != first.Width || capture.Height != first.Height)
            {
                Log.Warn("The screen changed size during a scrolling capture, so it ends here");
                break;
            }

            var frame = Cut(capture, area, spare);
            var outcome = stitcher.Add(frame);

            spare = outcome is Stitched.Unmoved or Stitched.Lost ? frame : null;
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;

            if (outcome is Stitched.Grown or Stitched.Rejoined)
            {
                lastGrowth = clock.Elapsed;
            }

            if (outcome == Stitched.Full)
            {
                Log.Info($"Scrolling capture reached its limit of {ScrollStitcher.MaximumRows} rows");
                break;
            }

            var waited = clock.Elapsed - lastGrowth;

            if (stitcher.HasGrown ? waited > SettledAfter : waited > NeverStartedAfter)
            {
                break;
            }

            await Task.WhenAny(stop, Task.Delay(Interval, cancellationToken));
        }

        Log.Info($"Scrolling capture: {stitcher.Rows} rows from {area.Height} in {clock.Elapsed.TotalSeconds:F1} s ("
            + string.Join(", ", outcomes.Select(outcome => $"{outcome.Value} {outcome.Key.ToString().ToLowerInvariant()}")) + ")");

        if (stitcher.Seams > 0)
        {
            Log.Warn($"The scrolling could not be followed {stitcher.Seams} time(s), so the picture has a seam where it was carried on regardless");
        }

        return stitcher.Finish();
    }

    /// <summary>The region's pixels out of a frame, as rows with nothing between them. Copied, because the engine takes every frame into the same buffer.</summary>
    static byte[] Cut(CaptureResult capture, CaptureRegion area, byte[]? into)
    {
        var rowBytes = area.Width * 4;
        var cut = into ?? new byte[(long)rowBytes * area.Height];

        for (var y = 0; y < area.Height; y++)
        {
            Buffer.BlockCopy(capture.Pixels, (area.Y + y) * capture.Stride + area.X * 4, cut, y * rowBytes, rowBytes);
        }

        return cut;
    }

    static CaptureRegion Clamp(CaptureRegion region, int width, int height)
    {
        var x = Math.Clamp(region.X, 0, Math.Max(width - 1, 0));
        var y = Math.Clamp(region.Y, 0, Math.Max(height - 1, 0));

        return new CaptureRegion(x, y, Math.Clamp(region.Width, 1, width - x), Math.Clamp(region.Height, 1, height - y));
    }
}
