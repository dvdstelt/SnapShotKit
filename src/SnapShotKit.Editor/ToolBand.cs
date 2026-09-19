using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>
/// The drawing tools along the top of the window, and their styles and settings in a sidebar down
/// its right.
///
/// Split across the two because a single band ran out of width. Every tool that gained a setting
/// took it from the room the others' settings had, and a band that scrolls sideways hides exactly
/// the control somebody is looking for. A sidebar grows downward, where there is room to spare, and
/// it is where Snagit keeps the same things. One class still owns both, since they are one set of
/// controls answering to one tool or selection, and only where they are shown differs.
///
/// The settings shown change with the tool, but their order does not: colour is always above
/// weight. The hand learns where a control is once, and switching tools never moves it past
/// another one.
///
/// Every setting offers a handful of presets and a way to reach any other value. The presets carry
/// almost all of the use; being unable to reach the one value that is not on the list is the kind
/// of limit that makes a tool feel like a toy.
///
/// The styles lead the sidebar, because a look is the unit anyone actually works in: a red box with
/// no fill is one decision, not three controls in a row. The settings under them are for the times
/// the answer is not on the list, and they say what they are set to whether it came from a style or
/// from them.
/// </summary>
public sealed class ToolBand : Border
{
    /// <summary>Stroke weights offered as presets. Anything else is one click further on.</summary>
    static readonly double[] Weights = [2, 4, 6, 8];

    static readonly double[] TextSizes = [15, 22, 30, 44];

    const double BandHeight = 68;

    /// <summary>
    /// Wide enough for the longest label under its icon. Every cell is the same width, so the row
    /// reads as a set of equal tools rather than as words of different lengths.
    /// </summary>
    const double CellWidth = 54;

    const double CellHeight = 50;

    /// <summary>Four styles to a row, with the sidebar's padding either side.</summary>
    const double SidebarWidth = 288;

    readonly List<(EditorTool Tool, Border Cell, Control Glyph, TextBlock Label)> tools = [];

    readonly ColourField colour;
    readonly ColourField fillColour;
    readonly ColourField textBackColour;
    readonly NumberField stepNumber;
    readonly NumberField stepSize;
    readonly Segmented textBack;
    readonly NumberField weight;
    readonly NumberField blur;
    readonly NumberField textSize;
    readonly Segmented head;
    readonly Segmented fill;
    readonly Segmented cutDirection;

    readonly StyleGrid style;

    readonly Control stylesSection;
    readonly StackPanel settings;
    readonly Control settingsSection;
    readonly TextBlock nothingToSet;

    readonly TextBox canvasWidth;
    readonly TextBox canvasHeight;

    readonly Control colourGroup;
    readonly Control fillColourGroup;
    readonly Control textBackGroup;
    readonly Control textBackColourGroup;
    readonly Control stepNumberGroup;
    readonly Control stepSizeGroup;
    readonly Control weightGroup;
    readonly Control headGroup;
    readonly Segmented shape;
    readonly Control shapeGroup;
    readonly Segmented hide;
    readonly Control hideGroup;
    readonly NumberField dim;
    readonly NumberField lens;
    readonly Control zoomGroup;
    readonly Control dimGroup;
    readonly Control fillGroup;
    readonly Control blurGroup;
    readonly Control textSizeGroup;
    readonly Control cutDirectionGroup;
    readonly Control canvasWidthGroup;
    readonly Control canvasHeightGroup;
    readonly Control canvasFitGroup;
    readonly Control pictureGroup;

    readonly TextBlock zoomLabel = Labels.Body("100%", 12.5, Tokens.Neutral800Brush);

    readonly TextBlock canvasSizeLabel = Labels.Body(string.Empty, 12.5, Tokens.Neutral800Brush);
    readonly TextBlock canvasFixedLabel = Labels.Body("fixed", 10.5, Tokens.Neutral500Brush);
    readonly Control canvasIcon = Lucide.Icon(Lucide.Crop, 14, Tokens.Neutral800Brush);
    readonly Border canvasReadout;

