using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>
/// The one band that carries both the drawing tools and their settings.
///
/// It replaces the old icon rail and right-hand properties panel together. The settings shown
/// change with the tool, but their positions do not: colour is always in the same place, weight
/// always the next along. That is the whole point of the arrangement — the hand learns where a
/// control is once, and switching tools never moves it somewhere else.
///
/// Every setting offers a handful of presets and a way to reach any other value. The presets carry
/// almost all of the use; being unable to reach the one value that is not on the list is the kind
/// of limit that makes a tool feel like a toy.
/// </summary>
public sealed class ToolBand : Border
{
    /// <summary>Stroke weights offered as presets. Anything else is one click further on.</summary>
    static readonly double[] Weights = [2, 4, 6, 8];

    static readonly double[] TextSizes = [15, 22, 30, 44];

    const double BandHeight = 62;
    const double CellWidth = 40;
    const double CellHeight = 34;

    readonly List<(EditorTool Tool, Border Cell, Control Glyph)> tools = [];

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
    readonly Control fillGroup;
    readonly Control blurGroup;
    readonly Control textSizeGroup;
    readonly Control canvasWidthGroup;
    readonly Control canvasHeightGroup;
    readonly Control canvasFitGroup;

    readonly TextBlock zoomLabel = Labels.Body("100%", 12.5, Tokens.Neutral800Brush);

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

        head = new Segmented(["Single", "Double"], index => DoubleHeadChosen?.Invoke(index == 1));
        fill = new Segmented(["None", "Solid"], index => FillChosen?.Invoke(index == 1));

        // Every group is captioned, the colours included. Without a caption the swatches sit at a
        // different height from everything beside them and the row reads as broken.
        colourGroup = Group("Colour", colour);
        fillColourGroup = Group("Fill colour", fillColour);
        weightGroup = Group("Weight", weight);
        headGroup = Group("Head", head);
        fillGroup = Group("Fill", fill);
        blurGroup = Group("Blur", blur);
        textSizeGroup = Group("Size", textSize);
        textBackGroup = Group("Background", textBack);
        textBackColourGroup = Group("Background colour", textBackColour);
        stepNumberGroup = Group("Number", stepNumber);
        stepSizeGroup = Group("Size", stepSize);

        Control widthBox;
        Control heightBox;
        (widthBox, canvasWidth) = SizeBox(value => CanvasWidthChosen?.Invoke(value));
        (heightBox, canvasHeight) = SizeBox(value => CanvasHeightChosen?.Invoke(value));

