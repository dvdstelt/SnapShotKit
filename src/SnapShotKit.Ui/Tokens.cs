using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace SnapShotKit.Ui;

/// <summary>
/// The Industry design system's tokens, as the rest of the application sees them.
///
/// Every colour, size and space in SnapShotKit's interface comes from here. The design system is
/// explicit that values are taken from its token sheet rather than written inline, and a single
/// origin is what keeps the three windows looking like one application: the editor, the overlay and
/// the library are separate processes and would otherwise drift apart a shade at a time.
///
/// Values are transcribed from docs/design/industry-design-system/styles.css, which is authoritative.
///
/// Named Tokens rather than Theme, which the handoff suggested, because every Avalonia control
/// already inherits a Theme property: a class by that name is shadowed inside exactly the control
/// subclasses that need it most.
/// </summary>
public static class Tokens
{
    /// <summary>Window ground, menu bar, bands, dropdowns.</summary>
    public static readonly Color Bg = Color.FromRgb(0xF2, 0xF2, 0xF3);

    public static readonly Color Surface = Color.FromRgb(0xE9, 0xE9, 0xEA);

    /// <summary>Body text.</summary>
    public static readonly Color Text = Color.FromRgb(0x1D, 0x1F, 0x20);

    /// <summary>
    /// The one accent. Active tool, primary button, selection, focus.
    ///
    /// Interface chrome only. Annotations drawn on a capture use <see cref="AnnotationDefault"/>:
    /// the design system's single-accent rule governs the application's own surfaces, while an
    /// annotation is a mark on someone else's screenshot and has to stand out against it.
    /// </summary>
    public static readonly Color Accent = Color.FromRgb(0x59, 0x80, 0xA6);

    /// <summary>
    /// The default colour of a newly drawn annotation.
    ///
    /// Red rather than the steel accent, deliberately. A screenshot annotation has to read as
    /// deliberate marking against arbitrary underlying pixels, and red is the convention every
    /// reader of a screenshot already knows.
    /// </summary>
    public const string AnnotationDefault = "#E5342A";

    /// <summary>Every hairline border in the system: text at 16% alpha, always one pixel.</summary>
    public static readonly Color Divider = Color.FromArgb(0x29, 0x1D, 0x1F, 0x20);

    // Tonal ramps. Generated in OKLCH on one shared lightness scale, so the same step of any role
    // carries the same visual weight.

    public static readonly Color Neutral100 = Color.FromRgb(0xF5, 0xF5, 0xF8);
    public static readonly Color Neutral200 = Color.FromRgb(0xE7, 0xE7, 0xEA);
    public static readonly Color Neutral300 = Color.FromRgb(0xD4, 0xD4, 0xD7);
    public static readonly Color Neutral400 = Color.FromRgb(0xB7, 0xB7, 0xBA);
    public static readonly Color Neutral500 = Color.FromRgb(0x98, 0x98, 0x9B);
    public static readonly Color Neutral600 = Color.FromRgb(0x7A, 0x7A, 0x7D);
    public static readonly Color Neutral700 = Color.FromRgb(0x5D, 0x5D, 0x60);
    public static readonly Color Neutral800 = Color.FromRgb(0x42, 0x42, 0x44);
    public static readonly Color Neutral900 = Color.FromRgb(0x2B, 0x2B, 0x2D);

    public static readonly Color Accent100 = Color.FromRgb(0xEE, 0xF6, 0xFF);
    public static readonly Color Accent200 = Color.FromRgb(0xD6, 0xEB, 0xFF);
    public static readonly Color Accent300 = Color.FromRgb(0xB5, 0xD9, 0xFD);
    public static readonly Color Accent400 = Color.FromRgb(0x94, 0xBC, 0xE3);
    public static readonly Color Accent500 = Color.FromRgb(0x74, 0x9D, 0xC4);
    public static readonly Color Accent600 = Color.FromRgb(0x59, 0x7E, 0xA3);
    public static readonly Color Accent700 = Color.FromRgb(0x41, 0x61, 0x80);
    public static readonly Color Accent800 = Color.FromRgb(0x2C, 0x45, 0x5D);
    public static readonly Color Accent900 = Color.FromRgb(0x1D, 0x2D, 0x3D);

    // Brushes for the same values. Immutable and shared: interface surfaces repaint constantly, and
    // a brush per repaint is churn for nothing.

    public static readonly IBrush BgBrush = new ImmutableSolidColorBrush(Bg);
    public static readonly IBrush SurfaceBrush = new ImmutableSolidColorBrush(Surface);
    public static readonly IBrush TextBrush = new ImmutableSolidColorBrush(Text);
    public static readonly IBrush AccentBrush = new ImmutableSolidColorBrush(Accent);
    public static readonly IBrush DividerBrush = new ImmutableSolidColorBrush(Divider);

    public static readonly IBrush Neutral100Brush = new ImmutableSolidColorBrush(Neutral100);
    public static readonly IBrush Neutral200Brush = new ImmutableSolidColorBrush(Neutral200);
    public static readonly IBrush Neutral300Brush = new ImmutableSolidColorBrush(Neutral300);
    public static readonly IBrush Neutral400Brush = new ImmutableSolidColorBrush(Neutral400);
    public static readonly IBrush Neutral500Brush = new ImmutableSolidColorBrush(Neutral500);
    public static readonly IBrush Neutral600Brush = new ImmutableSolidColorBrush(Neutral600);
    public static readonly IBrush Neutral700Brush = new ImmutableSolidColorBrush(Neutral700);
    public static readonly IBrush Neutral800Brush = new ImmutableSolidColorBrush(Neutral800);
    public static readonly IBrush Neutral900Brush = new ImmutableSolidColorBrush(Neutral900);