    const string CanvasTip = "Resize canvas  (C)\nDrag an edge in to crop, or out to add transparent space.\nEnter applies, Escape backs out.";

    public ToolBand()
    {
        Background = Tokens.BgBrush;
        BorderBrush = Tokens.DividerBrush;
        BorderThickness = new Thickness(0, 0, 0, 1);
        Height = BandHeight;

        colour = new ColourField(value => ColourChosen?.Invoke(value));
        fillColour = new ColourField(value => FillColourChosen?.Invoke(value));

        weight = new NumberField("Weight", Weights, 1, 40, value => WeightChosen?.Invoke(value));
        blur = new NumberField("Blur", BlurAnnotation.Presets.Select(step => (double)step).ToArray(), 1, 100,
            value => BlurChosen?.Invoke((int)Math.Round(value)));
        textSize = new NumberField("Size", TextSizes, 8, 200, value => TextSizeChosen?.Invoke(value));

        textBackColour = new ColourField(value => TextBackColourChosen?.Invoke(value));
        textBack = new Segmented(["None", "Solid"], index => TextBackChosen?.Invoke(index == 1));

        // A marker's number is a plain field: new ones count up on their own, but two markers
        // saying the same thing is a real thing to want.
        stepNumber = new NumberField("Number", [1, 2, 3, 4], 1, 999,
            value => StepNumberChosen?.Invoke((int)Math.Round(value)));

        stepSize = new NumberField("Size", [28, 36, 48, 64], 12, 200, value => StepSizeChosen?.Invoke(value));

        // None makes it a line, which is an arrow that points at nothing rather than another tool.
        head = new Segmented(["None", "Single", "Double"], index => HeadsChosen?.Invoke(index));

        lens = new NumberField("Magnification", MagnifyAnnotation.Presets, 1, 16, value => LensChosen?.Invoke(value));

        dim = new NumberField("Dim", SpotlightAnnotation.Presets.Select(step => (double)step).ToArray(), 1, 100,
            value => DimChosen?.Invoke((int)Math.Round(value)));

        shape = new Segmented(["Rectangle", "Ellipse"], index => ShapeChosen?.Invoke(index == 1));

        // In order of how much each leaves behind. A blur can sometimes be worked backwards on
        // text; squares throw the detail away; a bar leaves nothing.
        hide = new Segmented(["Blur", "Pixelate", "Solid"], index => HideChosen?.Invoke((HideMode)index));
        ToolTip.SetTip(hide, "How what is underneath is hidden.\nA light blur over text can sometimes be reversed. For anything that must not be read, use Solid.");

        // Automatic first, because it is right nearly always: a band is almost never so small that
        // the way it was dragged does not say which way it runs.
        cutDirection = new Segmented(["Auto", "Horizontal", "Vertical"], index => CutDirectionChosen?.Invoke(index switch
        {
            1 => CutAxis.Rows,
            2 => CutAxis.Columns,
            _ => null
        }));
        fill = new Segmented(["None", "Solid"], index => FillChosen?.Invoke(index == 1));

        style = new StyleGrid(chosen => StyleChosen?.Invoke(chosen));

        // Every group is captioned, the colours included, so each setting says what it is without
        // leaning on what happens to sit beside it.
        colourGroup = Group("Colour", colour);
        fillColourGroup = Group("Fill colour", fillColour);
        weightGroup = Group("Weight", weight);
        headGroup = Group("Head", head);
        shapeGroup = Group("Shape", shape);
        hideGroup = Group("Hide with", hide);
        dimGroup = Group("Dim", dim);
        zoomGroup = Group("Magnification", lens);
        fillGroup = Group("Fill", fill);
        blurGroup = Group("Blur", blur);
        textSizeGroup = Group("Size", textSize);
        textBackGroup = Group("Background", textBack);
        textBackColourGroup = Group("Background colour", textBackColour);
        stepNumberGroup = Group("Number", stepNumber);
        stepSizeGroup = Group("Size", stepSize);

        cutDirectionGroup = Group("Band", cutDirection);
        ToolTip.SetTip(cutDirection, "Which way the band runs.\nHorizontal takes rows out and makes the picture shorter; vertical takes columns and makes it narrower.");

        Control widthBox;
        Control heightBox;
        (widthBox, canvasWidth) = SizeBox(value => CanvasWidthChosen?.Invoke(value));
        (heightBox, canvasHeight) = SizeBox(value => CanvasHeightChosen?.Invoke(value));

        canvasWidthGroup = Group("Width", widthBox);
        canvasHeightGroup = Group("Height", heightBox);
        canvasFitGroup = Group("Canvas", TextAction("Fit to pictures", () => CanvasFitRequested?.Invoke()));

        pictureGroup = Group("Picture", new StackPanel
        {
            Spacing = Tokens.Space.S1,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                TextAction("Flip horizontally", () => PictureFlipRequested?.Invoke(true)),
                TextAction("Flip vertically", () => PictureFlipRequested?.Invoke(false)),
                TextAction("Actual size", () => PictureSizeRestoreRequested?.Invoke())
            }
        });

