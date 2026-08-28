using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapShotKit.Contracts;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>
/// The whole collection of captures, as a grid grouped by day.
///
/// A wall of thumbnails rather than a list with one preview: recognising a screenshot is something
/// the eye does at a glance and in parallel, and the only reason the old shape was a list was that
/// thumbnails were expensive. They are cached now, so they can all be on screen at once.
///
/// Searching and tagging are deliberately absent for the moment. The date groups carry most of the
/// weight of finding something, and a search worth having has to look inside the documents, which
/// wants an index rather than four hundred archives opened per keystroke.
/// </summary>
public sealed class LibraryWindow : Window
{
    const double ThumbnailWidth = 196;
    const double ThumbnailHeight = 116;

    /// <summary>How many documents may be read at once while filling in what is drawn on each capture.</summary>
    static readonly SemaphoreSlim Reading = new(4);

    readonly ThumbnailCache thumbnails = new();
    readonly StackPanel groups;
    readonly TextBlock count;
    readonly MenuBar menu;

    CancellationTokenSource work = new();
    SnapshotItem? selected;
    Border? selectedCell;

    public LibraryWindow()
    {
        Title = "SnapShotKit library";
        Width = 1180;
        Height = 740;
        Background = Tokens.BgBrush;

        count = Labels.Body(string.Empty, 12.5, Tokens.Neutral600Brush);
        count.VerticalAlignment = VerticalAlignment.Center;
        count.HorizontalAlignment = HorizontalAlignment.Right;

        menu = new MenuBar("SnapShotKit");

        menu.Add("File", () =>
        [
            MenuEntry.Item("New capture", "Print", NewCapture),
            MenuEntry.Separator,
            MenuEntry.Item("Close", "Ctrl+W", Close)
        ]);

        menu.Add("Library", () =>
        [
            MenuEntry.Item("Open selected", "Enter", OpenSelected),
            MenuEntry.Item("Delete selected", "Del", () => _ = DeleteSelectedAsync()),
            MenuEntry.Separator,
            MenuEntry.Item("Refresh", "F5", Refresh)
        ]);

        menu.Add("Help", () =>
        [
            MenuEntry.Item("Snapshots folder", null, () => count.Text = SnapshotLibrary.Folder)
        ]);

        var filterBar = new Border
        {
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Height = 58,
            Padding = new Thickness(Tokens.Space.S6, 0),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children = { count }
            }
        };

        Grid.SetColumn(count, 1);

        groups = new StackPanel { Spacing = Tokens.Space.S6, Margin = new Thickness(Tokens.Space.S6) };

        var layout = new DockPanel();

        DockPanel.SetDock(menu, Dock.Top);
        layout.Children.Add(menu);

        DockPanel.SetDock(filterBar, Dock.Top);
        layout.Children.Add(filterBar);

