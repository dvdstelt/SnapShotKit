using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace SnapShotKit.Ui;

/// <summary>
/// A segmented control: a row of mutually exclusive options sharing one hairline frame.
///
/// The design uses these where a value has a handful of sensible settings — stroke weight, arrow
/// head, fill — rather than a slider. Discrete beats continuous here: nobody wants a 4.7 pixel
/// stroke, the choice is visible without being dragged, and a click is naturally one undo step
/// where a drag is a hundred.
/// </summary>
public sealed class Segmented : Border
{
    readonly List<Border> segments = [];
    readonly List<TextBlock> labels = [];

    public Segmented(IReadOnlyList<string> options, Action<int> chosen, double height = 28)
    {
        BorderBrush = Tokens.DividerBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = Tokens.Radius;
        Background = Tokens.BgBrush;
        Height = height;

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        for (var index = 0; index < options.Count; index++)
        {
            var position = index;

            var label = new TextBlock
            {
                Text = options[index],
                FontFamily = Tokens.Fonts.Body,
                FontSize = 12.5,
                Foreground = Tokens.Neutral800Brush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var segment = new Border
            {
                Child = label,
                Padding = new Thickness(Tokens.Space.S3, 0),
                MinWidth = 30,
                Background = Tokens.BgBrush,
                CornerRadius = Tokens.Radius,
                Cursor = new Cursor(StandardCursorType.Hand),
                // Internal rules between options, drawn as the left edge of every segment but the first.
                BorderBrush = Tokens.DividerBrush,
                BorderThickness = new Thickness(index == 0 ? 0 : 1, 0, 0, 0)
            };

            segment.PointerPressed += (_, _) =>
            {
                Select(position);
                chosen(position);
            };

            segment.PointerEntered += (_, _) =>
            {
                if (position != SelectedIndex)
                {
                    segment.Background = Tokens.Neutral200Brush;
                }
            };

            segment.PointerExited += (_, _) =>
            {
                if (position != SelectedIndex)
                {
                    segment.Background = Tokens.BgBrush;
                }
            };

            segments.Add(segment);
            labels.Add(label);
            row.Children.Add(segment);
        }

        Child = row;
    }

    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Marks an option as chosen without raising the callback, for syncing to a selection.</summary>
    public void Select(int index)
    {
        SelectedIndex = index;

        for (var position = 0; position < segments.Count; position++)
        {
            var active = position == index;
            segments[position].Background = active ? Tokens.AccentBrush : Tokens.BgBrush;
            labels[position].Foreground = active ? Tokens.BgBrush : Tokens.Neutral800Brush;
        }
    }
}
