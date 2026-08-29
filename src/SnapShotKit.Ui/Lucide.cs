using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace SnapShotKit.Ui;

/// <summary>
/// The Lucide glyphs the design calls for, as path data on Lucide's own 24 by 24 grid.
///
/// Transcribed from lucide.dev rather than shipped as image assets, so they stay sharp at any size
/// and recolour with the interface. Every glyph is stroked, never filled, at stroke width 1.5:
/// the design system is explicit that thick icon strokes are wrong for it.
///
/// Rectangles from the original SVGs are written out as paths here, since Avalonia parses path data
/// but has no notion of an SVG rect element.
/// </summary>
public static class Lucide
{
    /// <summary>mouse-pointer-2. The select tool.</summary>
    public const string Select = "M4,4 L11.07,21 L13.58,13.61 L21,11.07 Z";

    /// <summary>arrow-up-right. The arrow tool.</summary>
    public const string Arrow = "M7,7 H17 V17 M7,17 L17,7";

    /// <summary>square. The box tool.</summary>
    public const string Box = "M5,3 H19 A2,2 0 0 1 21,5 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V5 A2,2 0 0 1 5,3 Z";

    /// <summary>droplet. The blur tool.</summary>
    public const string Blur = "M12,22 A7,7 0 0 0 19,15 C19,13 18,11.1 16,9.5 "
        + "C14,7.9 12.5,5.5 12,3 C11.5,5.5 10,7.9 8,9.5 C6,11.1 5,13 5,15 A7,7 0 0 0 12,22 Z";

    /// <summary>type. The text tool.</summary>
    public const string Text = "M12,4 V20 M4,7 V4 H20 V7 M9,20 H6.5 M9,20 H15";

    /// <summary>hard-drive-download. Save to disk.</summary>
    public const string SaveToDisk = "M12,2 V10 M16,6 L12,10 L8,6 "
        + "M4,14 H20 A2,2 0 0 1 22,16 V20 A2,2 0 0 1 20,22 H4 A2,2 0 0 1 2,20 V16 A2,2 0 0 1 4,14 Z "
        + "M6,18 h0.01 M10,18 h0.01";

    /// <summary>square-pen. Open in editor.</summary>
    public const string OpenInEditor = "M12,3 H5 A2,2 0 0 0 3,5 V19 A2,2 0 0 0 5,21 H19 A2,2 0 0 0 21,19 V12 "
        + "M18.375,2.625 a1,1 0 0 1 3,3 L12.362,14.639 a2,2 0 0 1 -0.853,0.505 l-2.873,0.84 "
        + "a0.5,0.5 0 0 1 -0.62,-0.62 l0.84,-2.873 a2,2 0 0 1 0.506,-0.852 Z";

    /// <summary>clipboard-copy. Copy to clipboard.</summary>
    public const string CopyToClipboard = "M9,2 H15 A1,1 0 0 1 16,3 V5 A1,1 0 0 1 15,6 H9 A1,1 0 0 1 8,5 V3 A1,1 0 0 1 9,2 Z "
        + "M8,4 H6 A2,2 0 0 0 4,6 V20 A2,2 0 0 0 6,22 H8 "
        + "M16,4 H18 A2,2 0 0 1 20,6 V8 "
        + "M21,14 H11 M15,10 L11,14 L15,18";

    /// <summary>x. Cancel.</summary>
    public const string Cancel = "M18,6 L6,18 M6,6 L18,18";

    /// <summary>monitor. Whole screen.</summary>
    public const string WholeScreen = "M4,3 H20 A2,2 0 0 1 22,5 V15 A2,2 0 0 1 20,17 H4 A2,2 0 0 1 2,15 V5 A2,2 0 0 1 4,3 Z "
        + "M8,21 H16 M12,17 V21";

    /// <summary>circle with a one in it. The numbered marker, drawn rather than taken from the set, which has no numbered disc.</summary>
    public const string Step = "M12,3 A9,9 0 1 1 11.9,3 Z M10.6,9.6 L12.6,8.4 L12.6,15.6 M10.6,15.6 L14.6,15.6";

    /// <summary>plus. Zooming in.</summary>
    public const string Plus = "M5,12 H19 M12,5 V19";

    /// <summary>minus. Zooming out.</summary>
    public const string Minus = "M5,12 H19";

    /// <summary>crop. Resizing the canvas.</summary>
    public const string Crop = "M6,2 V16 A2,2 0 0 0 8,18 H22 M18,22 V8 A2,2 0 0 0 16,6 H2";

    /// <summary>folder. The library.</summary>
    public const string Library = "M4,20 A2,2 0 0 1 2,18 V5 A2,2 0 0 1 4,3 H9 L12,6 H20 A2,2 0 0 1 22,8 V18 A2,2 0 0 1 20,20 Z";

    /// <summary>
    /// Builds a glyph at a given size.
    ///
    /// A Viewbox rather than a stretched path, because Lucide's stroke width is expressed on the
    /// 24 unit grid: scaling the whole drawing scales the stroke with it, exactly as the browser
    /// does, where stretching the path alone would leave a 1.5 pixel stroke on a 17 pixel icon.
    /// </summary>
    public static Control Icon(string geometry, double size, IBrush stroke) => new Viewbox
    {
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new Path
        {
            Data = Geometry.Parse(geometry),
            Width = 24,
            Height = 24,
            Stroke = stroke,
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        }
    };
}