    public static readonly IBrush Accent100Brush = new ImmutableSolidColorBrush(Accent100);
    public static readonly IBrush Accent200Brush = new ImmutableSolidColorBrush(Accent200);
    public static readonly IBrush Accent300Brush = new ImmutableSolidColorBrush(Accent300);
    public static readonly IBrush Accent400Brush = new ImmutableSolidColorBrush(Accent400);
    public static readonly IBrush Accent500Brush = new ImmutableSolidColorBrush(Accent500);
    public static readonly IBrush Accent600Brush = new ImmutableSolidColorBrush(Accent600);
    public static readonly IBrush Accent700Brush = new ImmutableSolidColorBrush(Accent700);
    public static readonly IBrush Accent800Brush = new ImmutableSolidColorBrush(Accent800);
    public static readonly IBrush Accent900Brush = new ImmutableSolidColorBrush(Accent900);

    /// <summary>The hairline every framed object is drawn with. One pixel, always.</summary>
    public static readonly IPen DividerPen = new ImmutablePen(new ImmutableSolidColorBrush(Divider), 1);

    /// <summary>
    /// Spacing, at the system's 0.85 density. Deliberately not round numbers: the scale is what
    /// gives the layout its rhythm, and rounding a 13.6 to 14 in one place breaks the alignment
    /// with everything else on the same step.
    /// </summary>
    public static class Space
    {
        public const double S1 = 3.4;
        public const double S2 = 6.8;
        public const double S3 = 10.2;
        public const double S4 = 13.6;
        public const double S6 = 20.4;
        public const double S8 = 27.2;
    }

    /// <summary>
    /// Radius is zero on everything.
    ///
    /// Not an oversight to be tidied up later: the system's whole grammar is wireframe objects,
    /// square-cornered and hairline-bordered. The only curves in the interface are inside the icons.
    /// </summary>
    public static readonly CornerRadius Radius = new(0);

    /// <summary>
    /// Typography.
    ///
    /// Barlow Condensed for headings and chrome labels, Barlow for body and interface text. Both
    /// are shipped with the application rather than assumed present: a condensed heading face
    /// silently falling back to the system sans loses the design's voice entirely.
    /// </summary>
    public static class Fonts
    {
        const string Folder = "avares://SnapShotKit.Ui/Assets/Fonts";

        static FontFamily? heading;
        static FontFamily? body;

        /// <summary>Barlow Condensed 600, for uppercase headings and chrome labels.</summary>
        public static FontFamily Heading => heading ??= Resolve("BarlowCondensed-SemiBold.ttf", "Barlow Condensed");

        /// <summary>Barlow 400/500, for menus, buttons, fields and body text.</summary>
        public static FontFamily Body => body ??= Resolve("Barlow-Regular.ttf", "Barlow");

        public const FontWeight HeadingWeight = FontWeight.SemiBold;

        /// <summary>
        /// Prefers the copy shipped inside the application, and falls back to a system copy or the
        /// default sans when it is absent.
        ///
        /// Resolved rather than assumed so that a build without the font files still runs: an
        /// unresolvable embedded family gives every label the default face without saying why,
        /// which is a confusing way to discover a missing asset.
        /// </summary>
        static FontFamily Resolve(string file, string family)
        {
            try
            {
                if (Avalonia.Platform.AssetLoader.Exists(new Uri($"{Folder}/{file}")))
                {
                    return new FontFamily($"{Folder}#{family}");
                }
            }
            catch (Exception)
            {
                // Asked before Avalonia was initialised, or the asset assembly is not loaded.
                // Either way the fallback below is the answer.
            }

            return new FontFamily($"{family}, sans-serif");
        }
    }

    /// <summary>
    /// Letter spacing for the system's uppercase labels, which are always tracked out.
    ///
    /// Avalonia takes letter spacing in device pixels rather than em, so these are helpers rather
    /// than constants: the same 0.16em is a different number of pixels at 11px and at 30px.
    /// </summary>
    public static double Tracking(double fontSize, double em) => fontSize * em;

    // Elevation, tuned to the light ground.

    public static readonly BoxShadows ShadowSm = new(new BoxShadow
    {
        OffsetY = 1, Blur = 2, Color = Color.FromArgb(0x24, 0x2B, 0x2B, 0x2D)
    });

    public static readonly BoxShadows ShadowMd = new(new BoxShadow
    {
        OffsetY = 3, Blur = 10, Color = Color.FromArgb(0x29, 0x2B, 0x2B, 0x2D)
    });

    public static readonly BoxShadows ShadowLg = new(new BoxShadow
    {
        OffsetY = 12, Blur = 32, Color = Color.FromArgb(0x38, 0x2B, 0x2B, 0x2D)
    });

    /// <summary>Interactive surfaces drop to this opacity when disabled.</summary>
    public const double DisabledOpacity = 0.45;
}
