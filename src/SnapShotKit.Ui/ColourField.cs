using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace SnapShotKit.Ui;

/// <summary>
/// The annotation colour control: a few swatches for the common cases, and a picker for everything
/// else.
///
/// The palette leads with red rather than the interface accent. The design system's single-accent
/// rule governs the application's own surfaces, but an annotation is a mark on somebody else's
/// screenshot: it has to read as deliberate against arbitrary pixels underneath, and red is the
/// convention every reader of a screenshot already knows.
///
/// The last swatch is the picker. It shows the current colour whenever that colour is not one of
/// the presets, so a custom choice stays visible on the band rather than disappearing the moment
/// the popup closes.
/// </summary>
public sealed class ColourField : StackPanel
{
    /// <summary>The presets, in the order they are shown. Red, three steels, and the two neutrals.</summary>
    public static readonly string[] Palette =
    [
        Tokens.AnnotationDefault,
        "#5980A6",
        "#1D2D3D",
        "#2B2B2D",
        "#F5F5F8"
    ];

    const double SwatchSize = 18;

    readonly List<(string Colour, Border Ring, Border Swatch)> presets = [];
    readonly Border customRing;
    readonly Border customSwatch;
    readonly Popup popup;
    readonly Action<string> picked;

    string current = Tokens.AnnotationDefault;

    public ColourField(Action<string> picked)
    {
        this.picked = picked;

        Orientation = Orientation.Horizontal;
        Spacing = 4;
        VerticalAlignment = VerticalAlignment.Center;

        foreach (var colour in Palette)
        {
            var (ring, swatch) = Swatch(new SolidColorBrush(Color.Parse(colour)));

            var value = colour;
            swatch.PointerPressed += (_, _) => Choose(value);

            presets.Add((colour, ring, swatch));
            Children.Add(ring);
        }

        // The picker's own swatch. Chequered until a custom colour is in use, so it reads as "some
        // other colour" rather than as a sixth preset that happens to be white.
        (customRing, customSwatch) = Swatch(Chequer());
        ToolTip.SetTip(customSwatch, "More colours");

        popup = new Popup
        {
            PlacementTarget = customSwatch,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true
        };

        customSwatch.PointerPressed += (_, _) => Open();

        Children.Add(customRing);
        Children.Add(new Panel { Width = 0, Children = { popup } });
    }

    static (Border Ring, Border Swatch) Swatch(IBrush fill)
    {
        var swatch = new Border
        {
            Width = SwatchSize,
            Height = SwatchSize,
            Background = fill,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // The chosen swatch is ringed at an offset rather than given a heavier border, so choosing
        // a colour never changes the size or position of the row.
        var ring = new Border
        {
            Padding = new Thickness(2),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = Tokens.Radius,
            Child = swatch
        };

        return (ring, swatch);
    }

    /// <summary>A small chequerboard, the usual way of drawing "no colour in particular".</summary>
    static IBrush Chequer() => new DrawingBrush
    {
        TileMode = TileMode.Tile,
        SourceRect = new RelativeRect(0, 0, 8, 8, RelativeUnit.Absolute),
        DestinationRect = new RelativeRect(0, 0, 8, 8, RelativeUnit.Absolute),
        Stretch = Stretch.None,
        Drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = Brushes.White, Geometry = new RectangleGeometry(new Rect(0, 0, 8, 8)) },
                new GeometryDrawing
                {
                    Brush = Tokens.Neutral400Brush,
                    Geometry = new RectangleGeometry(new Rect(0, 0, 4, 4))
                },
                new GeometryDrawing
                {
                    Brush = Tokens.Neutral400Brush,
                    Geometry = new RectangleGeometry(new Rect(4, 4, 4, 4))
                }
            }
        }
    };

    void Open()
    {
        var picker = new ColourPicker(current);
        picker.Chosen += Choose;

        popup.Child = Blueprint.Wrap(new Border
        {
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            BoxShadow = Tokens.ShadowLg,
            Padding = new Thickness(Tokens.Space.S3),
            Child = picker
        }, drawFrame: false);

        popup.IsOpen = true;
    }

    void Choose(string colour)
    {
        Show(colour);
        picked(colour);
    }

    /// <summary>Marks a colour as current without raising the callback.</summary>
    public void Show(string? colour)
    {
        current = string.IsNullOrWhiteSpace(colour) ? Tokens.AnnotationDefault : colour;

        var preset = false;

        foreach (var (candidate, ring, _) in presets)
        {
            var chosen = string.Equals(candidate, colour, StringComparison.OrdinalIgnoreCase);
            ring.BorderBrush = chosen ? Tokens.Accent700Brush : Brushes.Transparent;
            preset |= chosen;
        }

        // Off the palette: the picker's swatch takes the colour and the ring, so the band still
        // shows what is actually selected.
        customRing.BorderBrush = preset ? Brushes.Transparent : Tokens.Accent700Brush;

        customSwatch.Background = preset || !Color.TryParse(current, out var parsed)
            ? Chequer()
            : new SolidColorBrush(parsed);
    }
}
