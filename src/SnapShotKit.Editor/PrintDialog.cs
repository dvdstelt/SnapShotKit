using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>What the print dialog came back with: everything needed to make the page and send it.</summary>
/// <param name="Printer">Where it goes, or null when it is to be written out as a PDF instead.</param>
public sealed record PrintRequest(PrintSettings Settings, PrintLayout Layout, int Copies, string? Printer);

/// <summary>
/// Where on the page the picture goes, how large, on what paper, and how many.
///
/// The application's own dialog rather than the desktop's. The desktop's knows about printers and
/// nothing about this picture: it cannot show where on the sheet it will land, let it be dragged to
/// the middle, or say that at this size it prints at ninety pixels to the inch and will look it.
/// Those are the questions somebody printing a screenshot actually has, so they are asked here,
/// beside a sheet of paper that shows the answer, and the printer is only told what was decided.
///
/// The sheet on the left and the settings on the right both work on the one
/// <see cref="PrintLayout"/>, so a number typed moves the picture and a picture dragged changes the
/// number, and neither can be left behind.
/// </summary>
public static class PrintDialog
{
    const double PreviewSize = 400;
    const double SettingsWidth = 360;

    static readonly double[] Margins = [0, 5, 10, 15, 20];
    static readonly Paper[] Papers = [Paper.A3, Paper.A4, Paper.A5, Paper.Letter, Paper.Legal];

    /// <summary>Asks, and returns what was chosen, or nothing if the dialog was cancelled.</summary>
    /// <param name="picture">The canvas as it will be printed, already rendered.</param>
    public static async Task<PrintRequest?> AskAsync(Window owner, PrintSettings current, Bitmap picture)
    {
        // A copy, so a cancelled dialog leaves the remembered settings exactly as they were.
        var settings = current.Copy();
        var layout = new PrintLayout(picture.PixelSize, settings);

        var printers = await Printing.PrintersAsync();

        // The one remembered if it is still there, else the system's own choice, else a file.
        var printer = printers.FirstOrDefault(known => known.Name == settings.Printer)?.Name
            ?? printers.FirstOrDefault()?.Name;

        var copies = 1;
        var accepted = false;

        var dialog = new Window
        {
            Title = "Print",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Tokens.BgBrush
        };

        var preview = new PagePreview(layout, picture, PreviewSize);

        // ---- Size, which the preview changes too, so it is shown through one function ----------

        var sharpness = Labels.Body(string.Empty, 11.5, Tokens.Neutral500Brush);

        Segmented? fill = null;

        // Made first and told what to do afterwards, because what each does ends in showing both.
        var width = new MillimetreField();
        var height = new MillimetreField();

        void Show()
        {
            // A field being typed in is left alone, or the number would change under the caret.
            if (!width.Box.IsFocused)
            {
                width.Box.Text = Millimetres(layout.Width);
            }

            if (!height.Box.IsFocused)
            {
                height.Box.Text = Millimetres(layout.Height);
            }

            sharpness.Text = Sharpness(layout);
            preview.InvalidateVisual();
        }

        // Anything done by hand, as against asking for a fit, leaves no fit chosen.
        void Edit(Action change)
        {
            change();
            fill?.Select(-1);
            Show();
        }

        width.Chosen = typed => Edit(() => layout.Resize(typed));
        height.Chosen = typed => Edit(() => layout.ResizeByHeight(typed));

        preview.Changed += () =>
        {
            fill?.Select(-1);
            Show();
        };

        fill = new Segmented(["Page width", "Whole page", "Actual size"], index =>
        {
            switch (index)
            {
                case 0:
                    layout.FitWidth();
                    break;

                case 1:
                    layout.FitPage();
                    break;

                default:
                    layout.ActualSize();
                    break;
            }

            Show();
        });

        // Things to do rather than states to be in, so nothing stays lit once it is done. Posted,
        // because the control marks the segment chosen after it has called back.
        Segmented? centre = null;

        centre = new Segmented(["Across", "Down", "Both"], index =>
        {
            if (index != 1)
            {
                layout.CentreAcross();
            }

            if (index != 0)
            {
                layout.CentreDown();
            }

            Show();
            Dispatcher.UIThread.Post(() => centre?.Select(-1));
        });

        // ---- The page ---------------------------------------------------------------------------

        void Repage()
        {
            layout.Repage(settings);
            Show();
        }

        var paper = new Segmented([.. Papers.Select(size => size.ToString())], index =>
        {
            settings.Paper = Papers[index];
            Repage();
        });

        paper.Select(Array.IndexOf(Papers, settings.Paper));

        var orientation = new Segmented(["Portrait", "Landscape"], index =>
        {
            settings.Landscape = index == 1;
            Repage();
        });

        orientation.Select(settings.Landscape ? 1 : 0);

        var margin = new Segmented([.. Margins.Select(size => size == 0 ? "None" : $"{size:0} mm")], index =>
        {
            settings.Margin = Margins[index];
            Repage();
        });

        margin.Select(Array.IndexOf(Margins, settings.Margin));

        // ---- The printer ------------------------------------------------------------------------

        Control? send = null;

        var count = Stepper(copies, 1, 999, chosen => copies = chosen);

        var destination = Picker(printers, printer, chosen =>
        {
            printer = chosen;
            Relabel();
        });

        // Copies of a file are copies nobody asked for, and the button says what it is about to do.
        void Relabel()
        {
            count.Frame.IsEnabled = printer is not null;
            count.Frame.Opacity = printer is not null ? 1 : 0.4;

            // The primary button is a button inside its blueprint frame.
            if (send is Decorator { Child: Button button })
            {
                Caption(button, printer is null ? "Save PDF…" : "Print");
            }
        }

        void Accept()
        {
            // A size still being typed counts, the same as one that was tabbed away from.
            // Only the one being typed in: the others show a rounded number, and committing that
            // would move the picture by the rounding.
            if (width.Box.IsFocused)
            {
                width.Commit();
            }

            if (height.Box.IsFocused)
            {
                height.Commit();
            }

            count.Commit();

            accepted = true;
            dialog.Close();
        }

        send = Buttons.Primary("Print", null, Accept);

        var sizes = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S2,
            Children =
            {
                width.Frame,
                Unit("×"),
                height.Frame,
                Unit("mm")
            }
        };

