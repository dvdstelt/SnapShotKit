using System.Diagnostics;

namespace SnapShotKit.Cli;

/// <summary>
/// Installs the systemd user service.
///
/// Binding Print without this produces a shortcut that fails, because the client is only a
/// messenger: everything expensive lives in the daemon and the daemon has to already be running.
/// </summary>
internal static class ServiceUnit
{
    const string UnitName = "snapshotkitd.service";

    static string UnitPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "systemd", "user", UnitName);

    public static void Install()
    {
        var daemon = LocateDaemon();

        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        File.WriteAllText(UnitPath, $"""
            [Unit]
            Description=SnapShotKit capture daemon
            PartOf=graphical-session.target
            After=graphical-session.target

            [Service]
            Type=simple
            ExecStart={daemon}
            Restart=on-failure
            RestartSec=2

            [Install]
            WantedBy=graphical-session.target
            """);

        Systemctl("daemon-reload");
        Systemctl("enable", "--now", UnitName);

        Console.WriteLine($"Daemon installed and started: {daemon}");
    }

    public static void Remove()
    {
        if (!File.Exists(UnitPath))
        {
            return;
        }

        Systemctl("disable", "--now", UnitName);
        File.Delete(UnitPath);
        Systemctl("daemon-reload");

        Console.WriteLine("Daemon stopped and removed from startup.");
    }

    static string LocateDaemon()
    {
        const string fileName = "snapshotkitd";

        if (Environment.GetEnvironmentVariable("SNAPSHOTKIT_DAEMON") is { Length: > 0 } configured)
        {
            return configured;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(beside))
        {
            return beside;
        }

        // Development layout: the daemon's build output sits beside ours.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent?.Name ?? "Debug";
        var framework = directory.Name;

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "SnapShotKit.Daemon", "bin", configuration, framework, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {fileName}. Build src/SnapShotKit.Daemon.");
    }

    static void Systemctl(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("systemctl") { RedirectStandardError = true };
        startInfo.ArgumentList.Add("--user");

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not run systemctl.");

        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"systemctl --user {string.Join(' ', arguments)} failed: {error.Trim()}");
        }
    }
}
