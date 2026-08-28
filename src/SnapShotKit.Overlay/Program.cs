using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Input;
using SnapShotKit.Ui;

namespace SnapShotKit.Overlay;

// snapshotkit-overlay: shows the frozen capture, lets the user shape a region, and asks what to do with
// it. Nothing is captured until an action is chosen, so the box is a preview rather than a result.
//
// It is a separate short-lived process rather than part of the daemon, so an idle SnapShotKit does not
// keep a GUI toolkit resident. See docs/spikes/004-process-model.md.
//
// Reports one line on stdout:
//   region <x> <y> <width> <height> save|edit|copy
//   cancel

internal static class Program
{
    internal static FrameInfo Info;
    internal static bool StartWithMagnifier;

    public static int Main(string[] args)
    {
        if (Option(args, "--frame") is not { } path)
        {
            Console.Error.WriteLine("usage: snapshotkit-overlay --frame <path> --width <w> --height <h> --stride <s>");
            return 2;
        }

        Info = new FrameInfo(
            path,
            int.Parse(Option(args, "--width") ?? "0"),
            int.Parse(Option(args, "--height") ?? "0"),
            int.Parse(Option(args, "--stride") ?? "0"));

        // On by default: it earns its place. M still toggles it, which is useful when judging paint cost.
        StartWithMagnifier = !args.Contains("--no-magnifier");

        // Lets the daemon exercise the whole pipeline without a human at the keyboard.
        if (Option(args, "--auto") is { } auto)
        {
            Console.WriteLine(auto == "full" ? "cancel" : $"region {auto.Replace(',', ' ')} save");
            return 0;
        }

        return AppBuilder.Configure<OverlayApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }

    static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal sealed class OverlayApp : Application
{
    SelectionView view = null!;
    Canvas chrome = null!;
    ActionBar wholeScreenActions = null!;
    ActionBar regionActions = null!;
    HintBar hints = null!;
    IClassicDesktopStyleApplicationLifetime desktop = null!;

    public override void Initialize()
    {
        // Without a theme the built-in controls have no template at all: a Button renders as
        // nothing and cannot be clicked.
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

        base.Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            desktop = lifetime;

            var bitmap = Frame.Load(Program.Info);
            view = new SelectionView(bitmap) { ShowMagnifier = Program.StartWithMagnifier };

            wholeScreenActions = new ActionBar(forRegion: false, Chosen);
            regionActions = new ActionBar(forRegion: true, Chosen) { IsVisible = false };

            hints = new HintBar("Drag to draw a region", "Space — whole screen", "Esc — cancel");

            // A transparent canvas over the surface: only the chrome sits in it, so everywhere else
            // keeps reaching the selection view underneath.
            chrome = new Canvas();
            chrome.Children.Add(wholeScreenActions);
            chrome.Children.Add(regionActions);
            chrome.Children.Add(hints);

            var root = new Panel();
            root.Children.Add(view);
            root.Children.Add(chrome);

            var window = new Window
            {
                WindowDecorations = WindowDecorations.None,
                WindowState = WindowState.FullScreen,
                Topmost = true,
                Background = null,
                Cursor = new Cursor(StandardCursorType.Cross),
                Content = root
            };

            view.SelectionChanged += Refresh;

            view.LayoutUpdated += (_, _) => Refresh();
            window.KeyDown += OnKeyDown;

            window.Show();
            window.Activate();
            view.Focus();

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Moves the chrome to match the phase.
    ///
    /// The whole-screen offer belongs at the top where the pointer is not; the region's actions
    /// belong under the region itself. Both hide while a drag is in progress, so nothing sits over
    /// the magnifier at the moment it is being used.
    /// </summary>
    void Refresh()
    {
        var screen = view.Bounds.Size;

        if (screen.Width <= 0)
        {
            return;
        }

        hints.PlaceAtBottom(screen);

        var hasRegion = view.Selection is { Width: > 0, Height: > 0 };

        wholeScreenActions.IsVisible = !hasRegion && !view.IsAdjusting;
        regionActions.IsVisible = hasRegion && !view.IsAdjusting;

        if (wholeScreenActions.IsVisible)
        {
            wholeScreenActions.PlaceAtTop(screen);
        }

        if (regionActions.IsVisible)
        {
            regionActions.PlaceUnder(view.SelectionInControl(), screen);
        }
    }

    void Chosen(OverlayAction action)
    {
        switch (action)
        {
            case OverlayAction.WholeScreen:
                Finish($"region 0 0 {view.ImageSize.Width} {view.ImageSize.Height} save");
                break;

            case OverlayAction.WholeScreenToClipboard:
                Finish($"region 0 0 {view.ImageSize.Width} {view.ImageSize.Height} copy");
                break;

            case OverlayAction.Save when view.Selection is { } save:
                Finish($"region {save.X} {save.Y} {save.Width} {save.Height} save");
                break;

            case OverlayAction.Edit when view.Selection is { } edit:
                Finish($"region {edit.X} {edit.Y} {edit.Width} {edit.Height} edit");
                break;

            case OverlayAction.Copy when view.Selection is { } copy:
                Finish($"region {copy.X} {copy.Y} {copy.Width} {copy.Height} copy");
                break;

            case OverlayAction.Cancel:
                Finish("cancel");
                break;
        }
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;

        // Arrows steer the drag while the button is held, so an edge can be placed on an exact
        // pixel without fighting the mouse. With a region already drawn and nothing being dragged,
        // they nudge the region itself, and with Shift they resize it.
        var arrow = e.Key switch
        {
            Key.Left => new PixelPoint(-1, 0),
            Key.Right => new PixelPoint(1, 0),
            Key.Up => new PixelPoint(0, -1),
            Key.Down => new PixelPoint(0, 1),
            _ => default
        };

        if (arrow != default)
        {
            e.Handled = true;

            if (view.Nudge(arrow.X * step, arrow.Y * step))
            {
                return;
            }

            if (view.Selection is { } region)
            {
                view.SetSelection(e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                    ? new PixelRect(region.X, region.Y,
                        Math.Max(region.Width + arrow.X, 1), Math.Max(region.Height + arrow.Y, 1))
                    : new PixelRect(region.X + arrow.X, region.Y + arrow.Y, region.Width, region.Height));
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                // Escape backs out one step: first the selection, then the overlay itself.
                if (view.Selection is not null)
                {
                    view.Clear();
                }
                else
                {
                    Finish("cancel");
                }

                break;

            // Enter takes the primary action of whichever phase is showing, which is what the one
            // solid button on screen says it is.
            case Key.Enter when view.Selection is { } chosen:
                Finish($"region {chosen.X} {chosen.Y} {chosen.Width} {chosen.Height} save");
                break;

            case Key.Space or Key.Enter:
                Finish($"region 0 0 {view.ImageSize.Width} {view.ImageSize.Height} save");
                break;

            case Key.M:
                view.ToggleMagnifier();
                break;
        }
    }

    void Finish(string result)
    {
        Console.WriteLine(result);
        Console.Out.Flush();
        desktop.Shutdown();
    }
}