        layout.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = groups
        });

        Content = layout;

        KeyDown += OnKeyDown;

        // Housekeeping for keys orphaned by editing, done once when the library is opened rather
        // than on a timer nobody asked for.
        Task.Run(ThumbnailCache.Prune);

        Closed += (_, _) =>
        {
            work.Cancel();
            thumbnails.Dispose();
        };

        Refresh();
        menu.MarkActive("Library");
    }

    /// <summary>Raised with the snapshot the user chose to edit.</summary>
    public event Action<string>? Chosen;

    void Refresh()
    {
        work.Cancel();
        work.Dispose();
        work = new CancellationTokenSource();

        selected = null;
        selectedCell = null;
        groups.Children.Clear();

        var entries = SnapshotLibrary.List();
        var items = SnapshotItem.Build(entries, thumbnails, work.Token);

        count.Text = entries.Count == 1 ? "1 capture" : $"{entries.Count} captures";

        if (entries.Count == 0)
        {
            groups.Children.Add(Labels.Body($"No captures in {SnapshotLibrary.Folder}", 13, Tokens.Neutral600Brush));
            return;
        }

        foreach (var day in items.GroupBy(item => item.Entry.Modified.Date).OrderByDescending(group => group.Key))
        {
            groups.Children.Add(Group(day.Key, day.ToList()));
        }

        foreach (var item in items)
        {
            _ = item.LoadContentsAsync(Reading, work.Token);
        }
    }

    Control Group(DateTime day, IReadOnlyList<SnapshotItem> items)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S3,
            Children =
            {
                Labels.Heading(DayName(day), 13, 0.18, Tokens.Neutral800Brush),
                Labels.Body(items.Count == 1 ? "1 capture" : $"{items.Count} captures", 12.5, Tokens.Neutral500Brush)
            }
        };

        var rule = new Border
        {
            Height = 1,
            Background = Tokens.DividerBrush,
            Margin = new Thickness(0, Tokens.Space.S2, 0, Tokens.Space.S4)
        };

        // A wrap panel rather than a fixed five-column grid, so a resized window uses the width it
        // has instead of leaving a gutter down one side.
        var cells = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var item in items)
        {
            cells.Children.Add(Cell(item));
        }

        return new StackPanel { Children = { header, rule, cells } };
    }

    static string DayName(DateTime day)
    {
        if (day == DateTime.Today)
        {
            return $"TODAY · {day:d MMMM}";
        }

        return day == DateTime.Today.AddDays(-1)
            ? $"YESTERDAY · {day:d MMMM}"
            : day.ToString("dddd d MMMM");
    }

    Control Cell(SnapshotItem item)
    {
        var picture = new Image { Stretch = Stretch.UniformToFill };
        picture.Bind(Image.SourceProperty, new Avalonia.Data.Binding(nameof(SnapshotItem.Thumbnail)) { Source = item });

        var frame = new Border
        {
            Width = ThumbnailWidth,
            Height = ThumbnailHeight,
            Background = Tokens.Neutral200Brush,
            CornerRadius = Tokens.Radius,
            ClipToBounds = true,
            Child = picture
        };

        var name = Labels.Body(item.ShortName, 13, Tokens.Neutral900Brush);
        name.TextTrimming = TextTrimming.CharacterEllipsis;

        var time = Labels.Body(item.Time, 11.5, Tokens.Neutral500Brush);
        time.HorizontalAlignment = HorizontalAlignment.Right;

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(time, 1);
        titleRow.Children.Add(name);
        titleRow.Children.Add(time);

        var contents = Labels.Body(string.Empty, 11.5, Tokens.Accent700Brush);
        contents.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(SnapshotItem.Contents)) { Source = item });

        var meta = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var size = Labels.Body(item.Summary.Split("   ").Last(), 11.5, Tokens.Neutral600Brush);
        Grid.SetColumn(size, 0);
        Grid.SetColumn(contents, 1);
        meta.Children.Add(size);
        meta.Children.Add(contents);

        var cell = new Border
        {
            Padding = new Thickness(2),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            CornerRadius = Tokens.Radius,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, Tokens.Space.S6, Tokens.Space.S6),
            Child = new StackPanel
            {
                Width = ThumbnailWidth,
                Spacing = Tokens.Space.S1,
                Children = { Blueprint.Wrap(frame), titleRow, meta }
            }
        };

        ToolTip.SetTip(cell, item.Name);

        cell.PointerPressed += (_, e) =>
        {
            Select(item, cell);

            // Double click opens. In a wall of similar-looking pictures a single click is how you
            // look closer, not how you commit.
            if (e.ClickCount >= 2)
            {
                OpenSelected();
            }
        };

        return cell;
    }

    void Select(SnapshotItem item, Border cell)
    {
        if (selectedCell is { } previous)
        {
            previous.BorderBrush = Brushes.Transparent;
            previous.BoxShadow = default;
        }

        selected = item;
        selectedCell = cell;

        cell.BorderBrush = Tokens.AccentBrush;
        cell.BoxShadow = Tokens.ShadowMd;
    }

    void OpenSelected()
    {
        if (selected is { } item)
        {
            Chosen?.Invoke(item.Entry.Path);
            Close();
        }
    }

    static void NewCapture()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("snapshotkit") { UseShellExecute = false };
            startInfo.ArgumentList.Add("capture");
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            Crash.Record("could not start a capture", exception);
        }
    }

    async Task DeleteSelectedAsync()
    {
        if (selected is not { } item)
        {
            return;
        }

        // Deleting a capture is not undoable, so it gets a question. The wording says what will be
        // gone rather than asking whether the user is sure.
        var confirmed = await Confirm.DeleteAsync(this, "Delete capture",
            $"{item.Name} will be deleted permanently.\nAny images you already exported are unaffected.");

        if (!confirmed)
        {
            return;
        }

        try
        {
            SnapshotLibrary.Delete(item.Entry);
            Refresh();
        }
        catch (Exception exception)
        {
            count.Text = $"Could not delete {item.Name}: {exception.Message}";
        }
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                OpenSelected();
                break;

            case Key.Delete or Key.Back:
                _ = DeleteSelectedAsync();
                break;

            case Key.F5:
                Refresh();
                break;

            case Key.Escape:
                menu.CloseAll();
                break;

            case Key.W when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                Close();
                break;
        }
    }
}
