using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>
/// What the export should come out as, asked once before the save dialog opens.
///
/// Settings first and the file name second, rather than the other way round. The format decides the
/// extension, and a save dialog that has to be told the name before anyone has said what kind of
/// file it is can only offer to rename it afterwards. This way the picker arrives already filtered,
/// already named and already pointed at a folder.
///
/// Not built into the picker itself, though the portal can carry extra controls: those are a fixed
/// list of combo boxes drawn by the file chooser in its own style, with no way to show a quality
/// setting only when the format that has one is chosen, and no way to look like the rest of this
/// application.
///
/// Every setting is remembered, so exporting the same way twice is this dialog and Enter.
/// </summary>
public static class ExportDialog
{
    const double Width = 420;

    /// <summary>Asks, and returns the settings chosen, or nothing if the dialog was cancelled.</summary>
    public static async Task<ExportSettings?> AskAsync(Window owner, ExportSettings current, string? originFolder)
    {
        // A copy, so a cancelled dialog leaves the remembered settings exactly as they were.
        var settings = current.Copy();
        var accepted = false;

        var dialog = new Window
        {
            Title = "Export",
            Width = Width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Tokens.BgBrush
        };

        // Built once and refilled, never rebuilt: the rows below the format change with it, and a
        // panel that is replaced takes the keyboard focus and the window's settled height with it.
        var options = new StackPanel { Spacing = Tokens.Space.S3, Margin = new Thickness(0, Tokens.Space.S3, 0, 0) };

        // Rebuilding the rows from inside one of the controls being rebuilt would pull that control
        // out of the tree while it is still handling its own click, so a change asked for from
        // within the panel is posted and happens once the click has finished. A change asked for by
        // the format control, which lives above the panel and survives it, happens straight away.
        void Rebuild() => Fill(options, settings, Later);
        void Later() => Dispatcher.UIThread.Post(Rebuild);

        var format = new Segmented(["PNG", "JPEG", "WebP"], index =>
        {
            settings.Format = index switch
            {
                1 => ExportFormat.Jpeg,
                2 => ExportFormat.Webp,
                _ => ExportFormat.Png
            };

            Rebuild();
        });

        format.Select(settings.Format switch
        {
            ExportFormat.Jpeg => 1,
            ExportFormat.Webp => 2,
            _ => 0
        });

        Rebuild();

        var body = new StackPanel
        {
            Children =
            {
                Labels.Heading("EXPORT", 13, 0.18, Tokens.Neutral800Brush),
                Row("Format", format),
                options
            }
        };

        // Only worth asking when there is somewhere else to go. A capture came off a screen, so
        // there is one sensible folder and no question to put to anybody.
        if (originFolder is not null)
        {
            var where = new Segmented(["Exports folder", "Beside the original"],
                index => settings.Folder = index == 1 ? ExportFolder.Origin : ExportFolder.Exports);

            where.Select(settings.Folder == ExportFolder.Origin ? 1 : 0);

            body.Children.Add(new Border
            {
                BorderBrush = Tokens.DividerBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, Tokens.Space.S4, 0, 0),
                Padding = new Thickness(0, Tokens.Space.S4, 0, 0),
                Child = new StackPanel
                {
                    Children =
                    {
                        Row("Start in", where),
                        Note(Shorten(originFolder))
                    }
                }
            });
        }