        var right = new StackPanel
        {
            Width = SettingsWidth,
            Spacing = Tokens.Space.S3,
            Children =
            {
                Labels.Heading("PRINT", 13, 0.18, Tokens.Neutral800Brush),
                Row("Printer", destination),
                Row("Copies", count.Frame),
                Rule(),
                Row("Paper", paper),
                Row("Orientation", orientation),
                Row("Margins", margin),
                Rule(),
                Row("Fill", fill),
                Row("Size", sizes),
                Note(sharpness),
                Row("Centre", centre),
                Note(Labels.Body("Drag the picture to move it and a corner to size it. It pulls to the middle of the page; hold Alt to stop it.",
                    11.5, Tokens.Neutral500Brush))
            }
        };

        if (printers.Count == 0)
        {
            right.Children.Insert(2, Note(Labels.Body("CUPS reports no printers, so the page can only be saved as a PDF.",
                11.5, Tokens.Neutral500Brush)));
        }

        right.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S2,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, Tokens.Space.S3, 0, 0),
            Children =
            {
                Buttons.Secondary("Cancel", null, () => dialog.Close()),
                send
            }
        });

        dialog.Content = new Border
        {
            Padding = new Thickness(Tokens.Space.S6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.Space.S6,
                Children =
                {
                    new Border { VerticalAlignment = VerticalAlignment.Top, Child = Blueprint.Wrap(preview) },
                    right
                }
            }
        };

        Relabel();
        Show();

        // Enter and Escape, tunnelled so the segmented controls do not have to let them past. Enter
        // inside a number commits the number instead: somebody typing a width and pressing Enter
        // wants to see the width, not a sheet of paper coming out of the printer.
        dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter when e.Source is not TextBox:
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

        if (!accepted)
        {
            return null;
        }

        settings.Printer = printer ?? settings.Printer;
        return new PrintRequest(settings, layout, copies, printer);
    }

    static string Millimetres(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>
    /// How much of the page it takes and how sharp it will be, which are the two things a size in
    /// millimetres does not say by itself.
    ///
    /// Below about 150 pixels to the inch a printed screenshot starts to show its pixels, so that
    /// is said rather than left to be found out on paper.
    /// </summary>
    static string Sharpness(PrintLayout layout)
    {
        var share = layout.Width / layout.PageWidth * 100;
        var line = $"{share:0}% of the page width, at {layout.PixelsPerInch:0} pixels to the inch.";

        return layout.PixelsPerInch < 150 ? line + " Large enough that it will print soft." : line;
    }

    /// <summary>A labelled row: the name on the left at a fixed width, the control filling what is left.</summary>
    static Control Row(string label, Control control)
    {
        var name = Labels.Body(label, 12.5, Tokens.Neutral600Brush);
        name.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{LabelWidth.ToString(CultureInfo.InvariantCulture)},*") };

        // A row of three choices is as wide as its three choices, not as wide as the column.
        if (control is Segmented)
        {
            control.HorizontalAlignment = HorizontalAlignment.Left;
        }

        Grid.SetColumn(name, 0);
        Grid.SetColumn(control, 1);

        grid.Children.Add(name);
        grid.Children.Add(control);

        return grid;
    }

    const double LabelWidth = 84;

    /// <summary>A line under a setting, indented to the controls it belongs to.</summary>
    static Control Note(TextBlock text)
    {
        text.TextWrapping = TextWrapping.Wrap;
        text.Margin = new Thickness(LabelWidth, 0, 0, 0);
        return text;
    }

    static Control Rule() => new Border
    {
        Height = 1,
        Background = Tokens.DividerBrush,
        Margin = new Thickness(0, Tokens.Space.S1)
    };

    static TextBlock Unit(string text)
    {
        var unit = Labels.Body(text, 12.5, Tokens.Neutral600Brush);
        unit.VerticalAlignment = VerticalAlignment.Center;
        return unit;
    }

    static void Caption(Button button, string text)
    {
        if (button.Content is Panel { Children: [.., TextBlock label] })
        {
            label.Text = text;
        }
    }

    // ---- Fields ------------------------------------------------------------------------------------

    static Border Framed(Control child) => new()
    {
        Child = child,
        Height = 28,
        Background = Tokens.BgBrush,
        BorderBrush = Tokens.DividerBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = Tokens.Radius,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left
    };

    static TextBox Box(double width) => new()
    {
        Theme = TextFields.Bare,
        FontFamily = Tokens.Fonts.Body,
        FontSize = 12.5,
        Foreground = Tokens.Neutral800Brush,
        CaretBrush = Tokens.Neutral800Brush,
        SelectionBrush = Tokens.Accent300Brush,
        SelectionForegroundBrush = Tokens.Neutral900Brush,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Width = width
    };

    /// <summary>
    /// A size in millimetres, committed on Enter or on leaving the field, since a picture resized
    /// on every keystroke would go to 1, then 12, then 120 on the way to typing 120.
    ///
    /// Takes a comma or a point for the decimal, whichever the keyboard in front of it has.
    /// </summary>
    sealed class MillimetreField
    {
        /// <summary>What the field held when it was entered, so that leaving it untouched changes nothing.</summary>
        string shown = string.Empty;

        public MillimetreField()
        {
            Box = PrintDialog.Box(52);

            Box.GotFocus += (_, _) => shown = Box.Text ?? string.Empty;
            Box.LostFocus += (_, _) => Commit();

            Box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    Commit();
                    shown = Box.Text ?? string.Empty;
                }
            };

            var frame = Framed(Box);
            frame.Padding = new Thickness(Tokens.Space.S2, 0);
            Frame = frame;
        }

        public Control Frame { get; }

        public TextBox Box { get; }

        public Action<double>? Chosen { get; set; }

        public void Commit()
        {
            // Only what was actually typed. Leaving a field that was never touched would otherwise
            // put back the rounded number it displays, and move the picture by the rounding.
            if (Box.Text == shown)
            {
                return;
            }

            if (double.TryParse(Box.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var typed) && typed > 0)
            {
                Chosen?.Invoke(typed);
            }
        }
    }

    /// <summary>A whole number with a step either side of it, framed as one object the way the zoom readout is.</summary>
    static (Control Frame, Action Commit) Stepper(int start, int minimum, int maximum, Action<int> chosen)
    {
        var value = start;
        var box = Box(40);
        box.Text = $"{value}";

        void Set(int wanted)
        {
            value = Math.Clamp(wanted, minimum, maximum);
            box.Text = $"{value}";
            chosen(value);
        }

        void Commit() => Set(int.TryParse(box.Text, out var typed) ? typed : value);

        box.LostFocus += (_, _) => Commit();

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };

        Control Cell(string icon, bool first, Action clicked)
        {
            var cell = new Border
            {
                Child = Lucide.Icon(icon, 14, Tokens.Neutral800Brush),
                Padding = new Thickness(Tokens.Space.S2, 0),
                Background = Tokens.BgBrush,
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderBrush = Tokens.DividerBrush,
                BorderThickness = new Thickness(first ? 0 : 1, 0, first ? 1 : 0, 0)
            };

            cell.PointerPressed += (_, _) => clicked();
            cell.PointerEntered += (_, _) => cell.Background = Tokens.Neutral200Brush;
            cell.PointerExited += (_, _) => cell.Background = Tokens.BgBrush;

            return cell;
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Cell(Lucide.Minus, first: true, () => Set(value - 1)),
                box,
                Cell(Lucide.Plus, first: false, () => Set(value + 1))
            }
        };

        return (Framed(row), Commit);
    }

    /// <summary>
    /// The printer, as a field that opens onto the list of them.
    ///
    /// Saving a PDF is on the same list, below the printers, because it is the same decision: where
    /// the page goes. It is also what is left when there is no printer at all.
    /// </summary>
    static Control Picker(IReadOnlyList<Printing.Printer> printers, string? current, Action<string?> chosen)
    {
        const string File = "Save as PDF";

        var name = Labels.Body(current ?? File, 12.5, Tokens.Neutral800Brush);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;

        var chevron = Lucide.Icon(Lucide.More, 14, Tokens.Neutral600Brush);
        chevron.VerticalAlignment = VerticalAlignment.Center;

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        Grid.SetColumn(name, 0);
        Grid.SetColumn(chevron, 1);

        content.Children.Add(name);
        content.Children.Add(chevron);

        var field = Framed(content);
        field.Padding = new Thickness(Tokens.Space.S3, 0, Tokens.Space.S2, 0);
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        field.Cursor = new Cursor(StandardCursorType.Hand);

        // Built once and kept in the tree. A popup moved to a new parent stops opening.
        var popup = new Popup
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            PlacementTarget = field,
            IsLightDismissEnabled = true
        };

        void Pick(string? printer)
        {
            name.Text = printer ?? File;
            chosen(printer);
        }

        List<MenuEntry> entries = [.. printers.Select(printer => MenuEntry.Item(printer.Name, printer.IsDefault ? "Default" : null, () => Pick(printer.Name)))];

        if (entries.Count > 0)
        {
            entries.Add(MenuEntry.Separator);
        }

        entries.Add(MenuEntry.Item(File + "…", null, () => Pick(null)));

        popup.Child = PopupMenu.Build(entries, popup);

        field.PointerPressed += (_, _) => popup.IsOpen = !popup.IsOpen;
        field.PointerEntered += (_, _) => field.Background = Tokens.Neutral100Brush;
        field.PointerExited += (_, _) => field.Background = Tokens.BgBrush;

        return new Panel { Children = { field, new Panel { Width = 0, Height = 0, Children = { popup } } } };
    }
}
