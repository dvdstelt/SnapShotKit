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

    /// <param name="suppress">
    /// An annotation to leave undrawn. Used while text is being typed in place, where the editor
    /// itself is showing the words: drawing them underneath as well would double every stroke.
    /// </param>
    public static void Draw(DrawingContext context, Snapshot snapshot, BlurCache blurs, Rect target,
        Annotation? suppress = null)
    {
        var canvas = snapshot.Document.Canvas;
        var scale = canvas.Width == 0 ? 1 : target.Width / canvas.Width;

        context.DrawImage(snapshot.Bitmap, target);

        // In the order they are in. What is on top is the user's to decide, which is why every
        // annotation can be moved forward and back; a rule that always put one kind underneath
        // would quietly override that choice.
        foreach (var annotation in snapshot.Document.Layers)
        {
            if (ReferenceEquals(annotation, suppress))
            {
                continue;
            }

            switch (annotation)
            {
                case BlurAnnotation blur:
                    DrawBlur(context, blurs, blur, target, scale);
                    break;

                case BoxAnnotation box:
                    DrawBox(context, box, target, scale);
                    break;

                case ArrowAnnotation arrow:
                    DrawArrow(context, arrow, target, scale);
                    break;

                case StepAnnotation step:
                    DrawStep(context, step, target, scale);
                    break;

                case TextAnnotation text:
                    DrawText(context, text, target, scale);
                    break;
            }
        }
    }

    /// <summary>Text, on its plate when it has one.</summary>
    static void DrawText(DrawingContext context, TextAnnotation text, Rect target, double scale)
    {
        var formatted = Format(text, scale);
        var origin = new Point(target.X + text.X * scale, target.Y + text.Y * scale);

        if (text.HasBackground)
        {
            var padding = text.BackgroundPadding * scale;

            context.FillRectangle(BrushFor(text.Background), new Rect(
                origin.X - padding,
                origin.Y - padding,
                formatted.Width + 2 * padding,
                formatted.Height + 2 * padding));
        }

        context.DrawText(formatted, origin);
    }

    /// <summary>A numbered marker: a filled disc with its number centred in it.</summary>
    static void DrawStep(DrawingContext context, StepAnnotation step, Rect target, double scale)
    {
        var centre = new Point(target.X + step.X * scale, target.Y + step.Y * scale);
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

    static void DrawBox(DrawingContext context, BoxAnnotation box, Rect target, double scale)
    {
        var rect = new Rect(
            target.X + box.X * scale,
            target.Y + box.Y * scale,
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

    static void DrawBlur(DrawingContext context, BlurCache blurs, BlurAnnotation blur, Rect target, double scale)
    {
        var source = new Rect(blur.X, blur.Y, Math.Max(blur.Width, 1), Math.Max(blur.Height, 1));

        var destination = new Rect(
            target.X + blur.X * scale,
            target.Y + blur.Y * scale,
            Math.Max(blur.Width * scale, 1),
            Math.Max(blur.Height * scale, 1));

        // The region is simply the same patch of an already blurred copy of the capture.
        //
        // Nothing else is drawn on it here. A blurred region carries an edge and a caption on the
        // editing canvas, but those belong to editing: an exported screenshot must not come out
        // with "BLUR 2" printed across the thing the user was hiding.
        context.DrawImage(blurs.For(blur.Strength), source, destination);
    }

    static void DrawArrow(DrawingContext context, ArrowAnnotation arrow, Rect target, double scale)
    {
        var from = new Point(target.X + arrow.X1 * scale, target.Y + arrow.Y1 * scale);
        var to = new Point(target.X + arrow.X2 * scale, target.Y + arrow.Y2 * scale);

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

    public static Color ParseColor(string value)
        => Color.TryParse(value, out var color) ? color : Colors.Red;
}
