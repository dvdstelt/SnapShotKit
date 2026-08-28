using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace SnapShotKit.Ui;

/// <summary>
/// A colour, as hue, saturation and value. Hue is degrees, the other two run zero to one.
/// </summary>
public readonly record struct Hsv(double Hue, double Saturation, double Value)
{
    public static Hsv FromColor(Color colour)
    {
        double r = colour.R / 255.0, g = colour.G / 255.0, b = colour.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var span = max - min;

        var hue = 0.0;

        if (span > 0)
        {
            hue = max == r ? 60 * (((g - b) / span + 6) % 6)
                : max == g ? 60 * ((b - r) / span + 2)
                : 60 * ((r - g) / span + 4);
        }

        return new Hsv(hue, max == 0 ? 0 : span / max, max);
    }

    public Color ToColor()
    {
        var chroma = Value * Saturation;
        var second = chroma * (1 - Math.Abs(Hue / 60 % 2 - 1));
        var match = Value - chroma;

        var (r, g, b) = Hue switch
        {
            < 60 => (chroma, second, 0.0),
            < 120 => (second, chroma, 0.0),
            < 180 => (0.0, chroma, second),
            < 240 => (0.0, second, chroma),
            < 300 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + match) * 255),
            (byte)Math.Round((g + match) * 255),
            (byte)Math.Round((b + match) * 255));
    }
}

/// <summary>
/// The saturation and value square: saturation left to right, value bottom to top, at a fixed hue.
///
/// Drawn rather than composed from controls, because it is three stacked gradients and a marker,
/// and a Control that paints them is both smaller and sharper than a stack of Borders would be.
/// </summary>
public sealed class SaturationValueArea : Control
{
    static readonly IBrush ToWhite = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) }
    };

    static readonly IBrush ToBlack = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Colors.Black, 1) }
    };

    public SaturationValueArea() => Cursor = new Cursor(StandardCursorType.Cross);

    public Hsv Colour
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    } = new(0, 1, 1);

    public event Action<Hsv>? Picked;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        e.Pointer.Capture(this);
        Pick(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (Equals(e.Pointer.Captured, this))
        {
            Pick(e.GetPosition(this));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) => e.Pointer.Capture(null);

    void Pick(Point at)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        Colour = Colour with
        {
            Saturation = Math.Clamp(at.X / Bounds.Width, 0, 1),
            Value = Math.Clamp(1 - at.Y / Bounds.Height, 0, 1)
        };

        Picked?.Invoke(Colour);
    }

    public override void Render(DrawingContext context)
    {
        var area = new Rect(Bounds.Size);

        context.FillRectangle(new SolidColorBrush(new Hsv(Colour.Hue, 1, 1).ToColor()), area);
        context.FillRectangle(ToWhite, area);
        context.FillRectangle(ToBlack, area);
        context.DrawRectangle(null, Tokens.DividerPen, area.Deflate(0.5));

        // The marker is a ring rather than a dot, so the colour under it stays visible.
        var at = new Point(Colour.Saturation * area.Width, (1 - Colour.Value) * area.Height);

        context.DrawEllipse(null, new Pen(Brushes.White, 2), at, 5, 5);
        context.DrawEllipse(null, new Pen(Brushes.Black, 1), at, 6.5, 6.5);
    }
}

