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
/// The palette departs from the design system's single-accent rule, deliberately. That rule governs
/// the application's own surfaces; an annotation is a mark on somebody else's screenshot, and it
/// has to read as deliberate against arbitrary pixels underneath.
///
/// The last swatch is the picker. It shows the current colour whenever that colour is not one of
/// the presets, so a custom choice stays visible on the band rather than disappearing the moment
/// the popup closes.
/// </summary>
public sealed class ColourField : StackPanel
{
    /// <summary>
    /// The presets, in the order they are shown.
    ///
    /// Black and white first, and both of them the real thing rather than a shade near it. They are
    /// what a fill, a plate or a mark on a light or a dark screenshot most often wants, and two
    /// swatches a few points apart from each other and from black are a choice nobody can make on
    /// sight. The tonal ramps are for the interface, which is why they are not here.
    ///
    /// Then the colours a screenshot is actually marked up in. Red leads them because it is the
    /// convention every reader of a screenshot already knows, and it stays the colour a new
    /// annotation is drawn in.
    /// </summary>
    public static readonly string[] Palette =
    [
        "#000000",
        "#FFFFFF",
        Tokens.AnnotationDefault,
        "#F5A524",
        "#22A45D",
        "#2F6FE0"
    ];

    const double SwatchSize = 18;

    readonly List<(string Colour, Border Ring, Border Swatch)> presets = [];
    readonly Border customRing;
    readonly Border customSwatch;
    readonly Popup popup;
    readonly Action<string> picked;

    string current = Tokens.AnnotationDefault;

    /// <summary>What a field that offers no colour at all reports when that is chosen.</summary>
    public const string Transparent = "transparent";

    /// <param name="picked">Called with the colour chosen, as hex, or <see cref="Transparent"/>.</param>
    /// <param name="offersTransparent">
    /// Whether no colour at all is a choice, as a swatch ahead of the rest. Only where nothing is a
    /// meaningful colour to have, such as the far end of a fade; for a border or a line it would
    /// only be a way to draw something invisible.
    /// </param>
    public ColourField(Action<string> picked, bool offersTransparent = false)
    {
        this.picked = picked;

        Orientation = Orientation.Horizontal;
        Spacing = 4;
        VerticalAlignment = VerticalAlignment.Center;

        IEnumerable<string> offered = offersTransparent ? [Transparent, .. Palette] : Palette;

        foreach (var colour in offered)
        {
            var (ring, swatch) = Swatch(colour == Transparent ? Nothing() : new SolidColorBrush(Color.Parse(colour)));

            if (colour == Transparent)
            {
                ToolTip.SetTip(swatch, "Transparent");
            }

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

    /// <summary>
    /// White struck through in red, the usual way of drawing no colour at all.
    ///
    /// Not the chequerboard, which already stands for the picker's "some other colour" at the end of
    /// the same row, and two swatches that look alike and mean different things is one too many.
    /// </summary>
    static IBrush Nothing() => new DrawingBrush
    {
        Stretch = Stretch.Fill,
        Drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = Brushes.White, Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)) },
                new GeometryDrawing
                {
                    Pen = new Pen(new SolidColorBrush(Color.Parse(Tokens.AnnotationDefault)), 1.5),
                    Geometry = new LineGeometry(new Point(16, 0), new Point(0, 16))
                }
            }
        }
    };

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
        // No colour at all has no hue to start the picker from.
        var picker = new ColourPicker(current == Transparent ? Tokens.AnnotationDefault : current);
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