        // Every settings group lives in this one column, in a fixed order. Only the ones the active
        // tool uses are visible; the rest collapse, and the ones that remain keep their order.
        settings = new StackPanel { Spacing = Tokens.Space.S4 };

        foreach (var group in new[]
                 {
                     colourGroup, weightGroup, blurGroup, textSizeGroup, stepNumberGroup, stepSizeGroup,
                     headGroup, shapeGroup, hideGroup, dimGroup, zoomGroup, fillGroup, fillColourGroup, textBackGroup, textBackColourGroup,
                     cutDirectionGroup, canvasWidthGroup, canvasHeightGroup, canvasFitGroup, pictureGroup
                 })
        {
            settings.Children.Add(group);
        }

        stylesSection = Section("Styles", style);
        settingsSection = Section("Properties", settings);

        nothingToSet = Labels.Body("Nothing to set for this tool. Select something on the canvas to change how it looks.",
            12, Tokens.Neutral500Brush);
        nothingToSet.TextWrapping = TextWrapping.Wrap;

        Sidebar = new Border
        {
            Width = SidebarWidth,
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new StackPanel
                {
                    Margin = new Thickness(Tokens.Space.S4),
                    Spacing = Tokens.Space.S6,
                    Children = { stylesSection, settingsSection, nothingToSet }
                }
            }
        };

        // The tools in the middle of the window, where the eye already is, and undo and redo at the
        // end of the same row.
        var tools = BuildToolCells();
        tools.HorizontalAlignment = HorizontalAlignment.Center;

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S3,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        right.Children.Add(TextAction("Undo", () => UndoRequested?.Invoke()));
        right.Children.Add(TextAction("Redo", () => RedoRequested?.Invoke()));

        canvasReadout = BuildCanvasReadout();

        // Built here, since they are wired to the same events, but shown along the foot of the
        // window. How large the picture is and how large it is being shown are about the view rather
        // than about drawing, and the band's width is what the busiest tool's settings run out of.
        ViewControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { canvasReadout, BuildZoom() }
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(Tokens.Space.S4, 0) };
        Grid.SetColumn(tools, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(tools);
        grid.Children.Add(right);

        Child = grid;
    }

    /// <summary>The styles and settings, for the window to put down its right-hand side.</summary>
    public Control Sidebar { get; }

    /// <summary>The canvas size and the zoom, for the window to put along its foot. See the constructor.</summary>
    public Control ViewControls { get; }

    public event Action<EditorTool>? ToolChosen;
    public event Action<string>? ColourChosen;
    public event Action<string>? FillColourChosen;
    public event Action<double>? WeightChosen;
    public event Action<int>? HeadsChosen;
    public event Action<bool>? ShapeChosen;
    public event Action<HideMode>? HideChosen;
    public event Action<int>? DimChosen;
    public event Action<double>? LensChosen;
    public event Action<bool>? FillChosen;
    public event Action<int>? BlurChosen;
    public event Action<double>? TextSizeChosen;
    public event Action<bool>? TextBackChosen;
    public event Action<string>? TextBackColourChosen;
    public event Action<int>? StepNumberChosen;
    public event Action<double>? StepSizeChosen;

    /// <summary>A step up or down the zoom ladder: 1 in, -1 out.</summary>
    public event Action<int>? ZoomStepped;

    public event Action? ZoomFitRequested;
    /// <summary>A whole look, chosen in one go.</summary>
    public event Action<AnnotationStyle>? StyleChosen;

    /// <summary>Which way a cut runs, or null to take it from the drag.</summary>
    public event Action<CutAxis?>? CutDirectionChosen;

    public event Action<int>? CanvasWidthChosen;
    public event Action<int>? CanvasHeightChosen;
    public event Action? CanvasFitRequested;

    /// <summary>The canvas size was clicked: open a resize, or back out of the one that is open.</summary>
    public event Action? CanvasResizeRequested;

    /// <summary>Mirror the selected picture: true left to right, false top to bottom.</summary>
    public event Action<bool>? PictureFlipRequested;

    public event Action? PictureSizeRestoreRequested;
    public event Action? UndoRequested;
    public event Action? RedoRequested;

    Control BuildToolCells()
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        // Named under their icons, not only in a tooltip. A row of icons is quick to use once it is
        // learned and slow to learn, and "which of these is blur" should not need a hover to answer.
        foreach (var (tool, glyph, name, tip) in new[]
                 {
                     (EditorTool.Select, Lucide.Select, "Select", "Select and move  (V)"),
                     (EditorTool.Arrow, Lucide.Arrow, "Arrow", "Arrow  (A)"),
                     (EditorTool.Box, Lucide.Box, "Box", "Box  (B)"),
                     (EditorTool.Pen, Lucide.Pen, "Pen", "Pen  (P)\nDraws wherever the pointer goes."),
                     (EditorTool.Blur, Lucide.Blur, "Blur", "Blur  (L)"),
                     (EditorTool.Magnify, Lucide.Magnify, "Magnify", "Magnify  (G)\nA lens: drag it out over a detail and it shows that spot larger."),
                     (EditorTool.Spotlight, Lucide.Spotlight, "Spotlight", "Spotlight  (O)\nDims everything except the regions dragged out."),
                     (EditorTool.Step, Lucide.Step, "Marker", "Numbered marker  (N)\nEach one takes the next number up."),
                     (EditorTool.Text, Lucide.Text, "Text", "Text  (T)\nType in place. Shift+Enter for a new line, Enter to finish."),
                     (EditorTool.Cut, Lucide.Cut, "Cut", "Cut out  (X)\nDrag down the picture to take a band of rows out of it, or across to take columns.\nWhat is left closes up.")
                 })
        {
            var icon = Lucide.Icon(glyph, 19, Tokens.Neutral800Brush);

            var label = Labels.Body(name, 11, Tokens.Neutral700Brush);
            label.HorizontalAlignment = HorizontalAlignment.Center;

            var cell = new Border
            {
                Width = CellWidth,
                Height = CellHeight,
                Background = Tokens.BgBrush,
                CornerRadius = Tokens.Radius,
                Child = new StackPanel
                {
                    Spacing = 3,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { icon, label }
                },
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            var chosen = tool;
            cell.PointerPressed += (_, _) => ToolChosen?.Invoke(chosen);
            cell.PointerEntered += (_, _) =>
            {
                if (Active != chosen)
                {
                    cell.Background = Tokens.Neutral200Brush;
                }
            };
            cell.PointerExited += (_, _) =>
            {
                if (Active != chosen)
                {
                    cell.Background = Tokens.BgBrush;
                }
            };

            ToolTip.SetTip(cell, tip);
            tools.Add((tool, cell, icon, label));
            strip.Children.Add(cell);
        }

        return strip;
    }

    public EditorTool Active { get; private set; } = EditorTool.Arrow;

    /// <summary>A part of the sidebar: a heading over a hairline, and what it heads.</summary>
    static Control Section(string heading, Control content) => new StackPanel
    {
        Spacing = Tokens.Space.S3,
        Children =
        {
            new Border
            {
                BorderBrush = Tokens.DividerBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, Tokens.Space.S1),
                Child = Labels.Heading(heading, 12, 0.16, Tokens.Neutral700Brush)
            },
            content
        }
    };

    /// <summary>A settings group: its condensed caption above the control it names.</summary>
    static Control Group(string caption, Control control)
    {
        // At its own width rather than the sidebar's. Stretched, a two-segment switch is a frame
        // with a wide empty stretch inside it that reads as a third option nobody can pick.
        control.HorizontalAlignment = HorizontalAlignment.Left;

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Labels.Heading(caption, 10.5, 0.18, Tokens.Neutral500Brush),
                control
            }
        };
    }

    static Control TextAction(string text, Action clicked)
    {
        var label = Labels.Body(text, 12.5, Tokens.Neutral700Brush);

        var cell = new Border
        {
            Child = label,
            Padding = new Thickness(Tokens.Space.S2, Tokens.Space.S1),
            Background = Tokens.BgBrush,
            CornerRadius = Tokens.Radius,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        cell.PointerPressed += (_, _) => clicked();
        cell.PointerEntered += (_, _) => cell.Background = Tokens.Neutral200Brush;
        cell.PointerExited += (_, _) => cell.Background = Tokens.BgBrush;

        return cell;
    }

    /// <summary>
    /// A number typed in full, framed like the band's other fields.
    ///
    /// Not a <see cref="NumberField"/>, which leads with presets. A canvas has no sizes worth
    /// offering as presets: the useful widths are whatever this particular picture needs, and a
    /// scale of four suggested numbers would be four wrong answers. It is committed on Enter or on
    /// leaving the field, since a canvas resized on every keystroke would resize to 1, then 19,
    /// then 192 on the way to typing 1920.
    /// </summary>
    static (Control Frame, TextBox Box) SizeBox(Action<int> chosen)
    {
        var box = new TextBox
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
            Width = 46
        };

        void Commit()
        {
            if (int.TryParse(box.Text, out var typed))
            {
                chosen(typed);
            }
        }

        box.LostFocus += (_, _) => Commit();

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };

        var frame = new Border
        {
            Child = box,
            Height = 28,
            Padding = new Thickness(Tokens.Space.S2, 0),
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            VerticalAlignment = VerticalAlignment.Center
        };

        return (frame, box);
    }

    /// <summary>
    /// The zoom readout with a step either side of it.
    ///
    /// One framed object rather than three loose controls: they are one thing in the hand even
    /// though they do three separate jobs, and the frame is the same one every other field on the
    /// band wears. The number is a button too, because the thing most often wanted after zooming
    /// in is the whole picture back.
    /// </summary>
    Control BuildZoom()
    {
        zoomLabel.VerticalAlignment = VerticalAlignment.Center;
        zoomLabel.HorizontalAlignment = HorizontalAlignment.Center;
        zoomLabel.TextAlignment = TextAlignment.Center;
        zoomLabel.MinWidth = 34;

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        row.Children.Add(ZoomCell(
            Lucide.Icon(Lucide.Minus, 14, Tokens.Neutral800Brush), "Zoom out  (Ctrl+-)", first: true,
            () => ZoomStepped?.Invoke(-1)));

        row.Children.Add(ZoomCell(zoomLabel, "Fit to window  (Ctrl+0)", first: false,
            () => ZoomFitRequested?.Invoke()));

        row.Children.Add(ZoomCell(
            Lucide.Icon(Lucide.Plus, 14, Tokens.Neutral800Brush), "Zoom in  (Ctrl++)", first: false,
            () => ZoomStepped?.Invoke(1)));

        return new Border
        {
            Child = row,
            Height = 28,
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>
    /// The canvas size, which is also the way into resizing it.
    ///
    /// Beside the zoom along the foot of the window rather than among the tools. Resizing the canvas
    /// draws nothing: it changes the document rather than what stands on it, and in a row of drawing
    /// tools it was the one that was not one. Here it says how large the picture will come out
    /// whenever anybody looks, which is worth the space on its own, and it is where Snagit puts the
    /// same thing.
    /// </summary>
    Border BuildCanvasReadout()
    {
        canvasSizeLabel.VerticalAlignment = VerticalAlignment.Center;
        canvasFixedLabel.VerticalAlignment = VerticalAlignment.Center;
        canvasFixedLabel.IsVisible = false;

        var cell = new Border
        {
            Height = 28,
            Padding = new Thickness(Tokens.Space.S2, 0),
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.Space.S2,
                Children = { canvasIcon, canvasSizeLabel, canvasFixedLabel }
            }
        };

        ToolTip.SetTip(cell, CanvasTip);

        cell.PointerPressed += (_, _) => CanvasResizeRequested?.Invoke();
        cell.PointerEntered += (_, _) => { if (Active != EditorTool.Canvas) cell.Background = Tokens.Neutral200Brush; };
        cell.PointerExited += (_, _) => { if (Active != EditorTool.Canvas) cell.Background = Tokens.BgBrush; };

        return cell;
    }

    /// <summary>
    /// Points the canvas readout at the canvas as the file would come out.
    ///
    /// "fixed" when the size was set by hand, which is the one thing about the canvas that cannot be
    /// seen by looking at it: a canvas that has stopped shrinking to fit the pictures looks the same
    /// as one that never needed to, and the difference is worth being able to find without guessing.
    /// </summary>
    public void ShowCanvas(int width, int height, bool setByHand, bool resizing)
    {
        canvasSizeLabel.Text = $"{width} × {height}";
        canvasFixedLabel.IsVisible = setByHand;

        canvasReadout.Background = resizing ? Tokens.AccentBrush : Tokens.BgBrush;
        canvasSizeLabel.Foreground = resizing ? Tokens.BgBrush : Tokens.Neutral800Brush;
        canvasFixedLabel.Foreground = resizing ? Tokens.BgBrush : Tokens.Neutral500Brush;

        if (canvasIcon is Viewbox { Child: Avalonia.Controls.Shapes.Path path })
        {
            path.Stroke = resizing ? Tokens.BgBrush : Tokens.Neutral800Brush;
        }

        ToolTip.SetTip(canvasReadout, setByHand
            ? CanvasTip + "\n\nThis size was set by hand, so the canvas stays at least this large.\nFit to pictures lets it follow them again."
            : CanvasTip);
    }

    /// <summary>One segment of the zoom control, ruled off from the one before it.</summary>
    static Control ZoomCell(Control content, string tip, bool first, Action clicked)
    {
        var cell = new Border
        {
            Child = content,
            Padding = new Thickness(Tokens.Space.S2, 0),
            Background = Tokens.BgBrush,
            CornerRadius = Tokens.Radius,
            Cursor = new Cursor(StandardCursorType.Hand),
            // Internal rules between segments, drawn as the left edge of every one but the first.
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(first ? 0 : 1, 0, 0, 0)
        };

        ToolTip.SetTip(cell, tip);

        cell.PointerPressed += (_, _) => clicked();
        cell.PointerEntered += (_, _) => cell.Background = Tokens.Neutral200Brush;
        cell.PointerExited += (_, _) => cell.Background = Tokens.BgBrush;

        return cell;
    }

    public void ShowZoom(double scale) => zoomLabel.Text = $"{scale * 100:F0}%";

    /// <summary>
    /// Points the size fields at the canvas as it now is.
    ///
    /// A field being typed in is left alone. The canvas changes on every step of a drag and on
    /// every commit, and rewriting the box under the caret would fight whoever is using it.
    /// </summary>
    public void ShowCanvasSize(int width, int height)
    {
        if (!canvasWidth.IsFocused)
        {
            canvasWidth.Text = width.ToString();
        }

        if (!canvasHeight.IsFocused)
        {
            canvasHeight.Text = height.ToString();
        }
    }

    /// <summary>
    /// Points the band at a tool and, when something is selected, at that object's own values.
    ///
    /// A selection wins over the tool's defaults, because acting on the selected object is what the
    /// user is doing; with nothing selected the band shows what the next thing drawn will look like.
    /// </summary>
    public void Sync(EditorTool tool, ToolDefaults defaults, Annotation? selected)
    {
        Active = tool;

        foreach (var (candidate, cell, glyph, label) in tools)
        {
            var active = candidate == tool;
            cell.Background = active ? Tokens.AccentBrush : Tokens.BgBrush;
            label.Foreground = active ? Tokens.BgBrush : Tokens.Neutral700Brush;

            if (glyph is Viewbox { Child: Avalonia.Controls.Shapes.Path path })
            {
                path.Stroke = active ? Tokens.BgBrush : Tokens.Neutral800Brush;
            }
        }

        // What is shown follows the selection when there is one, and the tool otherwise. Selecting
        // an object is a statement about what you mean to work on, whichever tool is in hand.
        var kind = selected switch
        {
            ArrowAnnotation => EditorTool.Arrow,
            BoxAnnotation => EditorTool.Box,
            BlurAnnotation => EditorTool.Blur,
            SpotlightAnnotation => EditorTool.Spotlight,
            PenAnnotation => EditorTool.Pen,
            MagnifyAnnotation => EditorTool.Magnify,
            TextAnnotation => EditorTool.Text,
            StepAnnotation => EditorTool.Step,

            // A picture has no drawing tool of its own, and none of the settings a tool has. Select
            // is the tool that picks one up, and it shows nothing, which leaves the picture's own
            // group alone on the band.
            ImageAnnotation => EditorTool.Select,
            _ => tool
        };

        // The styles on offer follow the kind, and the one marked is whichever the selection or the
        // tool is already wearing. Changing a single setting away from a style unmarks it rather
        // than leaving the row claiming a look that is no longer being worn.
        var styles = AnnotationStyles.For(kind);
        var worn = styles.FirstOrDefault(candidate => selected is not null
            ? selected.WearsStyle(candidate.Look)
            : defaults.Wears(candidate.Look));

        style.Show(styles, worn);
        stylesSection.IsVisible = styles.Count > 0;

        colourGroup.IsVisible = kind is EditorTool.Arrow or EditorTool.Box or EditorTool.Text or EditorTool.Step or EditorTool.Pen;
        weightGroup.IsVisible = kind is EditorTool.Arrow or EditorTool.Box or EditorTool.Pen;
        headGroup.IsVisible = kind is EditorTool.Arrow;
        shapeGroup.IsVisible = kind is EditorTool.Box;
        hideGroup.IsVisible = kind is EditorTool.Blur;
        dimGroup.IsVisible = kind is EditorTool.Spotlight;
        zoomGroup.IsVisible = kind is EditorTool.Magnify;
        fillGroup.IsVisible = kind is EditorTool.Box;
        blurGroup.IsVisible = kind is EditorTool.Blur;
        textSizeGroup.IsVisible = kind is EditorTool.Text;
        textBackGroup.IsVisible = kind is EditorTool.Text;
        stepNumberGroup.IsVisible = kind is EditorTool.Step;
        stepSizeGroup.IsVisible = kind is EditorTool.Step;
        cutDirectionGroup.IsVisible = kind is EditorTool.Cut;
        cutDirection.Select(defaults.CutDirection switch
        {
            CutAxis.Rows => 1,
            CutAxis.Columns => 2,
            _ => 0
        });

        canvasWidthGroup.IsVisible = kind is EditorTool.Canvas;
        canvasHeightGroup.IsVisible = kind is EditorTool.Canvas;
        canvasFitGroup.IsVisible = kind is EditorTool.Canvas;
        pictureGroup.IsVisible = selected is ImageAnnotation;

        // A fill colour only means anything when there is a fill to colour.
        var filled = selected is BoxAnnotation box ? box.HasFill : defaults.BoxFilled;
        fillColourGroup.IsVisible = kind is EditorTool.Box && filled;

        var backed = selected is TextAnnotation backedText ? backedText.HasBackground : defaults.TextBackgrounded;
        textBackColourGroup.IsVisible = kind is EditorTool.Text && backed;

        // A sidebar with nothing in it says so, rather than being an empty column that looks as if
        // something failed to load.
        settingsSection.IsVisible = settings.Children.Any(group => group.IsVisible);
        nothingToSet.IsVisible = !stylesSection.IsVisible && !settingsSection.IsVisible;

        switch (selected)
        {
            case ArrowAnnotation arrow:
                colour.Show(arrow.Color);
                weight.Show(arrow.Thickness);
                head.Select(arrow.Heads);
                break;

            case BoxAnnotation shape:
                colour.Show(shape.BorderColor);
                weight.Show(shape.BorderThickness);
                this.shape.Select(shape.Ellipse ? 1 : 0);
                fill.Select(shape.HasFill ? 1 : 0);
                fillColour.Show(shape.HasFill ? shape.FillColor : defaults.BoxFillColor);
                break;

            case BlurAnnotation region:
                blur.Show(region.Strength);
                hide.Select((int)region.Mode);
                break;

            case SpotlightAnnotation spotlight:
                dim.Show(spotlight.Dim);
                break;

            case MagnifyAnnotation magnify:
                lens.Show(magnify.Zoom);
                break;

            case PenAnnotation drawn:
                colour.Show(drawn.Color);
                weight.Show(drawn.Thickness);
                break;

            case TextAnnotation text:
                colour.Show(text.Color);
                textSize.Show(text.FontSize);
                textBack.Select(text.HasBackground ? 1 : 0);
                textBackColour.Show(text.HasBackground ? text.Background : defaults.TextBackgroundColor);
                break;

            case StepAnnotation step:
                colour.Show(step.Color);
                stepNumber.Show(step.Number);
                stepSize.Show(step.Diameter);
                break;

            default:
                colour.Show(tool switch
                {
                    EditorTool.Box => defaults.BoxBorderColor,
                    EditorTool.Text => defaults.TextColor,
                    EditorTool.Step => defaults.StepColor,
                    EditorTool.Pen => defaults.PenColor,
                    _ => defaults.ArrowColor
                });

                textBack.Select(defaults.TextBackgrounded ? 1 : 0);
                textBackColour.Show(defaults.TextBackgroundColor);
                stepSize.Show(defaults.StepDiameter);

                weight.Show(tool switch
                {
                    EditorTool.Box => defaults.BoxBorderThickness,
                    EditorTool.Pen => defaults.PenThickness,
                    _ => defaults.ArrowThickness
                });
                head.Select(defaults.ArrowHeads);
                shape.Select(defaults.BoxEllipse ? 1 : 0);
                hide.Select((int)defaults.HideMode);
                dim.Show(defaults.SpotlightDim);
                lens.Show(defaults.MagnifyZoom);
                fill.Select(defaults.BoxFilled ? 1 : 0);
                fillColour.Show(defaults.BoxFillColor);
                blur.Show(defaults.BlurStrength);
                textSize.Show(defaults.TextSize);
                break;
        }
    }
}
