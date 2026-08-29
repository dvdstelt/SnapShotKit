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
/// Blur has none. A style is a combination, and blur has one thing to set, which its own field
/// already offers as presets.
/// </summary>
public static class AnnotationStyles
{
    const string Red = SnapShotKit.Ui.Tokens.AnnotationDefault;
    const string Black = "#000000";
    const string White = "#FFFFFF";

    /// <summary>
    /// The space a preview's sample is drawn in, in the same pixels an annotation is measured in.
    ///
    /// Samples are written against this and scaled into whatever cell they land in, so the row can
    /// be made bigger or smaller without every sample being redrawn by hand.
    /// </summary>
    public static readonly Size Nominal = new(100, 64);

    static readonly AnnotationStyle[] Arrows =
    [
        new("Red", new ArrowAnnotation { Color = Red, Thickness = 4 }),
        new("Red, thick", new ArrowAnnotation { Color = Red, Thickness = 9 }),
        new("Black", new ArrowAnnotation { Color = Black, Thickness = 4 }),
        new("White", new ArrowAnnotation { Color = White, Thickness = 4 })
    ];

    static readonly AnnotationStyle[] Boxes =
    [
        new("Red border", new BoxAnnotation { BorderColor = Red, BorderThickness = 4 }),
        new("Red on white", new BoxAnnotation { BorderColor = Red, BorderThickness = 4, FillColor = White }),
        new("Black border", new BoxAnnotation { BorderColor = Black, BorderThickness = 4 }),

        // What a box is for when it is covering something up rather than pointing at it.
        new("Solid black", new BoxAnnotation { BorderColor = Black, BorderThickness = 4, FillColor = Black })
    ];

    static readonly AnnotationStyle[] Texts =
    [
        new("Red", new TextAnnotation { Color = Red, FontSize = 22 }),
        new("Black", new TextAnnotation { Color = Black, FontSize = 22 }),

        // The two that are legible over anything at all, which is what a screenshot is.
        new("White on black", new TextAnnotation { Color = White, FontSize = 22, Background = Black }),
        new("Black on white", new TextAnnotation { Color = Black, FontSize = 22, Background = White })
    ];

    static readonly AnnotationStyle[] Steps =
    [
        new("Red", new StepAnnotation { Color = Red, Diameter = 36 }),
        new("Red, large", new StepAnnotation { Color = Red, Diameter = 56 }),
        new("Black", new StepAnnotation { Color = Black, Diameter = 36 }),
        new("White", new StepAnnotation { Color = White, Diameter = 36 })
    ];

    static readonly AnnotationStyle[] None = [];

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
    /// sample runs corner to corner and every box sample fills the same rectangle, so the row shows
    /// what differs between the styles and nothing else.
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
