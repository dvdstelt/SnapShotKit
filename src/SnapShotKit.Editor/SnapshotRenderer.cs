using Avalonia;
using Avalonia.Media;

namespace SnapShotKit.Editor;

/// <summary>
/// Draws a snapshot and its annotations.
///
/// Used for both the editing canvas and the exported image, deliberately: two rendering paths would
/// drift, and the whole promise of the format is that what you exported is what you saw.
/// </summary>
public static class SnapshotRenderer
{
    /// <summary>
    /// Brushes by hex value. The canvas repaints on every pointer movement, so allocating a brush
    /// per annotation per frame is churn for nothing; the palette is small and the brushes are
    /// immutable, so they are simply kept. Only ever touched from the interface thread.
    /// </summary>
    static readonly Dictionary<string, IBrush> Brushes = [];

    public static IBrush BrushFor(string value)
    {
        if (!Brushes.TryGetValue(value, out var brush))
        {
            brush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(ParseColor(value));
            Brushes[value] = brush;
        }

        return brush;
    }

    /// <param name="area">
    /// The stretch of image space being drawn, in image pixels. Usually the canvas, which is what
    /// gets exported. The editor passes something larger while the canvas is being resized, so that
    /// what falls outside it can be seen rather than guessed at.
    /// </param>
    /// <param name="target">Where that stretch lands.</param>
    /// <param name="suppress">
    /// An annotation to leave undrawn. Used while text is being typed in place, where the editor
    /// itself is showing the words: drawing them underneath as well would double every stroke.
    /// </param>
    public static void Draw(DrawingContext context, Snapshot snapshot, BlurCache blurs, Rect target, Rect area,
        Annotation? suppress = null)
    {
        var scale = area.Width == 0 ? 1 : target.Width / area.Width;

        // Everything drawn on a snapshot is positioned against the capture's top-left corner rather
        // than the canvas's, so that cropping the canvas in or pushing it out moves nothing that was
        // drawn on it. This is where that corner falls on the target.
        var origin = Origin(area, target, scale);

        // The capture at its own size, wherever the canvas sits around it. Whatever the canvas
        // covers beyond the capture is simply not painted, which is what makes it transparent.
        context.DrawImage(snapshot.Bitmap, new Rect(origin, new Size(
            snapshot.Bitmap.PixelSize.Width * scale,
            snapshot.Bitmap.PixelSize.Height * scale)));

        // In the order they are in. What is on top is the user's to decide, which is why every
        // annotation can be moved forward and back; a rule that always put one kind underneath
        // would quietly override that choice.
        foreach (var annotation in snapshot.Document.Layers)
        {
            if (ReferenceEquals(annotation, suppress))
            {
                continue;
            }

            DrawAnnotation(context, annotation, blurs, origin, scale);
        }
    }

    /// <summary>
    /// One annotation, wherever the image's origin has landed.
    ///
    /// Public so that the band's style previews go through it too: a preview drawn by any other
    /// code would eventually stop looking like the thing it promises.
    /// </summary>
    /// <param name="blurs">The blurred copies of the capture, or null where there is no capture to blur, as in a preview.</param>
    public static void DrawAnnotation(DrawingContext context, Annotation annotation, BlurCache? blurs,
        Point origin, double scale)
    {
        switch (annotation)
        {
            case BlurAnnotation blur when blurs is not null:
                DrawBlur(context, blurs, blur, origin, scale);
                break;

            case BoxAnnotation box:
                DrawBox(context, box, origin, scale);
                break;

            case ArrowAnnotation arrow:
                DrawArrow(context, arrow, origin, scale);
                break;

            case StepAnnotation step:
                DrawStep(context, step, origin, scale);
                break;

            case TextAnnotation text:
                DrawText(context, text, origin, scale);
                break;
        }
    }

    /// <summary>Text, on its plate when it has one.</summary>
    static void DrawText(DrawingContext context, TextAnnotation text, Point origin, double scale)
    {
        var formatted = Format(text, scale);
        var at = new Point(origin.X + text.X * scale, origin.Y + text.Y * scale);

        if (text.HasBackground)
        {
            var padding = text.BackgroundPadding * scale;

            context.FillRectangle(BrushFor(text.Background), new Rect(
                at.X - padding,
                at.Y - padding,
                formatted.Width + 2 * padding,
                formatted.Height + 2 * padding));
        }

        context.DrawText(formatted, at);
    }

    /// <summary>A numbered marker: a filled disc with its number centred in it.</summary>
    static void DrawStep(DrawingContext context, StepAnnotation step, Point origin, double scale)
    {
        var centre = new Point(origin.X + step.X * scale, origin.Y + step.Y * scale);
        var radius = Math.Max(step.Radius * scale, 1);
        var fill = ParseColor(step.Color);

        context.DrawEllipse(new SolidColorBrush(fill), null, centre, radius, radius);

        // The number takes whichever of black or white reads on the disc, rather than being a
        // colour of its own to choose and get wrong.
        var number = new FormattedText(
            step.Number.ToString(),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(SnapShotKit.Ui.Tokens.Fonts.Body, weight: FontWeight.SemiBold),
            radius * 1.15,
            new SolidColorBrush(Legible(fill)));

        context.DrawText(number, new Point(centre.X - number.Width / 2, centre.Y - number.Height / 2));
    }