        void Accept()
        {
            accepted = true;
            dialog.Close();
        }

        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S2,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, Tokens.Space.S6, 0, 0),
            Children =
            {
                Buttons.Secondary("Cancel", null, () => dialog.Close()),
                Buttons.Primary("Export…", null, Accept)
            }
        });

        dialog.Content = new Border
        {
            Padding = new Thickness(Tokens.Space.S6),
            Child = body
        };

        // Enter and Escape, because a dialog whose whole job is to be confirmed should not need the
        // pointer to confirm it. Tunnelled, so the segmented controls and the sliders do not have
        // to agree to let them past.
        dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Accept();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    dialog.Close();
                    e.Handled = true;
                    break;
            }
        }, RoutingStrategies.Tunnel);

        await dialog.ShowDialog(owner);
        return accepted ? settings : null;
    }

    /// <summary>
    /// Puts the rows the chosen format actually has into the panel, and takes the others out.
    ///
    /// <paramref name="refill"/> is how a control in here asks to be drawn again, for the settings
    /// that change what the rows beside them mean rather than only their own value.
    /// </summary>
    static void Fill(StackPanel options, ExportSettings settings, Action refill)
    {
        options.Children.Clear();

        if (settings.CanKeepTransparency)
        {
            var transparency = new Segmented(["Flatten onto white", "Keep"], index =>
            {
                settings.KeepTransparency = index == 1;

                // The line under PNG says what the file will cost, and that answer just changed.
                refill();
            });

            transparency.Select(settings.KeepTransparency ? 1 : 0);
            options.Children.Add(Row("Transparency", transparency));
        }

        switch (settings.Format)
        {
            case ExportFormat.Jpeg:
                options.Children.Add(Amount("Quality", 1, 100, settings.JpegQuality,
                    value => settings.JpegQuality = value));

                options.Children.Add(Note("Lossy. Text and hairlines soften as the quality comes down."));
                break;

            case ExportFormat.Webp:
                var encoding = new Segmented(["Lossless", "Lossy"], index =>
                {
                    settings.WebpLossless = index == 0;

                    // Rebuilt because the number below means something else now, and a slider that
                    // keeps its label while changing its meaning is a lie about what it does.
                    refill();
                });

                encoding.Select(settings.WebpLossless ? 0 : 1);
                options.Children.Add(Row("Encoding", encoding));

                options.Children.Add(Amount(settings.WebpLossless ? "Effort" : "Quality", 1, 100, settings.WebpQuality,
                    value => settings.WebpQuality = value));

                options.Children.Add(Note(settings.WebpLossless
                    ? "Exact pixels either way. Higher works harder for a smaller file."
                    : "Lossy. Smaller than JPEG at the same quality, and softens the same things."));
                break;

            default:
                options.Children.Add(Note(settings.KeepTransparency
                    ? "Lossless, with an alpha channel."
                    : "Lossless, and a quarter smaller for having no alpha channel to carry."));
                break;
        }
    }

    /// <summary>A labelled row: the name on the left at a fixed width, the control filling what is left.</summary>
    static Control Row(string label, Control control)
    {
        var name = Labels.Body(label, 12.5, Tokens.Neutral600Brush);
        name.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };

        Grid.SetColumn(name, 0);
        Grid.SetColumn(control, 1);

        grid.Children.Add(name);
        grid.Children.Add(control);

        return grid;
    }

    /// <summary>A slider with its number beside it, since a quality nobody can read is a quality nobody can repeat.</summary>
    static Control Amount(string label, int minimum, int maximum, int value, Action<int> chosen)
    {
        var readout = Labels.Body($"{value}", 12.5, Tokens.Neutral800Brush);
        readout.VerticalAlignment = VerticalAlignment.Center;
        readout.Width = 26;
        readout.TextAlignment = Avalonia.Media.TextAlignment.Right;

        var slide = new Slide(minimum, maximum, value);
        slide.VerticalAlignment = VerticalAlignment.Center;

        slide.Moved += moved =>
        {
            var rounded = (int)Math.Round(moved);
            readout.Text = $"{rounded}";
            chosen(rounded);
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        Grid.SetColumn(slide, 0);
        Grid.SetColumn(readout, 1);

        grid.Children.Add(slide);
        grid.Children.Add(readout);

        return Row(label, grid);
    }

    /// <summary>
    /// A folder as somebody would say it out loud, with the home directory written the short way.
    ///
    /// The point of showing the path at all is to answer "beside which original", and a line that
    /// wraps three times to spell out a home directory the reader already knows answers it worse
    /// than one that fits.
    /// </summary>
    static string Shorten(string folder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return home.Length > 0 && folder.StartsWith(home, StringComparison.Ordinal)
            ? string.Concat("~", folder.AsSpan(home.Length))
            : folder;
    }

    /// <summary>The line under a setting that says what it costs, indented to the controls it belongs to.</summary>
    static Control Note(string text)
    {
        var note = Labels.Body(text, 11.5, Tokens.Neutral500Brush);
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        note.Margin = new Thickness(110, 0, 0, 0);

        return note;
    }
}
