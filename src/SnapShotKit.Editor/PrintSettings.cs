using System.Globalization;

namespace SnapShotKit.Editor;

/// <summary>The paper sizes offered. Few on purpose: the ones an office printer takes, not everything a driver lists.</summary>
public enum Paper
{
    A3,
    A4,
    A5,
    Letter,
    Legal
}

/// <summary>
/// How the last print was set up, which is how the next one is offered.
///
/// The printer, the paper, which way up and the margins, because those belong to the desk the
/// printer stands on and are the same tomorrow. Not where the picture stood on the page or how
/// large it was, which belong to the picture, and not the number of copies: somebody who printed
/// twenty of something last week does not want twenty of the next thing by default.
/// </summary>
public sealed class PrintSettings
{
    /// <summary>The CUPS destination, or null for whichever the system calls its default.</summary>
    public string? Printer { get; set; }

    public Paper Paper { get; set; } = DefaultPaper();

    public bool Landscape { get; set; }

    /// <summary>
    /// The margin kept clear on every side, in millimetres.
    ///
    /// A guide rather than a wall. It is what "full width" is measured within, since almost no
    /// printer can put ink to the edge of the sheet and a picture fitted to the bare page would
    /// lose its edges to that. The picture can still be dragged over it.
    /// </summary>
    public double Margin { get; set; } = 10;

    public PrintSettings Copy() => new() { Printer = Printer, Paper = Paper, Landscape = Landscape, Margin = Margin };

    /// <summary>The page in millimetres, already turned the way it will be printed.</summary>
    public (double Width, double Height) Page()
    {
        var (width, height) = SizeOf(Paper);
        return Landscape ? (height, width) : (width, height);
    }

    /// <summary>What CUPS calls this paper.</summary>
    public string Media => Paper.ToString();

    static (double Width, double Height) SizeOf(Paper paper) => paper switch
    {
        Paper.A3 => (297, 420),
        Paper.A5 => (148, 210),
        Paper.Letter => (215.9, 279.4),
        Paper.Legal => (215.9, 355.6),
        _ => (210, 297)
    };

    /// <summary>
    /// Letter where Letter is what is in the tray, and A4 everywhere else.
    ///
    /// From the region rather than from the printer's own default, which is whatever the driver
    /// shipped with and is Letter on a great many printers that have never seen a sheet of it.
    /// </summary>
    static Paper DefaultPaper()
    {
        try
        {
            return RegionInfo.CurrentRegion.TwoLetterISORegionName is "US" or "CA" or "MX" or "PH" or "CL" or "CO"
                ? Paper.Letter
                : Paper.A4;
        }
        catch (ArgumentException)
        {
            return Paper.A4;
        }
    }
}
