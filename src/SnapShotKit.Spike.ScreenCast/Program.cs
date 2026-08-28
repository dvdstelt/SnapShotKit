using System.Diagnostics;
using SnapShotKit.Portal;

// Spike 002: can the ScreenCast portal give us repeated captures without a consent dialog, and does
// pulling a raw PipeWire frame beat the ~700 ms the Screenshot portal costs?
// See docs/spikes/002-screencast-capture.md.

var stateFile = GetOption(args, "--state") ?? Path.Combine("spike-output", "screencast.token");
var pullFrame = args.Contains("--frame");
var frameCount = int.TryParse(GetOption(args, "--frames"), out var parsed) ? parsed : 1;
if (frameCount > 1) pullFrame = true;
var forget = args.Contains("--forget");

// Consent needs a human, so anything this slow means a dialog was shown.
var promptThreshold = TimeSpan.FromMilliseconds(1500);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stateFile))!);

if (forget && File.Exists(stateFile))
{
    File.Delete(stateFile);
    Console.WriteLine($"Deleted {stateFile}. The next run will ask for consent again.");
    Console.WriteLine();
}

var restoreToken = File.Exists(stateFile) ? File.ReadAllText(stateFile).Trim() : null;

Console.WriteLine("SnapShotKit spike 002: XDG ScreenCast portal");
Console.WriteLine("=======================================");
Console.WriteLine($"  state file     : {stateFile}");
Console.WriteLine($"  restore token  : {(restoreToken is null ? "none, expect a consent dialog" : $"{restoreToken[..Math.Min(8, restoreToken.Length)]}... ({restoreToken.Length} chars)")}");
Console.WriteLine();

using var portal = await PortalClient.ConnectAsync();
var screenCast = new ScreenCastPortal(portal);

Console.WriteLine($"  portal version : {await screenCast.GetVersionAsync()}");
Console.WriteLine();

if (restoreToken is null)
{
    Console.WriteLine("  No token yet. GNOME will ask you to share the screen. Accept it once, and the");
    Console.WriteLine("  token returned by Start should make every later run silent.");
    Console.WriteLine();
}

var total = Stopwatch.StartNew();

var sessionHandle = await Timed("CreateSession", () => screenCast.CreateSessionAsync());
await Timed("SelectSources", async () => { await screenCast.SelectSourcesAsync(sessionHandle, restoreToken); return 0; });
var startElapsed = Stopwatch.StartNew();
var session = await Timed("Start", () => screenCast.StartAsync(sessionHandle));
startElapsed.Stop();
var fd = await Timed("OpenPipeWireRemote", () => screenCast.OpenPipeWireRemoteAsync(sessionHandle));

total.Stop();

Console.WriteLine();
Console.WriteLine($"  node id        : {session.NodeId}");
Console.WriteLine($"  stream size    : {(session.Size is { } size ? $"{size.Width}x{size.Height}" : "not reported")}");
Console.WriteLine($"  pipewire fd    : {fd}");
Console.WriteLine($"  handshake      : {total.ElapsedMilliseconds} ms total");

if (session.RestoreToken is { } issued)
{
    await File.WriteAllTextAsync(stateFile, issued);
    Console.WriteLine($"  restore token  : saved to {stateFile}");
}
else
{
    Console.WriteLine("  restore token  : NONE RETURNED, so the next run will prompt again");
}

Console.WriteLine();
Console.WriteLine("Verdict");
Console.WriteLine("-------");

if (restoreToken is null)
{
    Console.WriteLine("  First run, so the dialog was expected. Run again to find out whether the token");
    Console.WriteLine("  actually suppresses it.");
}
else if (startElapsed.Elapsed > promptThreshold)
{
    Console.WriteLine($"  Start took {startElapsed.ElapsedMilliseconds} ms with a restore token, which means consent was");
    Console.WriteLine("  asked for again. ScreenCast is not usable as a silent capture backend here.");
}
else
{
    Console.WriteLine($"  Start took {startElapsed.ElapsedMilliseconds} ms with a restore token and no dialog. The session can be");
    Console.WriteLine("  established silently, so a daemon can hold one warm and pull frames on demand.");
}

if (pullFrame)
{
    Console.WriteLine();
    Console.WriteLine("Frame pull");
    Console.WriteLine("----------");
    await PullFrameAsync(session, frameCount);
}