/// <summary>The hue strip beside the square: the full spectrum, top to bottom.</summary>
public sealed class HueStrip : Control
{
    static readonly IBrush Spectrum = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(255, 0, 0), 0),
            new GradientStop(Color.FromRgb(255, 255, 0), 1 / 6.0),
            new GradientStop(Color.FromRgb(0, 255, 0), 2 / 6.0),
            new GradientStop(Color.FromRgb(0, 255, 255), 3 / 6.0),
            new GradientStop(Color.FromRgb(0, 0, 255), 4 / 6.0),
            new GradientStop(Color.FromRgb(255, 0, 255), 5 / 6.0),
            new GradientStop(Color.FromRgb(255, 0, 0), 1)
        }
    };

    public HueStrip() => Cursor = new Cursor(StandardCursorType.Hand);

    public double Hue
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    public event Action<double>? Picked;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        e.Pointer.Capture(this);
        Pick(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (Equals(e.Pointer.Captured, this))
        {
            Pick(e.GetPosition(this));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) => e.Pointer.Capture(null);

    void Pick(Point at)
    {
        if (Bounds.Height <= 0)
        {
            return;
        }

        Hue = Math.Clamp(at.Y / Bounds.Height, 0, 1) * 360;
        Picked?.Invoke(Hue);
    }

    public override void Render(DrawingContext context)
    {
        var area = new Rect(Bounds.Size);

        context.FillRectangle(Spectrum, area);
        context.DrawRectangle(null, Tokens.DividerPen, area.Deflate(0.5));

        var y = Hue / 360 * area.Height;

        context.DrawLine(new Pen(Brushes.White, 2), new Point(0, y), new Point(area.Width, y));
        context.DrawLine(new Pen(Brushes.Black, 1), new Point(0, y - 1.5), new Point(area.Width, y - 1.5));
        context.DrawLine(new Pen(Brushes.Black, 1), new Point(0, y + 1.5), new Point(area.Width, y + 1.5));
    }
}

/// <summary>
/// Any colour at all: a saturation and value square, a hue strip, and the hex code.
///
/// Built rather than taken from Avalonia's own colour picker, which is Fluent shaped: rounded
/// corners and pill controls read as a foreign object in a design whose whole grammar is square
/// and hairline. The three parts here are the ones that get used.
/// </summary>
public sealed class ColourPicker : StackPanel
{
    readonly SaturationValueArea square = new() { Width = 196, Height = 132 };
    readonly HueStrip hue = new() { Width = 18, Height = 132 };
    readonly TextBox hex;
    readonly Border preview;

    bool syncing;

    public ColourPicker(string initial)
    {
        Spacing = Tokens.Space.S2;

        var start = Color.TryParse(initial, out var parsed) ? parsed : Colors.Red;
        Current = start;

        square.Colour = Hsv.FromColor(start);
        hue.Hue = square.Colour.Hue;

        hex = new TextBox
        {
            Text = ToHex(start),
            FontFamily = Tokens.Fonts.Body,
            FontSize = 12.5,
            CornerRadius = Tokens.Radius,
            Padding = new Thickness(Tokens.Space.S2, 3),
            MinHeight = 0,
            Width = 96
        };

        preview = new Border
        {
            Width = 26,
            Height = 26,
            Background = new SolidColorBrush(start),
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius
        };

        square.Picked += _ => Commit(square.Colour.ToColor());

        hue.Picked += value =>
        {
            square.Colour = square.Colour with { Hue = value };
            Commit(square.Colour.ToColor());
        };

        hex.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || syncing)
            {
                return;
            }

            // Typed text is only taken when it parses. Rejecting it silently is right here: the
            // field is mid-edit for most of the keystrokes it will ever see.
            if (Color.TryParse(Normalise(hex.Text), out var typed))
            {
                square.Colour = Hsv.FromColor(typed);
                hue.Hue = square.Colour.Hue;
                Commit(typed, echoToHex: false);
            }
        };

        var surfaces = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S2,
            Children = { square, hue }
        };

        var entry = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { preview, hex }
        };

        Children.Add(surfaces);
        Children.Add(entry);
    }

    public Color Current { get; private set; }

    public event Action<string>? Chosen;

    void Commit(Color colour, bool echoToHex = true)
    {
        Current = colour;
        preview.Background = new SolidColorBrush(colour);

        if (echoToHex)
        {
            syncing = true;
            try
            {
                hex.Text = ToHex(colour);
            }
            finally
            {
                syncing = false;
            }
        }

        Chosen?.Invoke(ToHex(colour));
    }

    static string ToHex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    /// <summary>Accepts a hex code with or without its hash, since people type it both ways.</summary>
    static string Normalise(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return value.StartsWith('#') ? value : "#" + value;
    }
}