    /// <summary>Black or white, whichever stands out on the given colour.</summary>
    public static Color Legible(Color on) =>
        (0.299 * on.R + 0.587 * on.G + 0.114 * on.B) / 255 > 0.6 ? Color.FromRgb(0x1D, 0x1F, 0x20) : Colors.White;

    static void DrawBox(DrawingContext context, BoxAnnotation box, Point origin, double scale)
    {
        var rect = new Rect(
            origin.X + box.X * scale,
            origin.Y + box.Y * scale,
            Math.Max(box.Width * scale, 1),
            Math.Max(box.Height * scale, 1));

        var fill = box.HasFill ? BrushFor(box.FillColor) : null;
        var thickness = Math.Max(box.BorderThickness * scale, 0);
        var pen = thickness > 0 ? new Pen(BrushFor(box.BorderColor), thickness) : null;

        context.DrawRectangle(fill, pen, rect);
    }

    /// <summary>Lays out an annotation's text. Shared so hit testing measures exactly what is drawn.</summary>
    public static FormattedText Format(TextAnnotation text, double scale) => new(
        string.IsNullOrEmpty(text.Text) ? " " : text.Text,
        System.Globalization.CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        new Typeface(FontFamily.Parse(text.FontFamily)),
        Math.Max(text.FontSize * scale, 1),
        BrushFor(text.Color));

    static void DrawBlur(DrawingContext context, BlurCache blurs, BlurAnnotation blur, Point origin, double scale)
    {
        var source = new Rect(blur.X, blur.Y, Math.Max(blur.Width, 1), Math.Max(blur.Height, 1));

        var destination = new Rect(
            origin.X + blur.X * scale,
            origin.Y + blur.Y * scale,
            Math.Max(blur.Width * scale, 1),
            Math.Max(blur.Height * scale, 1));

        // The region is simply the same patch of an already blurred copy of the capture.
        //
        // Nothing else is drawn on it here. A blurred region carries an edge and a caption on the
        // editing canvas, but those belong to editing: an exported screenshot must not come out
        // with "BLUR 2" printed across the thing the user was hiding.
        context.DrawImage(blurs.For(blur.Strength), source, destination);
    }

    static void DrawArrow(DrawingContext context, ArrowAnnotation arrow, Point origin, double scale)
    {
        var from = new Point(origin.X + arrow.X1 * scale, origin.Y + arrow.Y1 * scale);
        var to = new Point(origin.X + arrow.X2 * scale, origin.Y + arrow.Y2 * scale);

        var span = to - from;
        var length = Math.Sqrt(span.X * span.X + span.Y * span.Y);

        if (length < 0.5)
        {
            return;
        }

        var direction = new Vector(span.X / length, span.Y / length);

        var brush = BrushFor(arrow.Color);
        var thickness = Math.Max(arrow.Thickness * scale, 1);

        var headLength = Math.Min(thickness * 3.4, length / (arrow.DoubleHeaded ? 2 : 1));

        // Stop the shaft just short of each head so the two do not overlap into a blob at low
        // thickness or a notch at high thickness.
        var shaftEnd = to - direction * (headLength * 0.85);
        var shaftStart = arrow.DoubleHeaded ? from + direction * (headLength * 0.85) : from;

        context.DrawLine(new Pen(brush, thickness, lineCap: PenLineCap.Round), shaftStart, shaftEnd);

        DrawHead(context, brush, to, direction, headLength, thickness);

        if (arrow.DoubleHeaded)
        {
            DrawHead(context, brush, from, -direction, headLength, thickness);
        }
    }

    /// <summary>One solid triangular head, pointing along <paramref name="direction"/> at <paramref name="tip"/>.</summary>
    static void DrawHead(DrawingContext context, IBrush brush, Point tip, Vector direction, double headLength, double thickness)
    {
        var normal = new Vector(-direction.Y, direction.X);
        var baseCentre = tip - direction * headLength;
        var halfWidth = thickness * 1.7;

        var head = new StreamGeometry();
        using (var sink = head.Open())
        {
            sink.BeginFigure(tip, true);
            sink.LineTo(baseCentre + normal * halfWidth);
            sink.LineTo(baseCentre - normal * halfWidth);
            sink.EndFigure(true);
        }

        context.DrawGeometry(brush, null, head);
    }

    /// <summary>
    /// Where the capture's top-left corner falls, given which stretch of image space is on show.
    ///
    /// Shared with the editing canvas, which has to map a pointer back the other way and must agree
    /// with this to the pixel or every click lands somewhere else than it looks.
    /// </summary>
    public static Point Origin(Rect area, Rect target, double scale) =>
        new(target.X - area.X * scale, target.Y - area.Y * scale);

    public static Color ParseColor(string value)
        => Color.TryParse(value, out var color) ? color : Colors.Red;
}
