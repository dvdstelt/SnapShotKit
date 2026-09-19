using System.Text.Json.Serialization;

namespace SnapShotKit.Editor;

/// <summary>
/// One annotation on a snapshot.
///
/// Everything here is geometry and style, never pixels. That is the whole point of the format: the
/// capture underneath is never modified, so an arrow drawn today can be moved or deleted next week.
/// Coordinates are in image pixels.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ArrowAnnotation), "arrow")]
[JsonDerivedType(typeof(BlurAnnotation), "blur")]
[JsonDerivedType(typeof(BoxAnnotation), "box")]
[JsonDerivedType(typeof(TextAnnotation), "text")]
[JsonDerivedType(typeof(StepAnnotation), "step")]
[JsonDerivedType(typeof(ImageAnnotation), "image")]
public abstract class Annotation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public abstract Annotation Copy();

    /// <summary>
    /// Takes on another annotation's look, leaving its own geometry and its place in the document
    /// alone.
    ///
    /// What counts as the look is each kind's own business, which is why this lives here rather
    /// than in the band that offers the ready-made ones. A style is a complete look and not a
    /// suggestion: it sets everything it covers, so picking one twice gives the same annotation
    /// both times.
    /// </summary>
    public abstract void AdoptStyle(Annotation style);

    /// <summary>Whether it already looks exactly like the given one. False for a different kind of annotation.</summary>
    public abstract bool WearsStyle(Annotation style);
}

/// <summary>
/// An annotation defined by a rectangle.
///
/// Blur and box share it today; highlight, ellipse and step numbers would all fit here too. The
/// canvas moves and resizes anything of this shape without knowing what it is.
/// </summary>
public abstract class RectAnnotation : Annotation
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class ArrowAnnotation : Annotation
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }

    /// <summary>Hex, so the document stays readable and diffable.</summary>
    public string Color { get; set; } = SnapShotKit.Ui.Tokens.AnnotationDefault;

    public double Thickness { get; set; } = 4;

    /// <summary>
    /// A head at both ends rather than only the far one.
    ///
    /// Absent from documents written before it existed, which deserialise as false: an arrow drawn
    /// last week keeps pointing the one way it was drawn to point.
    /// </summary>
    public bool DoubleHeaded { get; set; }

    /// <summary>
    /// No head at either end, which makes it a line.
    ///
    /// A line is an arrow that points at nothing rather than a tool of its own: it is drawn, picked
    /// up, moved and styled in exactly the same way, and somebody who drew an arrow and wanted a
    /// line should be able to say so without drawing it again. Kept beside
    /// <see cref="DoubleHeaded"/> rather than replacing it with a count, so that documents written
    /// before there were lines still mean what they meant.
    /// </summary>
    public bool Headless { get; set; }

    /// <summary>How many heads: none, one at the far end, or one at each.</summary>
    [JsonIgnore]
    public int Heads
    {
        get => Headless ? 0 : DoubleHeaded ? 2 : 1;
        set
        {
            Headless = value <= 0;
            DoubleHeaded = value >= 2;
        }
    }

    public override Annotation Copy() => new ArrowAnnotation
    {
        Id = Id, X1 = X1, Y1 = Y1, X2 = X2, Y2 = Y2,
        Color = Color, Thickness = Thickness, DoubleHeaded = DoubleHeaded, Headless = Headless
    };

    public override void AdoptStyle(Annotation style)
    {
        if (style is ArrowAnnotation arrow)
        {
            Color = arrow.Color;
            Thickness = arrow.Thickness;
            Heads = arrow.Heads;
        }
    }

    public override bool WearsStyle(Annotation style) => style is ArrowAnnotation arrow
        && Color == arrow.Color && Thickness == arrow.Thickness && Heads == arrow.Heads;
}

/// <summary>How a hidden region hides what is under it.</summary>
public enum HideMode
{
    Blur,
    Pixelate,
    Solid
}

public sealed class BlurAnnotation : RectAnnotation
{
    /// <summary>
    /// Blur strength, 1 to 100.
    ///
    /// Deliberately not the gaussian parameter. That parameter is sigma, and sigma climbs
    /// alarmingly: 3 already makes text unreadable and 5 flattens a region to one colour, so the
    /// entire useful range sat in the first few notches of the slider. Strength spreads that range
    /// across the whole slider instead.
    /// </summary>
    public int Strength { get; set; } = 45;

