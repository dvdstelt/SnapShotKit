using System.Diagnostics;
using SnapShotKit.Portal;
using SnapShotKit.Spike.PortalCapture;

// Spike 001: can an unsandboxed .NET app take repeated full-screen captures through the
// XDG Screenshot portal without the compositor prompting every time, and how fast is it?
// See docs/spikes/001-portal-capture.md for why this question decides the capture backend.

var iterations = GetIntOption(args, "--iterations") ?? 3;
var interactive = args.Contains("--interactive");
var outputDirectory = GetOption(args, "--out") ?? Path.Combine(AppContext.BaseDirectory, "spike-output");
var keepFiles = args.Contains("--keep");

// A prompt needs a human, so anything this slow almost certainly means a dialog was shown.
var promptThreshold = TimeSpan.FromMilliseconds(1500);

Directory.CreateDirectory(outputDirectory);

Console.WriteLine("SnapShotKit spike 001: XDG Screenshot portal");
Console.WriteLine("========================================");
Console.WriteLine($"  session type   : {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")}");
Console.WriteLine($"  desktop        : {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")}");
Console.WriteLine($"  mode           : {(interactive ? "interactive (compositor picker)" : "non-interactive (straight to full screen)")}");
Console.WriteLine($"  iterations     : {iterations}");
Console.WriteLine($"  output         : {outputDirectory}");
Console.WriteLine();

using var portal = await PortalClient.ConnectAsync();
var screenshot = new ScreenshotPortal(portal);

Console.WriteLine($"  portal version : {await screenshot.GetVersionAsync()}");
Console.WriteLine();

if (!interactive)
{
    Console.WriteLine("Watch the screen. If a permission dialog appears, answer it and note which iteration it was on.");
}
else
{
    Console.WriteLine("The compositor picker will open on each iteration. Pick anything and confirm.");
}

Console.WriteLine();

var results = new List<Attempt>();

for (var i = 1; i <= iterations; i++)
{
    Console.Write($"  [{i}/{iterations}] capturing ... ");

    var stopwatch = Stopwatch.StartNew();
    ScreenshotResult capture;
    try
    {
        capture = await screenshot.CaptureAsync(interactive);
    }
    catch (Exception exception)
    {
        stopwatch.Stop();
        Console.WriteLine($"FAILED after {stopwatch.ElapsedMilliseconds} ms: {exception.Message}");
        results.Add(new Attempt(i, stopwatch.Elapsed, null, null, 0, exception.Message));
        continue;
    }

    stopwatch.Stop();

    if (!capture.IsSuccess)
    {
        Console.WriteLine($"{capture.Status} after {stopwatch.ElapsedMilliseconds} ms");
        results.Add(new Attempt(i, stopwatch.Elapsed, null, null, 0, capture.Status.ToString()));
        continue;
    }

    var source = capture.Path!;
    var destination = Path.Combine(outputDirectory, $"capture-{i:00}.png");
    File.Move(source, destination, overwrite: true);

    var length = new FileInfo(destination).Length;
    var dimensions = Png.ReadDimensions(destination);

    Console.WriteLine($"{stopwatch.ElapsedMilliseconds,6} ms  {dimensions?.Width}x{dimensions?.Height}  {length / 1024.0 / 1024.0:F2} MB  (portal wrote it to {Path.GetDirectoryName(source)})");
    results.Add(new Attempt(i, stopwatch.Elapsed, destination, dimensions, length, null));
}

Console.WriteLine();
Console.WriteLine("Verdict");
Console.WriteLine("-------");

var successes = results.Where(r => r.Error is null).ToList();
var prompted = successes.Where(r => r.Duration > promptThreshold).ToList();

if (successes.Count == 0)
{
    Console.WriteLine("  Every capture failed. The Screenshot portal is not a usable backend as called here.");
}
else
{
    Console.WriteLine($"  succeeded      : {successes.Count}/{iterations}");
    Console.WriteLine($"  fastest        : {successes.Min(r => r.Duration).TotalMilliseconds:F0} ms");
    Console.WriteLine($"  slowest        : {successes.Max(r => r.Duration).TotalMilliseconds:F0} ms");
    Console.WriteLine($"  over {promptThreshold.TotalMilliseconds:F0} ms   : {(prompted.Count == 0 ? "none" : string.Join(", ", prompted.Select(r => $"#{r.Index}")))}");
    Console.WriteLine();

    Console.WriteLine(prompted.Count switch
    {
        0 => "  No iteration was slow enough to have involved a dialog. If nothing appeared on screen,\n  the Screenshot portal is prompt-free and fast enough to be the capture backend.",
        1 when prompted[0].Index == 1 => "  Only the first iteration was slow, which is the signature of a one-time permission grant.\n  The Screenshot portal is viable: pay the prompt once, then captures are silent.",
        _ => "  Multiple iterations were slow, which suggests the portal prompts repeatedly.\n  Fall back to the ScreenCast portal with a restore token held warm in a daemon."
    });
}

if (successes.Count > 1)
{
    var sizes = successes.Select(r => r.Dimensions).Distinct().ToList();
    if (sizes.Count > 1)
    {
        Console.WriteLine();
        Console.WriteLine($"  WARNING: captures came back at different sizes ({string.Join(", ", sizes.Select(s => $"{s?.Width}x{s?.Height}"))}).");
    }
}

if (!keepFiles && successes.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"  {successes.Count} image(s) left in {outputDirectory} for inspection. Delete them when done, or pass --keep to stop seeing this note.");
}

return successes.Count == 0 ? 1 : 0;

static string? GetOption(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int? GetIntOption(string[] args, string name)
    => int.TryParse(GetOption(args, name), out var value) ? value : null;

internal readonly record struct Attempt(int Index, TimeSpan Duration, string? Path, (int Width, int Height)? Dimensions, long Length, string? Error);
