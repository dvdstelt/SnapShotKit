using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace SnapShotKit.Ui;

/// <summary>
/// A number with a few presets and a way to type any other.
///
/// The presets carry the common cases in one click, which is what a segmented control is for. The
/// last segment opens a slider and a box for the times the answer is not one of them: a stroke of
/// exactly eleven, a blur a shade lighter than the lightest preset. It shows the current value
/// whenever that value is off the scale, so a custom number stays visible on the band.
/// </summary>
public sealed class NumberField : StackPanel
{
    const double SegmentHeight = 28;

    readonly IReadOnlyList<double> presets;
    readonly List<(Border Segment, TextBlock Label)> segments = [];
    readonly Border customSegment;
    readonly TextBlock customLabel;
    readonly Popup popup;
    readonly double minimum;
    readonly double maximum;
    readonly Action<double> chosen;
    readonly string caption;

    double current;

    public NumberField(string caption, IReadOnlyList<double> presets, double minimum, double maximum,
        Action<double> chosen)
    {
        this.presets = presets;
        this.minimum = minimum;
        this.maximum = maximum;
        this.chosen = chosen;
        this.caption = caption;

        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        var frame = new Border
        {
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            Background = Tokens.BgBrush,
            Height = SegmentHeight
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        for (var index = 0; index < presets.Count; index++)
        {
            var value = presets[index];
            var (segment, label) = Segment(Format(value), index == 0);

            segment.PointerPressed += (_, _) => Choose(value);
            segments.Add((segment, label));
            row.Children.Add(segment);
        }

        (customSegment, customLabel) = Segment("…", first: false);
        ToolTip.SetTip(customSegment, $"Any {caption.ToLowerInvariant()}");
        customSegment.PointerPressed += (_, _) => Open();

        row.Children.Add(customSegment);
        frame.Child = row;

        popup = new Popup
        {
            PlacementTarget = customSegment,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            IsLightDismissEnabled = true
        };

        Children.Add(frame);
        Children.Add(new Panel { Width = 0, Children = { popup } });
    }

    (Border Segment, TextBlock Label) Segment(string text, bool first)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = Tokens.Fonts.Body,
            FontSize = 12.5,
            Foreground = Tokens.Neutral800Brush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var segment = new Border
        {
            Child = label,
            Padding = new Thickness(Tokens.Space.S2, 0),
            MinWidth = 26,
            Background = Tokens.BgBrush,
            CornerRadius = Tokens.Radius,
            Cursor = new Cursor(StandardCursorType.Hand),
            // Internal rules between options, drawn as the left edge of every segment but the first.
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(first ? 0 : 1, 0, 0, 0)
        };

        segment.PointerEntered += (_, _) =>
        {
            if (!ReferenceEquals(segment.Background, Tokens.AccentBrush))
            {
                segment.Background = Tokens.Neutral200Brush;
            }
        };

        segment.PointerExited += (_, _) =>
        {
            if (!ReferenceEquals(segment.Background, Tokens.AccentBrush))
            {
                segment.Background = Tokens.BgBrush;
            }
        };

        return (segment, label);
    }

    void Open()
    {
        var slider = new Slide(minimum, maximum, current) { Width = 180 };

        var box = new TextBox
        {
            Text = Format(current),
            FontFamily = Tokens.Fonts.Body,
            FontSize = 12.5,
            CornerRadius = Tokens.Radius,
            Padding = new Thickness(Tokens.Space.S2, 3),
            MinHeight = 0,
            Width = 62
        };

        var syncing = false;

        slider.Moved += moved =>
        {
            if (syncing)
            {
                return;
            }

            syncing = true;
            try
            {
                var value = Math.Round(moved, 1);
                box.Text = Format(value);
                Choose(value);
            }
            finally
            {
                syncing = false;
            }
        };

        box.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || syncing)
            {
                return;
            }

            // Only taken when it parses and is in range. The box is mid-edit for most of the
            // keystrokes it will ever see, and half-typed numbers are not answers.
            if (!double.TryParse(box.Text, out var typed))
            {
                return;
            }

            syncing = true;
            try
            {
                var value = Math.Clamp(typed, minimum, maximum);
                slider.Show(value);
                Choose(value);
            }
            finally
            {
                syncing = false;
            }
        };

        popup.Child = Blueprint.Wrap(new Border
        {
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            BoxShadow = Tokens.ShadowLg,
            Padding = new Thickness(Tokens.Space.S3),
            Child = new StackPanel
            {
                Spacing = Tokens.Space.S2,
                Children =
                {
                    Labels.Heading(caption, 10.5, 0.18, Tokens.Neutral500Brush),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = Tokens.Space.S3,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { slider, box }
                    }
                }
            }
        }, drawFrame: false);

        popup.IsOpen = true;
    }

    void Choose(double value)
    {
        Show(value);
        chosen(value);
    }

    /// <summary>Marks a value as current without raising the callback.</summary>
    public void Show(double value)
    {
        current = value;

        var onScale = false;

        for (var index = 0; index < presets.Count; index++)
        {
            var active = Math.Abs(presets[index] - value) < 0.001;
            var (segment, label) = segments[index];

            segment.Background = active ? Tokens.AccentBrush : Tokens.BgBrush;
            label.Foreground = active ? Tokens.BgBrush : Tokens.Neutral800Brush;
            onScale |= active;
        }

        // Off the scale: the last segment shows the number and takes the highlight, so the band
        // never claims a preset is selected when it is not.
        customSegment.Background = onScale ? Tokens.BgBrush : Tokens.AccentBrush;
        customLabel.Foreground = onScale ? Tokens.Neutral800Brush : Tokens.BgBrush;
        customLabel.Text = onScale ? "…" : Format(value);
    }

    static string Format(double value) => value == Math.Floor(value)
        ? ((int)value).ToString()
        : value.ToString("0.#");
}