    /// <summary>
    /// The four strengths offered as presets.
    ///
    /// Not evenly spaced: strength ramps as a square, so these are four visibly different amounts
    /// of blur, from "the text is gone" to "the region is one colour". Any other strength can be
    /// typed; these are only the ones worth a single click.
    /// </summary>
    public static readonly int[] Presets = [20, 35, 55, 80];

    /// <summary>The gaussian sigma this strength corresponds to.</summary>
    public float Sigma => Sigmaof(Strength);

    public static float Sigmaof(int strength)
    {
        var normalised = Math.Clamp(strength <= 0 ? 45 : strength, 1, 100) / 100f;
        return Math.Max(normalised * normalised * 8f, 0.1f);
    }

    /// <summary>
    /// How what is underneath is hidden.
    ///
    /// A gaussian blur looks best and hides least: on text set in a known typeface, a light blur
    /// can be worked backwards, and people have had passwords read out of screenshots that way.
    /// Squares throw the detail away instead of smearing it, and a solid bar leaves nothing at all,
    /// which is the only honest answer for something that must not be read. Blur is first, so a
    /// document from before there was a choice means what it always meant.
    /// </summary>
    public HideMode Mode { get; set; }

    public override Annotation Copy() => new BlurAnnotation
    {
        Id = Id, X = X, Y = Y, Width = Width, Height = Height, Strength = Strength, Mode = Mode
    };

    public override void AdoptStyle(Annotation style)
    {
        if (style is BlurAnnotation blur)
        {
            Strength = blur.Strength;
            Mode = blur.Mode;
        }
    }

    public override bool WearsStyle(Annotation style) => style is BlurAnnotation blur && Strength == blur.Strength && Mode == blur.Mode;
}

public sealed class BoxAnnotation : RectAnnotation
{
    public string BorderColor { get; set; } = "#E5342A";

    public double BorderThickness { get; set; } = 4;

    /// <summary>
    /// Empty means no fill. A box drawn over a screenshot is usually an outline, so that is the
    /// default; any other value is the fill's own colour, chosen independently of the border's.
    /// </summary>
    public string FillColor { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasFill => !string.IsNullOrWhiteSpace(FillColor);

    /// <summary>
    /// Round rather than square: the ellipse that fits the same rectangle.
    ///
    /// The same object with a different outline, for the same reason a line is an arrow. It is
    /// dragged out, sized by the same eight grips and filled the same way, and the only thing that
    /// differs is the path the border takes.
    /// </summary>
    public bool Ellipse { get; set; }

    public override Annotation Copy() => new BoxAnnotation
    {
        Id = Id, X = X, Y = Y, Width = Width, Height = Height,
        BorderColor = BorderColor, BorderThickness = BorderThickness, FillColor = FillColor, Ellipse = Ellipse
    };

    public override void AdoptStyle(Annotation style)
    {
        if (style is BoxAnnotation box)
        {
            BorderColor = box.BorderColor;
            BorderThickness = box.BorderThickness;
            FillColor = box.FillColor;
            Ellipse = box.Ellipse;
        }
    }

    public override bool WearsStyle(Annotation style) => style is BoxAnnotation box
        && BorderColor == box.BorderColor && BorderThickness == box.BorderThickness && FillColor == box.FillColor
        && Ellipse == box.Ellipse;
}

public sealed class TextAnnotation : Annotation
{
    public double X { get; set; }
    public double Y { get; set; }

    public string Text { get; set; } = "Text";

    public string FontFamily { get; set; } = "Cantarell";

    public double FontSize { get; set; } = 28;

    public string Color { get; set; } = "#E5342A";

