using SnapShotKit.Contracts;

namespace SnapShotKit.Editor;

/// <summary>
/// Records unhandled exceptions to a file.
///
/// The editor is normally started by the daemon, so it has no terminal and stderr goes nowhere
/// anybody will look. A crash that leaves no trace is a crash that has to be reproduced by
/// guesswork, which is exactly what happened the first time.
/// </summary>
public static class Crash
{
    static string LogPath => Path.Combine(SnapShotKitPaths.State, "editor-crash.log");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record("unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Record("unobserved task", e.Exception);
    }

    public static void Record(string kind, Exception? exception)
    {
        var report = $"""

            ===== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {kind} =====
            {exception?.ToString() ?? "no exception object"}
            """;

        Console.Error.WriteLine(report);

        try
        {
            Directory.CreateDirectory(SnapShotKitPaths.State);
            File.AppendAllText(LogPath, report);
        }
        catch
        {
            // Already handling a crash; failing to write about it changes nothing.
        }
    }
}
