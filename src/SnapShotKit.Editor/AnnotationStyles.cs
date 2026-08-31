using Avalonia;

namespace SnapShotKit.Editor;

/// <summary>One ready-made look, and the annotation that wears it.</summary>
/// <param name="Name">What it is called, which is what the band's tooltip says.</param>
/// <param name="Look">An annotation carrying exactly this look. Drawn as the preview, and copied onto whatever the style is applied to.</param>
public sealed record AnnotationStyle(string Name, Annotation Look);

/// <summary>
/// The ready-made looks each tool offers.
///
/// Every setting on the band can already be reached one at a time, and that is the wrong unit for
/// the way anyone actually works: what is wanted is a red box with no fill, or white words on a
/// black plate, and reaching either through three controls in a row is three decisions where one
/// was meant. A style is that one decision, and the settings beside it stay exactly as they were
/// for the times the answer is not on the list.
///
/// They are prototypes rather than a table of values, so a style is applied by copying its look
/// onto an annotation and recognised by comparing against it. Each kind decides what its own look
/// consists of; see <see cref="Annotation.AdoptStyle"/>.
///
/// Colour is not what these are for. The palette beside them changes a colour in one click already,
/// so a style earns its place by combining things: a weight with a head, a border with a fill, ink
/// with a plate. They are offered in the few colours those combinations are actually wanted in.
///
/// Order matters. A tool opens showing the first three, so those are the ones worth having without
/// asking; the rest are one click further on, in the gallery.
///
/// Blur has none. A style is a combination, and blur has one thing to set, which its own field
/// already offers as presets.
/// </summary>
public static class AnnotationStyles
{
    const string Red = SnapShotKit.Ui.Tokens.AnnotationDefault;
    const string Black = "#000000";
    const string White = "#FFFFFF";
    const string Blue = "#2F6FE0";
    const string Amber = "#F5A524";

    const double Thin = 4;
    const double Thick = 9;

    /// <summary>
    /// The space a preview's sample is drawn in, in the same pixels an annotation is measured in.
    ///
    /// Samples are written against this and scaled into whatever cell they land in, so the row and
    /// the gallery can draw the same style at different sizes without a second set of drawings.
    /// </summary>
    public static readonly Size Nominal = new(100, 64);

    static readonly AnnotationStyle[] Arrows =
    [
        new("Red", Arrow(Red, Thin)),
        new("Red, thick", Arrow(Red, Thick)),
        new("Black", Arrow(Black, Thin)),

        new("White", Arrow(White, Thin)),
        new("Blue", Arrow(Blue, Thin)),
        new("Black, thick", Arrow(Black, Thick)),
        new("White, thick", Arrow(White, Thick)),

        new("Blue, thick", Arrow(Blue, Thick)),
        new("Amber", Arrow(Amber, Thin)),

        // Both ends, for saying that two things are the same rather than that one is over there.
        new("Red, both ends", Arrow(Red, Thin, doubled: true)),
        new("Black, both ends", Arrow(Black, Thin, doubled: true)),
        new("White, both ends", Arrow(White, Thin, doubled: true))
    ];

    static readonly AnnotationStyle[] Boxes =
    [
        new("Red border", Box(Red, Thin)),
        new("Red on white", Box(Red, Thin, White)),
        new("Black border", Box(Black, Thin)),

        new("White border", Box(White, Thin)),
        new("Blue border", Box(Blue, Thin)),
        new("Black on white", Box(Black, Thin, White)),
        new("Blue on white", Box(Blue, Thin, White)),

        new("Red, thick", Box(Red, Thick)),
        new("Black, thick", Box(Black, Thick)),
        new("White, thick", Box(White, Thick)),

        // What a box is for when it is covering something up rather than pointing at it.
        new("Solid black", Box(Black, Thin, Black)),
        new("Solid white", Box(White, Thin, White))
    ];

    static readonly AnnotationStyle[] Texts =
    [
        new("Red", Text(Red)),
        new("Black", Text(Black)),

        // The pair that stays legible over anything at all, which is what a screenshot is.
        new("White on black", Text(White, Black)),
        new("Black on white", Text(Black, White)),

        new("White", Text(White)),
        new("Blue", Text(Blue)),
        new("Red on white", Text(Red, White)),
        new("Blue on white", Text(Blue, White)),

        new("Amber on black", Text(Amber, Black)),
        new("White on red", Text(White, Red))
    ];

    static readonly AnnotationStyle[] Steps =
    [
        new("Red", Step(Red, 36)),
        new("Black", Step(Black, 36)),
        new("Red, large", Step(Red, 56)),

        new("White", Step(White, 36)),
        new("Blue", Step(Blue, 36)),
        new("Black, large", Step(Black, 56)),
        new("White, large", Step(White, 56)),
        new("Blue, large", Step(Blue, 56))
    ];

    static readonly AnnotationStyle[] None = [];

    static ArrowAnnotation Arrow(string colour, double thickness, bool doubled = false) =>
        new() { Color = colour, Thickness = thickness, DoubleHeaded = doubled };

    static BoxAnnotation Box(string colour, double thickness, string fill = "") =>
        new() { BorderColor = colour, BorderThickness = thickness, FillColor = fill };

    static TextAnnotation Text(string colour, string plate = "") =>
        new() { Color = colour, FontSize = 22, Background = plate };

    static StepAnnotation Step(string colour, double diameter) =>
        new() { Color = colour, Diameter = diameter };

    /// <summary>The looks offered for a tool, or none where the idea does not apply.</summary>
    public static IReadOnlyList<AnnotationStyle> For(EditorTool tool) => tool switch
    {
        EditorTool.Arrow => Arrows,
        EditorTool.Box => Boxes,
        EditorTool.Text => Texts,
        EditorTool.Step => Steps,
        _ => None
    };

    /// <summary>
    /// The sample as it is drawn in a preview, laid out in <see cref="Nominal"/>.
    ///
    /// Geometry rather than style, which is why it is here and not in the catalogue: every arrow
    /// sample runs corner to corner and every box sample fills the same rectangle, so a row of them
    /// shows what differs between the styles and nothing else.
    /// </summary>
    public static Annotation Sample(Annotation look)
    {
        var sample = look.Copy();

        switch (sample)
        {
            case ArrowAnnotation arrow:
                arrow.X1 = 12;
                arrow.Y1 = 52;
                arrow.X2 = 88;
                arrow.Y2 = 14;
                break;

            case RectAnnotation rect:
                rect.X = 10;
                rect.Y = 12;
                rect.Width = 80;
                rect.Height = 40;
                break;

            case StepAnnotation step:
                step.X = 50;
                step.Y = 32;
                step.Number = 1;
                break;

            case TextAnnotation text:
                // Positioned by the preview, which is the only thing that can measure the words.
                // Set to fill the cell rather than left at the style's own size: two letters at
                // 22 points in a cell this size would be a smudge, and what a text style differs
                // in is its colour and its plate, both of which the size only gets in the way of.
                text.Text = "Aa";
                text.FontSize = 40;
                text.BackgroundPadding = 6;
                break;
        }

        return sample;
    }
}