    /// <summary>
    /// A plate behind the words. Empty means none, which is the default.
    ///
    /// What makes text usable over a screenshot: no single ink colour is legible against a photo
    /// or a gradient, and a background is the only thing that reliably fixes it.
    /// </summary>
    public string Background { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasBackground => !string.IsNullOrWhiteSpace(Background);

    /// <summary>How far the plate extends past the words, in image pixels.</summary>
    public double BackgroundPadding { get; set; } = 6;

    public override Annotation Copy() => new TextAnnotation
    {
        Id = Id, X = X, Y = Y, Text = Text, FontFamily = FontFamily, FontSize = FontSize, Color = Color,
        Background = Background, BackgroundPadding = BackgroundPadding
    };

    /// <summary>
    /// The face is left out of it, deliberately: there is no way to choose one on the band, so a
    /// style that set it could only ever take away a choice made somewhere else.
    /// </summary>
    public override void AdoptStyle(Annotation style)
    {
        if (style is TextAnnotation text)
        {
            Color = text.Color;
            FontSize = text.FontSize;
            Background = text.Background;
            BackgroundPadding = text.BackgroundPadding;
        }
    }

    public override bool WearsStyle(Annotation style) => style is TextAnnotation text
        && Color == text.Color && FontSize == text.FontSize && Background == text.Background;
}

/// <summary>
/// A numbered marker: a filled disc with a number in it, for walking a reader through a screenshot
/// one step at a time.
///
/// The number is an ordinary field rather than a position in a sequence. New markers take the next
/// number up so a walkthrough numbers itself, but nothing stops two of them saying the same thing:
/// a picture with two separate "step 1" markers is a real thing to want.
/// </summary>
public sealed class StepAnnotation : Annotation
{
    /// <summary>The centre of the disc, which is what a marker is positioned by.</summary>
    public double X { get; set; }
    public double Y { get; set; }

    public int Number { get; set; } = 1;

    public double Diameter { get; set; } = 36;

    /// <summary>The disc's colour. The number takes whichever of black or white reads on it.</summary>
    public string Color { get; set; } = SnapShotKit.Ui.Tokens.AnnotationDefault;

    [JsonIgnore]
    public double Radius => Diameter / 2;

    public override Annotation Copy() => new StepAnnotation
    {
        Id = Id, X = X, Y = Y, Number = Number, Diameter = Diameter, Color = Color
    };

    /// <summary>The number is not part of the look. It says which step this is, which no style knows.</summary>
    public override void AdoptStyle(Annotation style)
    {
        if (style is StepAnnotation step)
        {
            Color = step.Color;
            Diameter = step.Diameter;
        }
    }

    public override bool WearsStyle(Annotation style) => style is StepAnnotation step
        && Color == step.Color && Diameter == step.Diameter;
}

/// <summary>
/// A picture standing on the canvas: the capture itself, or one pasted in beside it.
///
/// The capture is one of these rather than a backdrop drawn before everything else, so that it can
/// be selected, moved, resized and flipped like anything pasted over it. The pixels are still never
/// touched. What the layer holds is where the picture is placed and which way round it faces; the
/// picture is an entry in the snapshot, named by <see cref="Source"/>, and is kept exactly as it
/// arrived.
///
/// Nothing else is positioned against it. Coordinates are measured from where the capture's corner
/// was when it was taken, and they stay measured from there when the capture is moved, so an arrow
/// drawn on the picture stays where it was put rather than being dragged along.
/// </summary>
public sealed class ImageAnnotation : RectAnnotation
{
    /// <summary>The entry the capture is kept in, which is the one picture every snapshot has.</summary>
    public const string Capture = "original.png";

    /// <summary>The entry in the snapshot holding the picture.</summary>
    public string Source { get; set; } = Capture;

    /// <summary>Mirrored left to right.</summary>
    public bool FlipHorizontal { get; set; }

    /// <summary>Mirrored top to bottom.</summary>
    public bool FlipVertical { get; set; }

    [JsonIgnore]
    public bool IsCapture => Source == Capture;

    public override Annotation Copy() => new ImageAnnotation
    {
        Id = Id, X = X, Y = Y, Width = Width, Height = Height,
        Source = Source, FlipHorizontal = FlipHorizontal, FlipVertical = FlipVertical
    };

    /// <summary>A picture has no look to take on. Where it is and which way it faces are not style.</summary>
    public override void AdoptStyle(Annotation style)
    {
    }

    public override bool WearsStyle(Annotation style) => false;
}

/// <summary>
/// Which way a cut runs, which is to say what it takes out of the picture.
///
/// Written out by name rather than as a number, for the same reason a colour is written as hex:
/// the document is meant to be readable and diffable, and "1" says nothing about which way a band
/// was cut.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CutAxis>))]
public enum CutAxis
{
    /// <summary>A band across the picture, taking rows out of it, so what is left is shorter.</summary>
    Rows,

    /// <summary>A band down the picture, taking columns out of it, so what is left is narrower.</summary>
    Columns
}

/// <summary>
/// A band cut out of the picture, in capture pixels.
///
/// Not a pixel edit: the capture is drawn in pieces with this band skipped and everything after it
/// closed up. Taking the band out of the document puts the picture back exactly as it was, which is
/// the same promise every other kind of editing here makes.
/// </summary>
public sealed class CutBand
{
    public CutAxis Axis { get; set; }

