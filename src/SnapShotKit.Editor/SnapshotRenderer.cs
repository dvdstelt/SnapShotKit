using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

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
    /// The stretch of laid-out space being drawn, in image pixels with the cuts already closed.
    /// Usually the canvas, which is what gets exported. The editor passes something larger while the
    /// canvas is being resized, so that what falls outside it can be seen rather than guessed at.
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
        // drawn on it. This is where that corner falls on the target, before any cut moves it.
        var origin = Origin(area, target, scale);

        // A piece at a time, each one the whole picture drawn shifted by what the cuts before it
        // took and clipped to its own band. With nothing cut that is one piece, no shift and a clip
        // around everything, which is the same drawing as before cuts existed.
        var layout = snapshot.Layout;

        foreach (var (piece, shift) in layout.Pieces(layout.ToCapture(area)))
        {
            var laid = new Rect(piece.X - shift.X, piece.Y - shift.Y, piece.Width, piece.Height);

            var within = new Rect(
                origin.X + laid.X * scale,
                origin.Y + laid.Y * scale,
                laid.Width * scale,
                laid.Height * scale);

            if (within.Width <= 0 || within.Height <= 0)
            {
                continue;
            }

            using (context.PushClip(within))
            {
                DrawPiece(context, snapshot, blurs, origin - shift * scale, scale, suppress);
            }
        }
    }

    /// <summary>The pictures and everything on them, positioned in capture pixels from a given corner.</summary>
    static void DrawPiece(DrawingContext context, Snapshot snapshot, BlurCache blurs, Point origin, double scale,
        Annotation? suppress)
    {
        var layers = snapshot.Document.Layers;
        var dimmed = false;

        // In the order they are in. What is on top is the user's to decide, which is why every
        // annotation can be moved forward and back; a rule that always put one kind underneath
        // would quietly override that choice. The capture is a layer like the rest, usually the
        // first. Whatever the canvas covers that no picture does is simply not painted, which is
        // what makes it transparent.
        for (var index = 0; index < layers.Count; index++)
        {
            var annotation = layers[index];

            if (ReferenceEquals(annotation, suppress))
            {
                continue;
            }

            switch (annotation)
            {
                case ImageAnnotation image when snapshot.BitmapOf(image.Source) is { } bitmap:
                    DrawPicture(context, bitmap, image, origin, scale);
                    break;

                case BlurAnnotation blur:
                    DrawBlur(context, snapshot, blurs, blur, index, origin, scale);
                    break;

                // All of them at once, where the first of them stands in the stack. One dimming
                // with a hole for each, so a second spotlight is a second thing to look at rather
                // than something that dims the first.
                case MagnifyAnnotation magnify:
                    DrawMagnify(context, snapshot, magnify, index, origin, scale);
                    break;

                case SpotlightAnnotation when !dimmed:
                    DrawSpotlights(context, snapshot, origin, scale, suppress);
                    dimmed = true;
                    break;

                case SpotlightAnnotation:
                    break;

                default:
                    DrawAnnotation(context, annotation, origin, scale);
                    break;
            }
        }
    }

    /// <summary>
    /// A picture where its layer puts it, stretched to the layer's size and mirrored as it says.
    ///
    /// Mirrored by drawing through a transform about the picture's own centre, so a flipped
    /// picture occupies exactly the rectangle an unflipped one would: flipping turns it round
    /// where it stands rather than swinging it across the canvas.
    /// </summary>
    static void DrawPicture(DrawingContext context, Bitmap bitmap, ImageAnnotation image, Point origin, double scale)
    {
        var destination = new Rect(
            origin.X + image.X * scale,
            origin.Y + image.Y * scale,
            Math.Max(image.Width * scale, 1),
            Math.Max(image.Height * scale, 1));

        if (!image.FlipHorizontal && !image.FlipVertical)
        {
            context.DrawImage(bitmap, destination);
            return;
        }

        var centre = destination.Center;

        var mirror = Matrix.CreateTranslation(-centre.X, -centre.Y)
            * Matrix.CreateScale(image.FlipHorizontal ? -1 : 1, image.FlipVertical ? -1 : 1)
            * Matrix.CreateTranslation(centre.X, centre.Y);

        using (context.PushTransform(mirror))
        {
            context.DrawImage(bitmap, destination);
        }
    }

    /// <summary>
    /// One annotation, wherever the image's origin has landed.
    ///
    /// Public so that the band's style previews go through it too: a preview drawn by any other
    /// code would eventually stop looking like the thing it promises.
    ///
    /// Not a blur and not a picture. Both depend on the document around them, what is under the
    /// blur and which picture a layer names, so the document's own pass draws those, and a preview
    /// has neither.
    /// </summary>
    public static void DrawAnnotation(DrawingContext context, Annotation annotation, Point origin, double scale)
    {
        switch (annotation)
        {
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

            case PenAnnotation pen:
                DrawPen(context, pen, origin, scale);
                break;
        }
    }

    /// <summary>A hand-drawn line, with round ends and round joins so a sharp turn of the hand is a corner and not a spike.</summary>
    static void DrawPen(DrawingContext context, PenAnnotation pen, Point origin, double scale)
    {
        if (pen.Points.Count < 4)
        {
            return;
        }

        var path = new StreamGeometry();

        using (var sink = path.Open())
        {
            sink.BeginFigure(new Point(origin.X + pen.Points[0] * scale, origin.Y + pen.Points[1] * scale), false);

            for (var index = 2; index + 1 < pen.Points.Count; index += 2)
            {
                sink.LineTo(new Point(origin.X + pen.Points[index] * scale, origin.Y + pen.Points[index + 1] * scale));
            }

            sink.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(BrushFor(pen.Color), Math.Max(pen.Thickness * scale, 1),
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), path);
    }

    /// <summary>Text, on its plate when it has one.</summary>
    static void DrawText(DrawingContext context, TextAnnotation text, Point origin, double scale)
    {
        var formatted = Format(text, scale);
        var at = new Point(origin.X + text.X * scale, origin.Y + text.Y * scale);

        if (text.HasBackground)
        {
            var padding = text.BackgroundPadding * scale;

            var plate = new Rect(
                at.X - padding,
                at.Y - padding,
                formatted.Width + 2 * padding,
                formatted.Height + 2 * padding);

            if (text.HasTail)
            {
                DrawTail(context, BrushFor(text.Background), plate,
                    new Point(origin.X + text.TailX * scale, origin.Y + text.TailY * scale));
            }

            context.FillRectangle(BrushFor(text.Background), plate);
        }

        context.DrawText(formatted, at);
    }

    /// <summary>
    /// A callout's tail: a triangle from the middle of the plate to the tip.
    ///
    /// From the middle rather than from an edge, with the plate painted over it afterwards, so
    /// only the part outside the plate shows. That way there is no working out which edge the tail
    /// leaves by or where along it, and no seam where it crosses a corner: it works the same
    /// whichever way the tip lies. A tip inside the plate is a tail with nowhere to go, and is not
    /// drawn.
    /// </summary>
    static void DrawTail(DrawingContext context, IBrush brush, Rect plate, Point tip)
    {
        if (plate.Contains(tip))
        {
            return;
        }

        var span = tip - plate.Center;
        var length = Math.Sqrt(span.X * span.X + span.Y * span.Y);

        if (length < 1)
        {
            return;
        }

        // As wide where it leaves as the plate can carry: a third of its shorter side either way.
        var across = new Vector(-span.Y / length, span.X / length) * (Math.Min(plate.Width, plate.Height) * 0.33);

        var tail = new StreamGeometry();

        using (var sink = tail.Open())
        {
            sink.BeginFigure(tip, true);
            sink.LineTo(plate.Center + across);
            sink.LineTo(plate.Center - across);
            sink.EndFigure(true);
        }

        context.DrawGeometry(brush, null, tail);
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

        if (box.Ellipse)
        {
            context.DrawEllipse(fill, pen, rect.Center, rect.Width / 2, rect.Height / 2);
            return;
        }

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

    /// <summary>
    /// A blurred region: every picture beneath it, drawn again blurred and clipped to the region.
    ///
    /// Each picture is drawn from an already blurred copy of itself, placed and mirrored exactly as
    /// the picture is, so the blur lines up with what it hides wherever that picture has been moved
    /// to or however it has been stretched. For the capture at the origin at its own size this is
    /// pixel for pixel the patch of the blurred capture it always was.
    ///
    /// Only pictures below the blur in the stack, and only those it actually overlaps: one pasted
    /// on top of a blur is meant to be seen, and blurring a picture nothing covers would be a
    /// full-resolution gaussian for no visible difference.
    ///
    /// Nothing else is drawn on it here. A blurred region carries an edge and a caption on the
    /// editing canvas, but those belong to editing: an exported screenshot must not come out with
    /// "BLUR 2" printed across the thing the user was hiding.
    /// </summary>
    static void DrawBlur(DrawingContext context, Snapshot snapshot, BlurCache blurs, BlurAnnotation blur, int index,
        Point origin, double scale)
    {
        var region = RegionOf(blur);

        var destination = new Rect(
            origin.X + region.X * scale,
            origin.Y + region.Y * scale,
            region.Width * scale,
            region.Height * scale);

        if (blur.Mode == HideMode.Solid)
        {
            // Nothing of what is underneath goes into this, so nothing of it can be got back out.
            context.FillRectangle(Avalonia.Media.Brushes.Black, destination);
            return;
        }

        using (context.PushClip(destination))
        {
            for (var below = 0; below < index; below++)
            {
                if (snapshot.Document.Layers[below] is ImageAnnotation image
                    && Hides(blur, image)
                    && blurs.For(image.Source, blur.Strength, blur.Mode) is { } blurred)
                {
                    DrawPicture(context, blurred, image, origin, scale);
                }
            }
        }
    }

    /// <summary>
    /// The canvas dimmed, with every spotlight cut out of the dimming.
    ///
    /// As dark as the darkest of them asks for. There is one dimming, so it has one strength, and
    /// the one who asked for most is the one who would notice getting less.
    /// </summary>
    static void DrawSpotlights(DrawingContext context, Snapshot snapshot, Point origin, double scale, Annotation? suppress)
    {
        var spotlights = snapshot.Document.Layers
            .OfType<SpotlightAnnotation>()
            .Where(spotlight => !ReferenceEquals(spotlight, suppress))
            .ToList();

        if (spotlights.Count == 0)
        {
            return;
        }

        Rect Placed(double x, double y, double width, double height) =>
            new(origin.X + x * scale, origin.Y + y * scale, Math.Max(width, 1) * scale, Math.Max(height, 1) * scale);

        var canvas = snapshot.Document.Canvas;
        var holes = new GeometryGroup { FillRule = FillRule.NonZero };

        foreach (var spotlight in spotlights)
        {
            holes.Children.Add(new RectangleGeometry(Placed(spotlight.X, spotlight.Y, spotlight.Width, spotlight.Height)));
        }

        var dimming = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(Placed(canvas.X, canvas.Y, canvas.Width, canvas.Height)),
            holes);

        var strength = Math.Clamp(spotlights.Max(spotlight => spotlight.Dim), 1, 100) / 100.0;

        context.DrawGeometry(new SolidColorBrush(Colors.Black, strength), null, dimming);
    }

    static readonly IPen LensEdge = new Pen(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x33)), 2);
    static readonly IPen LensHalo = new Pen(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1);

    /// <summary>
    /// A lens: every picture below it, drawn again larger and clipped to its frame.
    ///
    /// The pictures are drawn through the same routine that draws them in the first place, with
    /// the origin moved so that the point under the middle of the lens stays where it is while
    /// everything grows away from it. That is all a magnification about a point is, and it means a
    /// flipped or stretched picture is magnified flipped and stretched, without the lens knowing.
    ///
    /// The ground under the lens is painted first, white, so that a lens hanging over the edge of
    /// a picture magnifies emptiness as emptiness rather than showing the unmagnified picture
    /// through the gap. The frame is dark with a light line outside it, which is the one pairing
    /// that shows up on a light screenshot and a dark one alike.
    /// </summary>
    static void DrawMagnify(DrawingContext context, Snapshot snapshot, MagnifyAnnotation magnify, int index,
        Point origin, double scale)
    {
        var frame = new Rect(
            origin.X + magnify.X * scale,
            origin.Y + magnify.Y * scale,
            Math.Max(magnify.Width, 1) * scale,
            Math.Max(magnify.Height, 1) * scale);

        var zoom = Math.Clamp(magnify.Zoom, 1, 16);

        var middle = new Point(magnify.X + magnify.Width / 2, magnify.Y + magnify.Height / 2);
        var grown = scale * zoom;
        var moved = new Point(origin.X + middle.X * (scale - grown), origin.Y + middle.Y * (scale - grown));

        using (context.PushClip(frame))
        {
            context.FillRectangle(Avalonia.Media.Brushes.White, frame);

            for (var below = 0; below < index; below++)
            {
                if (snapshot.Document.Layers[below] is ImageAnnotation image
                    && snapshot.BitmapOf(image.Source) is { } bitmap)
                {
                    DrawPicture(context, bitmap, image, moved, grown);
                }
            }
        }

        context.DrawRectangle(null, LensHalo, frame.Inflate(1.5));
        context.DrawRectangle(null, LensEdge, frame);
    }

    static Rect RegionOf(BlurAnnotation blur) => new(blur.X, blur.Y, Math.Max(blur.Width, 1), Math.Max(blur.Height, 1));

    /// <summary>
    /// Whether a blur lies over any of a picture beneath it, which is when a blurred copy of that
    /// picture is drawn. Asked by the cache as well, so that what it holds on to is exactly what is
    /// drawn from.
    /// </summary>
    public static bool Hides(BlurAnnotation blur, ImageAnnotation image) =>
        RegionOf(blur).Intersects(new Rect(image.X, image.Y, image.Width, image.Height));

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

        if (arrow.Headless)
        {
            // A line: the shaft from end to end, with nothing to stop short of.
            context.DrawLine(new Pen(brush, thickness, lineCap: PenLineCap.Round), from, to);
            return;
        }

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
