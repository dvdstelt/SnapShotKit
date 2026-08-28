namespace SnapShotKit.Daemon;

/// <summary>
/// Logging for a process supervised by systemd: stdout and stderr go to the journal, so there is no
/// value in a logging framework or a log file of our own.
/// </summary>
public static class Log
{
    public static void Info(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");

    public static void Warn(string message) => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} WARN {message}");

    public static void Error(string message) => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} ERROR {message}");
}
