using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace SnapShotKit.Editor;

// snapshotkit-editor: opens a .ssk snapshot for annotation.
//
// Deliberately a standalone tool rather than part of the capture path. Editing a snapshot has
// nothing to do with taking one, and the two are wanted at different times.

internal static class Program
{
    internal static string SnapshotPath = string.Empty;

    /// <summary>When set, render straight to this path and exit without showing a window.</summary>
    internal static string? ExportPath;

    public static int Main(string[] args)
    {
        Crash.Install();

        // A flag's value must not be mistaken for the snapshot: "--export out.png snap.ssk" would
        // otherwise try to open out.png. Walk the arguments once, consuming values with their flags.
        string? path = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--export")
            {
                if (i + 1 < args.Length)
                {
                    ExportPath = Path.GetFullPath(args[++i]);
                }

                continue;
            }

            if (!args[i].StartsWith('-'))
            {
                path ??= args[i];
            }
        }

        // Launched with no snapshot, it opens the library. It is a tool in its own right, not only
        // something the capture path hands work to.
        if (path is not null)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"snapshotkit-editor: {path} does not exist.");
                return 1;
            }

            SnapshotPath = Path.GetFullPath(path);
        }

        return AppBuilder.Configure<EditorApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class EditorApp : Application
{
    public override void Initialize()
    {
        // Without a theme the built-in controls have no template at all: a Slider renders as
        // nothing and cannot be dragged, which is exactly how it failed.
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        base.Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                if (Program.SnapshotPath.Length == 0)
                {
                    var library = new LibraryWindow();
                    library.Chosen += chosen => new EditorWindow(Snapshot.Open(chosen)).Show();

                    desktop.MainWindow = library;
                    base.OnFrameworkInitializationCompleted();
                    return;
                }

                var snapshot = Snapshot.Open(Program.SnapshotPath);

                if (Program.ExportPath is { } exportPath)
                {
                    // Rendering needs the graphics stack, so this runs inside the app lifetime even
                    // though no window is ever shown.
                    using var blurs = new BlurCache(snapshot.OriginalPng);
                    Export.ToFile(snapshot, blurs, exportPath);

                    Console.WriteLine(exportPath);

                    // Posted rather than called directly: shutting down from here tears the
                    // dispatcher down before the main loop starts, and the loop then throws.
                    Dispatcher.UIThread.Post(() => desktop.Shutdown());
                    return;
                }

                desktop.MainWindow = new EditorWindow(snapshot);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"snapshotkit-editor: could not open {Program.SnapshotPath}: {exception.Message}");
                Environment.Exit(1);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
