using System.Diagnostics;
using SnapShotKit.Contracts;

namespace SnapShotKit.Daemon;

/// <summary>What the user chose to do with the previewed region.</summary>
public enum OverlayChoice
{
    Cancelled,
    Save,
    Edit,
    Copy
}

/// <summary>The overlay's answer: a region, and what to do with it.</summary>
public readonly record struct OverlayResult(OverlayChoice Choice, CaptureRegion Region)
{
    public static OverlayResult Cancelled => new(OverlayChoice.Cancelled, default);
}

/// <summary>
/// Runs the overlay and waits for the user's answer.
///
/// The overlay is spawned per capture rather than kept resident: an idle Avalonia process costs
/// around 98 MB and never gives it back, while spawning costs about 200 ms that lands after the
/// frame is already captured. See docs/spikes/004-process-model.md.
/// </summary>
public static class OverlayClient
{
    public static async Task<OverlayResult> AskAsync(string framePath, int width, int height, int stride,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(Locate())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = false
        };

        foreach (var argument in new[]
                 {
                     "--frame", framePath,
                     "--width", width.ToString(),
                     "--height", height.ToString(),
                     "--stride", stride.ToString()
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the overlay.");

        var answer = await process.StandardOutput.ReadLineAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return Parse(answer);
    }

    static OverlayResult Parse(string? answer)
    {
        // A crashed or killed overlay reports nothing. Treating that as a cancel is right: the user
        // has no selection, and silently saving a whole screen they did not ask for is worse.
        if (string.IsNullOrWhiteSpace(answer) || answer == "cancel")
        {
            return OverlayResult.Cancelled;
        }

        var parts = answer.Split(' ');

        if (parts.Length == 6 && parts[0] == "region")
        {
            var region = new CaptureRegion(
                int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]), int.Parse(parts[4]));

            return parts[5] switch
            {
                "save" => new OverlayResult(OverlayChoice.Save, region),
                "edit" => new OverlayResult(OverlayChoice.Edit, region),
                "copy" => new OverlayResult(OverlayChoice.Copy, region),
                // Almost always a version skew: the overlay is rebuilt in place while the daemon
                // keeps running the binary it started with, so it offers an action the daemon has
                // never heard of. Saying so beats "not an action", which sounds like a bug in the
                // overlay rather than something a restart fixes.
                var unknown => throw new InvalidOperationException(
                    $"The overlay asked for '{unknown}', which this daemon does not understand. "
                    + "It is probably older than the overlay: restart it with "
                    + "'systemctl --user restart snapshotkitd'.")
            };
        }

        throw new InvalidOperationException($"The overlay reported '{answer}', which is not a result.");
    }

    static string Locate()
    {
        const string fileName = "snapshotkit-overlay";

        if (Environment.GetEnvironmentVariable("SNAPSHOTKIT_OVERLAY") is { Length: > 0 } configured)
        {
            return configured;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(beside))
        {
            return beside;
        }

        // Development layout: find the overlay's build output next to ours.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent?.Name ?? "Debug";
        var framework = directory.Name;

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "SnapShotKit.Overlay", "bin", configuration, framework, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {fileName}. Build src/SnapShotKit.Overlay.");
    }
}
