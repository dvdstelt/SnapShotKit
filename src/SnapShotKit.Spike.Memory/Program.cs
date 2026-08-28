using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Diagnostics;

namespace SnapShotKit.Spike.Memory;

// Spike 004: what does an idle Avalonia process actually cost?
//
// The daemon design assumes it is acceptable to keep a GUI toolkit resident all day so the overlay
// can appear without paying startup. That assumption was never measured, and if it is wrong the
// daemon should be a headless process that spawns the UI on demand instead.

internal static class Program
{
    // Assigned as the first statement of Main. A static initialiser would run lazily on first
    // access, which silently measures nothing.
    internal static Stopwatch SinceStart = null!;

    public static int Main(string[] args)
    {
        SinceStart = Stopwatch.StartNew();

        Report("bare .NET, before Avalonia is touched");

        return AppBuilder.Configure<MeasureApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }

    internal static void Report(string stage)
    {
        Console.WriteLine($"  {ReadResidentKilobytes() / 1024.0,7:F1} MB  {stage}");
    }

    static long ReadResidentKilobytes()
    {
        foreach (var line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
            {
                return long.Parse(line.Split(':')[1].Trim().Split(' ')[0]);
            }
        }

        return 0;
    }
}

internal sealed class MeasureApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        Console.WriteLine($"  {Program.SinceStart.ElapsedMilliseconds,7} ms  process start to Avalonia initialised");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = MeasureAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    static async Task MeasureAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Let initialisation settle before reading, otherwise the number is a moving target.
        await Task.Delay(1500);
        Program.Report("Avalonia initialised, no window ever shown  <- the daemon's idle cost");

        var bitmap = new WriteableBitmap(new PixelSize(5120, 1440), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        await Task.Delay(300);
        Program.Report("after allocating a 5120x1440 frame buffer (28 MB)");

        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            WindowState = WindowState.FullScreen,
            Content = new Image { Source = bitmap }
        };

        var toWindow = Stopwatch.StartNew();
        window.Show();
        // Wait for an actual frame rather than just the Show call returning.
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        toWindow.Stop();

        Console.WriteLine($"  {toWindow.ElapsedMilliseconds,7} ms  Show() to first frame on screen");

        await Task.Delay(2000);
        Program.Report("fullscreen window shown with the frame  <- peak during a capture");

        window.Close();
        bitmap.Dispose();
        await Task.Delay(500);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(1500);

        Program.Report("window closed, collected  <- idle cost after the first capture");

        desktop.Shutdown();
    }
}