    /// <summary>Where the band starts, across or down the capture depending on the axis.</summary>
    public double At { get; set; }

    /// <summary>How much it takes.</summary>
    public double Extent { get; set; }

    public CutBand Copy() => new() { Axis = Axis, At = At, Extent = Extent };
}

/// <summary>
/// The canvas: the rectangle that actually gets exported, expressed in image pixels.
///
/// It is a rectangle rather than a size because it no longer has to coincide with the capture. The
/// canvas can be pulled in to crop the picture, or pushed out past it to add space, and what it
/// adds is transparent. <see cref="X"/> and <see cref="Y"/> say where its top-left corner sits
/// relative to the capture's, so a canvas wider than the capture has a negative one.
///
/// Keeping the origin on the capture rather than on the canvas is what makes resizing cheap: every
/// annotation is positioned against the picture it was drawn on, so moving the canvas moves nothing
/// else. Documents written before any of this existed have no offset at all, and zero is precisely
/// what they meant.
/// </summary>
public sealed class CanvasArea
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// A canvas somebody sized by hand, and where each picture stood when they did.
///
/// Its absence is the ordinary case, and means the canvas simply fits the pictures. See
/// <see cref="CanvasFit"/> for what the two together decide.
/// </summary>
public sealed class ManualCanvas
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Each picture's rectangle at the moment the canvas was set, by the picture's id.</summary>
    public Dictionary<string, PictureBounds> Pictures { get; set; } = [];

    public ManualCanvas Copy() => new()
    {
        X = X, Y = Y, Width = Width, Height = Height,
        Pictures = Pictures.ToDictionary(pair => pair.Key, pair => pair.Value.Copy())
    };
}

/// <summary>Where a picture stood, in capture pixels.</summary>
public sealed class PictureBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public PictureBounds Copy() => new() { X = X, Y = Y, Width = Width, Height = Height };
}

/// <summary>The contents of document.json.</summary>
public sealed class SnapshotDocument
{
    /// <summary>
    /// The document format's version.
    ///
    /// Version 1 gave blur no place in the stacking order: it was always drawn first, whatever the
    /// order of the layers, so that a blur could never hide an arrow. Version 2 draws the layers in
    /// the order they are in, which is what makes moving an object forward or back mean anything.
    /// A version 1 document is reordered as it is opened, so it still looks exactly as it did.
    ///
    /// Version 4 added the bands cut out of the picture. An older document has none, which is what
    /// it meant, so there is nothing to fix up.
    ///
    /// Version 5 put the capture among the layers, so that it can be moved and resized like a
    /// picture pasted beside it. An older document is given one at the bottom of the stack, at its
    /// own size and at the origin, which is exactly where it was always drawn.
    ///
    /// Version 5 also records whether the canvas was sized by hand, since a canvas left alone now
    /// follows the pictures. An older canvas that is not exactly the capture was cropped or padded
    /// by somebody, and is migrated as set by hand so it stays exactly as they left it.
    /// </summary>
    public const int Current = 5;

    public int Version { get; set; } = Current;

    /// <summary>The canvas as it stands, which is what gets exported.</summary>
    public CanvasArea Canvas { get; set; } = new();

    /// <summary>The canvas as it was last sized by hand, or null when it has been left to fit the pictures.</summary>
    public ManualCanvas? ManualCanvas { get; set; }

    public List<Annotation> Layers { get; set; } = [];

    /// <summary>
    /// The bands cut out of the picture, in capture pixels.
    ///
    /// Kept apart from the layers because a cut is not something drawn on the picture: it changes
    /// where the picture is, which is why it belongs beside the canvas rather than among the things
    /// standing on it.
    /// </summary>
    public List<CutBand> Cuts { get; set; } = [];

    public SnapshotDocument Copy() => new()
    {
        Version = Version,
        Canvas = new CanvasArea { X = Canvas.X, Y = Canvas.Y, Width = Canvas.Width, Height = Canvas.Height },
        ManualCanvas = ManualCanvas?.Copy(),
        Layers = [.. Layers.Select(layer => layer.Copy())],
        Cuts = [.. Cuts.Select(cut => cut.Copy())]
    };
}
