using Avalonia;

namespace SnapShotKit.Editor;

/// <summary>
/// Where the picture stands on the page and how large it is, in millimetres.
///
/// Millimetres because the page is a physical thing and the questions asked of it are physical:
/// how wide is this going to be, is it in the middle. Pixels only come into it as the picture's
/// proportions, and as the answer to how sharp it will be at the size chosen.
///
/// The picture keeps its proportions always. Nobody printing a screenshot wants it stretched, and
/// a second number to keep in step with the first by hand would be a way to get it wrong and
/// nothing else, so there is one size, the width, and the height follows from it.
///
/// No window and no drawing in here. The preview drags it about and the fields type into it, and
/// both come to the same place because neither of them holds the answer.
/// </summary>
public sealed class PrintLayout
{
    /// <summary>What a pixel measures when nothing has said otherwise: a screen's 96 to the inch.</summary>
    const double MillimetresPerPixel = 25.4 / 96;

    /// <summary>The least a picture may be, so it can always be found again and taken hold of.</summary>
    public const double MinimumWidth = 10;

    /// <summary>How much of the picture has to stay on the sheet, so it cannot be dragged off and lost.</summary>
    const double KeptOnPage = 10;

    readonly double pixelWidth;
    readonly double pixelHeight;

    public PrintLayout(PixelSize picture, PrintSettings settings)
    {
        pixelWidth = Math.Max(picture.Width, 1);
        pixelHeight = Math.Max(picture.Height, 1);

        (PageWidth, PageHeight) = settings.Page();
        Margin = settings.Margin;

        Reset();
    }

    public double PageWidth { get; private set; }

    public double PageHeight { get; private set; }

    public double Margin { get; private set; }

    public double X { get; private set; }

    public double Y { get; private set; }

    public double Width { get; private set; }

    public double Height => Width * pixelHeight / pixelWidth;

    public Rect Picture => new(X, Y, Width, Height);

    /// <summary>The page inside its margins, which is what the fits are measured within.</summary>
    public Rect Printable
    {
        get
        {
            // Margins wide enough to meet in the middle leave nothing to fit into, and a fit into
            // nothing is a picture of no size.
            var margin = Math.Min(Margin, Math.Min(PageWidth, PageHeight) / 2 - MinimumWidth / 2);
            margin = Math.Max(margin, 0);

            return new Rect(margin, margin, PageWidth - 2 * margin, PageHeight - 2 * margin);
        }
    }

    /// <summary>How many of the picture's pixels go into an inch of paper at this size, which is how sharp it prints.</summary>
    public double PixelsPerInch => pixelWidth / (Width / 25.4);

    /// <summary>
    /// Where a picture starts out: at its own size if the page has room for it, and fitted to the
    /// page if not, across the middle at the top.
    ///
    /// Its own size rather than as large as will go, because a small screenshot blown up to fill a
    /// sheet is soft and enormous, and the top rather than the middle because that is where a
    /// picture on a page goes when nothing has been said about it.
    /// </summary>
    public void Reset()
    {
        var printable = Printable;
        var natural = pixelWidth * MillimetresPerPixel;

        Width = natural <= printable.Width && natural * pixelHeight / pixelWidth <= printable.Height
            ? natural
            : Fitted(printable);

        X = printable.X + (printable.Width - Width) / 2;
        Y = printable.Y;

        filling = Filling.Nothing;
    }

    /// <summary>What the picture was last asked to fill, if it has not been moved or sized by hand since.</summary>
    enum Filling
    {
        Nothing,
        Width,
        Page
    }

    Filling filling;

    /// <summary>
    /// A different sheet, the same one turned round, or other margins on it.
    ///
    /// A picture that was asked to fill the width goes on filling the width, since that is what was
    /// asked for and the width has changed. One that was placed by hand is placed afresh when the
    /// sheet itself changes, because where it stood was a place on the old sheet, and is left alone
    /// when only the margins do.
    /// </summary>
    public void Repage(PrintSettings settings)
    {
        var sameSheet = (PageWidth, PageHeight) == settings.Page();

        (PageWidth, PageHeight) = settings.Page();
        Margin = settings.Margin;

        switch (filling)
        {
            case Filling.Width:
                FitWidth();
                break;

            case Filling.Page:
                FitPage();
                break;

            default:
                if (sameSheet)
                {
                    Keep();
                }
                else
                {
                    Reset();
                }

                break;
        }
    }

    /// <summary>From margin to margin, however tall that comes out, and kept where it was down the page.</summary>
    public void FitWidth()
    {
        var printable = Printable;

        Width = printable.Width;
        X = printable.X;
        Y = Math.Clamp(Y, Math.Min(printable.Y, printable.Bottom - Height), Math.Max(printable.Y, printable.Bottom - Height));
        Keep();

        filling = Filling.Width;
    }

    /// <summary>As large as goes inside the margins whole, in the middle of them.</summary>
    public void FitPage()
    {
        Width = Fitted(Printable);
        X = (PageWidth - Width) / 2;
        Y = (PageHeight - Height) / 2;

        filling = Filling.Page;
    }

    /// <summary>One pixel to a ninety-sixth of an inch, which is the size it was on the screen it came off.</summary>
    public void ActualSize() => Resize(pixelWidth * MillimetresPerPixel);

    public void CentreAcross() => X = (PageWidth - Width) / 2;

    /// <summary>Filling the width says nothing about how far down the page, so this leaves that standing.</summary>
    public void CentreDown() => Y = (PageHeight - Height) / 2;

    /// <summary>A new width about the picture's own middle, which is what typing a size means when no corner was taken hold of.</summary>
    public void Resize(double width)
    {
        filling = Filling.Nothing;

        var centre = Picture.Center;

        Width = Limited(width);
        X = centre.X - Width / 2;
        Y = centre.Y - Height / 2;
        Keep();
    }

    public void ResizeByHeight(double height) => Resize(height * pixelWidth / pixelHeight);

    public void MoveTo(double x, double y)
    {
        filling = Filling.Nothing;

        X = x;
        Y = y;
        Keep();
    }

    /// <summary>
    /// A new width with one corner held, which is what dragging the corner opposite means.
    /// </summary>
    /// <param name="held">The corner that stays where it is, in page millimetres.</param>
    /// <param name="leftwards">Whether the picture lies to the left of the held corner.</param>
    /// <param name="upwards">Whether it lies above it.</param>
    public void ResizeFrom(Point held, double width, bool leftwards, bool upwards)
    {
        filling = Filling.Nothing;

        Width = Limited(width);
        X = leftwards ? held.X - Width : held.X;
        Y = upwards ? held.Y - Height : held.Y;
        Keep();
    }

    double Fitted(Rect within) => Math.Max(Math.Min(within.Width, within.Height * pixelWidth / pixelHeight), 1);

    /// <summary>No smaller than can be taken hold of, and no larger than twice the sheet, past which it is only being lost.</summary>
    double Limited(double width) => Math.Clamp(width, MinimumWidth, Math.Max(PageWidth, PageHeight) * 2);

    /// <summary>Holds some of the picture on the sheet. Over the margins and off the edge is allowed; gone altogether is not.</summary>
    void Keep()
    {
        X = Math.Clamp(X, KeptOnPage - Width, PageWidth - KeptOnPage);
        Y = Math.Clamp(Y, KeptOnPage - Height, PageHeight - KeptOnPage);
    }
}