        canvasWidthGroup = Group("Width", widthBox);
        canvasHeightGroup = Group("Height", heightBox);
        canvasFitGroup = Group("Canvas", TextAction("Fit to capture", () => CanvasFitRequested?.Invoke()));


        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S4,
            VerticalAlignment = VerticalAlignment.Center
        };

        left.Children.Add(BuildToolCells());
        left.Children.Add(Rule());

        // Every settings group lives in this one strip, in a fixed order. Only the ones the active
        // tool uses are visible; the rest collapse, and the ones that remain do not move.
        var settings = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S4,
            VerticalAlignment = VerticalAlignment.Center
        };

        foreach (var group in new[]
                 {
                     colourGroup, weightGroup, blurGroup, textSizeGroup, stepNumberGroup, stepSizeGroup,
                     headGroup, fillGroup, fillColourGroup, textBackGroup, textBackColourGroup,
                     canvasWidthGroup, canvasHeightGroup, canvasFitGroup
                 })
        {
            settings.Children.Add(group);
        }

        left.Children.Add(settings);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S3,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        right.Children.Add(TextAction("Undo", () => UndoRequested?.Invoke()));
        right.Children.Add(TextAction("Redo", () => RedoRequested?.Invoke()));
        right.Children.Add(Rule());
        right.Children.Add(BuildZoom());

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(Tokens.Space.S4, 0) };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        Child = grid;
    }

    public event Action<EditorTool>? ToolChosen;
    public event Action<string>? ColourChosen;
    public event Action<string>? FillColourChosen;
    public event Action<double>? WeightChosen;
    public event Action<bool>? DoubleHeadChosen;
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
    public event Action<int>? CanvasWidthChosen;
    public event Action<int>? CanvasHeightChosen;
    public event Action? CanvasFitRequested;
    public event Action? UndoRequested;
    public event Action? RedoRequested;

    Control BuildToolCells()
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        foreach (var (tool, glyph, tip) in new[]
                 {
                     (EditorTool.Select, Lucide.Select, "Select and move  (V)"),
                     (EditorTool.Arrow, Lucide.Arrow, "Arrow  (A)"),
                     (EditorTool.Box, Lucide.Box, "Box  (B)"),
                     (EditorTool.Blur, Lucide.Blur, "Blur  (L)"),
                     (EditorTool.Step, Lucide.Step, "Numbered marker  (N)\nEach one takes the next number up."),
                     (EditorTool.Text, Lucide.Text, "Text  (T)\nType in place. Shift+Enter for a new line, Enter to finish."),
                     (EditorTool.Canvas, Lucide.Crop, "Resize canvas  (C)\nDrag an edge in to crop, or out to add transparent space.\nEnter applies, Escape backs out.")
                 })
        {
            var icon = Lucide.Icon(glyph, 17, Tokens.Neutral800Brush);

            var cell = new Border
            {
                Width = CellWidth,
                Height = CellHeight,
                Background = Tokens.BgBrush,
                CornerRadius = Tokens.Radius,
                Child = icon,
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
            tools.Add((tool, cell, icon));
            strip.Children.Add(cell);
        }

        return strip;
    }

    public EditorTool Active { get; private set; } = EditorTool.Arrow;

    static Control Rule() => new Border
    {
        Width = 1,
        Height = 26,
        Background = Tokens.DividerBrush,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>A settings group: its condensed caption above the control it names.</summary>
    static Control Group(string caption, Control control) => new StackPanel
    {
        Spacing = 2,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            Labels.Heading(caption, 10.5, 0.18, Tokens.Neutral500Brush),
            control
        }
    };

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

        foreach (var (candidate, cell, glyph) in tools)
        {
            var active = candidate == tool;
            cell.Background = active ? Tokens.AccentBrush : Tokens.BgBrush;

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
            TextAnnotation => EditorTool.Text,
            StepAnnotation => EditorTool.Step,
            _ => tool
        };

        colourGroup.IsVisible = kind is EditorTool.Arrow or EditorTool.Box or EditorTool.Text or EditorTool.Step;
        weightGroup.IsVisible = kind is EditorTool.Arrow or EditorTool.Box;
        headGroup.IsVisible = kind is EditorTool.Arrow;
        fillGroup.IsVisible = kind is EditorTool.Box;
        blurGroup.IsVisible = kind is EditorTool.Blur;
        textSizeGroup.IsVisible = kind is EditorTool.Text;
        textBackGroup.IsVisible = kind is EditorTool.Text;
        stepNumberGroup.IsVisible = kind is EditorTool.Step;
        stepSizeGroup.IsVisible = kind is EditorTool.Step;
        canvasWidthGroup.IsVisible = kind is EditorTool.Canvas;
        canvasHeightGroup.IsVisible = kind is EditorTool.Canvas;
        canvasFitGroup.IsVisible = kind is EditorTool.Canvas;

        // A fill colour only means anything when there is a fill to colour.
        var filled = selected is BoxAnnotation box ? box.HasFill : defaults.BoxFilled;
        fillColourGroup.IsVisible = kind is EditorTool.Box && filled;

        var backed = selected is TextAnnotation backedText ? backedText.HasBackground : defaults.TextBackgrounded;
        textBackColourGroup.IsVisible = kind is EditorTool.Text && backed;

        switch (selected)
        {
            case ArrowAnnotation arrow:
                colour.Show(arrow.Color);
                weight.Show(arrow.Thickness);
                head.Select(arrow.DoubleHeaded ? 1 : 0);
                break;

            case BoxAnnotation shape:
                colour.Show(shape.BorderColor);
                weight.Show(shape.BorderThickness);
                fill.Select(shape.HasFill ? 1 : 0);
                fillColour.Show(shape.HasFill ? shape.FillColor : defaults.BoxFillColor);
                break;

            case BlurAnnotation region:
                blur.Show(region.Strength);
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
                    _ => defaults.ArrowColor
                });

                textBack.Select(defaults.TextBackgrounded ? 1 : 0);
                textBackColour.Show(defaults.TextBackgroundColor);
                stepSize.Show(defaults.StepDiameter);

                weight.Show(tool == EditorTool.Box ? defaults.BoxBorderThickness : defaults.ArrowThickness);
                head.Select(defaults.ArrowDoubleHeaded ? 1 : 0);
                fill.Select(defaults.BoxFilled ? 1 : 0);
                fillColour.Show(defaults.BoxFillColor);
                blur.Show(defaults.BlurStrength);
                textSize.Show(defaults.TextSize);
                break;
        }
    }
}