if (int.TryParse(GetOption(args, "--hold"), out var holdSeconds) && holdSeconds > 0)
{
    Console.WriteLine();
    Console.WriteLine($"Holding the session open for {holdSeconds}s. Probe it with:");
    Console.WriteLine($"  gst-launch-1.0 -v pipewiresrc path={session.NodeId} num-buffers=1 ! fakesink");
    await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
}

await screenCast.CloseSessionAsync(sessionHandle);
return 0;

async Task<T> Timed<T>(string label, Func<Task<T>> work)
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        var result = await work();
        Console.WriteLine($"  {label,-20} {stopwatch.ElapsedMilliseconds,6} ms");
        return result;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"  {label,-20} {stopwatch.ElapsedMilliseconds,6} ms  FAILED: {exception.Message}");
        throw;
    }
}

async Task PullFrameAsync(ScreenCastSession session, int frames)
{
    // Baseline first: gst-launch process startup is counted in the frame timing, so measure it
    // separately rather than quietly attributing it to PipeWire.
    var baseline = await RunAsync("gst-launch-1.0", "-q fakesrc num-buffers=1 ! fakesink");
    Console.WriteLine($"  gst-launch baseline  {baseline.Elapsed.TotalMilliseconds,6:F0} ms  (process startup, subtract this)");

    // One frame includes connecting to PipeWire and negotiating a format. Many frames amortise
    // that, so the difference between the two is the steady-state cost a warm stream would pay.
    var output = Path.Combine("spike-output", "frame.raw");
    var single = await RunAsync("gst-launch-1.0", $"-q pipewiresrc path={session.NodeId} num-buffers=1 ! video/x-raw ! filesink location={output}");
    var result = frames > 1
        ? await RunAsync("gst-launch-1.0", $"-q pipewiresrc path={session.NodeId} num-buffers={frames} ! video/x-raw ! fakesink")
        : single;

    if (frames > 1 && single.Succeeded && result.Succeeded)
    {
        var marginal = (result.Elapsed - single.Elapsed).TotalMilliseconds / (frames - 1);
        Console.WriteLine($"  1 frame              {single.Elapsed.TotalMilliseconds,6:F0} ms");
        Console.WriteLine($"  {frames} frames            {result.Elapsed.TotalMilliseconds,6:F0} ms");
        Console.WriteLine($"  marginal per frame   {marginal,6:F1} ms  <- what a warm in-process stream would cost");
    }

    if (!result.Succeeded)
    {
        Console.WriteLine($"  frame pull FAILED ({result.Elapsed.TotalMilliseconds:F0} ms): {result.Error.Trim()}");
        return;
    }

    var length = new FileInfo(output).Length;
    var caps = await RunAsync("gst-launch-1.0", $"-v pipewiresrc path={session.NodeId} num-buffers=1 ! fakesink");
    var capsLine = (caps.Output + caps.Error)
        .Split('\n')
        .FirstOrDefault(line => line.Contains("video/x-raw"));
    Console.WriteLine($"  negotiated caps      {(capsLine is null ? "not reported" : capsLine.Trim()[(capsLine.Trim().IndexOf("video/x-raw", StringComparison.Ordinal))..])}");
    var net = result.Elapsed - baseline.Elapsed;

    Console.WriteLine($"  frame pull           {result.Elapsed.TotalMilliseconds,6:F0} ms  -> {length / 1024.0 / 1024.0:F2} MB raw");
    Console.WriteLine($"  net of startup       {net.TotalMilliseconds,6:F0} ms");
    Console.WriteLine();
    Console.WriteLine($"  Screenshot portal costs ~700 ms for the same picture as an encoded PNG.");
}

static async Task<(bool Succeeded, TimeSpan Elapsed, string Error, string Output)> RunAsync(string fileName, string arguments)
{
    var startInfo = new ProcessStartInfo(fileName, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    var stopwatch = Stopwatch.StartNew();
    using var process = Process.Start(startInfo)!;
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    var output = await outputTask;
    await process.WaitForExitAsync();
    stopwatch.Stop();

    return (process.ExitCode == 0, stopwatch.Elapsed, error, output);
}

static string? GetOption(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
