using System.Diagnostics;
using SkiaSharp;

namespace SnapShotKit.Editor;

/// <summary>
/// Getting a page to a printer.
///
/// Through CUPS and its own command line, `lpstat` to ask what printers there are and `lp` to hand
/// one a job, the same way the clipboard is reached through wl-paste. The alternative is the print
/// portal, which brings the desktop's own print dialog with it, and that dialog is the problem
/// rather than the help: it asks for the copies, the paper and the orientation over again, after
/// this application has just asked, and has no idea where on the page the picture was put. CUPS
/// takes a finished page and prints it, which is all that is wanted of it.
///
/// The page goes over as a PDF, one page of exactly the paper's size with the picture placed on it.
/// A PDF because it is what CUPS prints natively, and because it carries the picture at every pixel
/// it has rather than at whatever a page-sized bitmap would have resampled it to: how sharp it
/// comes out is then up to the printer, which is the one that knows.
/// </summary>
public static class Printing
{
    const double PointsPerMillimetre = 72 / 25.4;

    /// <summary>A printer CUPS knows about.</summary>
    /// <param name="Name">What CUPS calls it, which is what a job is addressed to.</param>
    /// <param name="IsDefault">Whether it is the one the system prints to when nobody says.</param>
    public sealed record Printer(string Name, bool IsDefault);

    /// <summary>
    /// The printers there are, the system's default first. Empty when there are none, and empty
    /// when CUPS is not installed or not answering, which for somebody wanting to print comes to
    /// the same thing.
    /// </summary>
    public static async Task<IReadOnlyList<Printer>> PrintersAsync()
    {
        var names = await RunAsync("lpstat", ["-e"]);

        if (names.ExitCode != 0)
        {
            return [];
        }

        // "system default destination: NAME", or a sentence saying there is none.
        var standard = await RunAsync("lpstat", ["-d"]);
        var marker = standard.Output.LastIndexOf(": ", StringComparison.Ordinal);
        var chosen = standard.ExitCode == 0 && marker >= 0 ? standard.Output[(marker + 2)..].Trim() : null;

        return
        [
            .. names.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => new Printer(name, name == chosen))
                .OrderByDescending(printer => printer.IsDefault)
        ];
    }

    /// <summary>
    /// The page as a PDF: one sheet of the paper chosen, turned the way chosen, with the picture
    /// where the layout says.
    /// </summary>
    /// <param name="png">The picture, already flattened onto white. Paper has no transparency to show through to.</param>
    public static byte[] ToPdf(byte[] png, PrintLayout layout, string title)
    {
        using var image = SKImage.FromEncodedData(png) ?? throw new InvalidDataException("The picture could not be read back for printing.");
        using var buffer = new MemoryStream();

        var metadata = new SKDocumentPdfMetadata
        {
            Title = title,
            Creator = "SnapShotKit",

            // The picture goes in as it is. The default re-encodes it as JPEG, which is the one
            // thing a screenshot full of text and hairlines should never have done to it.
            EncodingQuality = 101,
            RasterDpi = 300
        };

        using (var document = SKDocument.CreatePdf(buffer, metadata))
        {
            var canvas = document.BeginPage(
                (float)(layout.PageWidth * PointsPerMillimetre),
                (float)(layout.PageHeight * PointsPerMillimetre));

            var picture = layout.Picture;

            canvas.DrawImage(image, new SKRect(
                (float)(picture.X * PointsPerMillimetre),
                (float)(picture.Y * PointsPerMillimetre),
                (float)(picture.Right * PointsPerMillimetre),
                (float)(picture.Bottom * PointsPerMillimetre)));

            document.EndPage();
            document.Close();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Hands the page to a printer, and returns what CUPS said about it.
    ///
    /// Told not to scale, because the page is already exactly the paper and the picture is already
    /// exactly where it was put, and a driver that helpfully shrinks the page to fit inside its own
    /// margins undoes both. A landscape page is not announced as one: the PDF is wider than it is
    /// tall, CUPS turns a page like that to suit the sheet by itself, and saying so as well turns
    /// it twice.
    /// </summary>
    /// <param name="printer">The destination, or null for the system's default.</param>
    /// <exception cref="InvalidOperationException">CUPS refused the job, or is not there to take it.</exception>
    public static async Task<string> SendAsync(byte[] pdf, string? printer, int copies, PrintSettings settings, string title)
    {
        // A real file, because lp reading from a pipe cannot tell what it has been given until it
        // has all of it, and a job named "(stdin)" in the queue tells nobody what is printing.
        var file = Path.Combine(Path.GetTempPath(), $"snapshotkit-print-{Guid.NewGuid():N}.pdf");

        try
        {
            await File.WriteAllBytesAsync(file, pdf);

            List<string> arguments = [];

            if (printer is not null)
            {
                arguments.AddRange(["-d", printer]);
            }

            arguments.AddRange(
            [
                "-n", Math.Clamp(copies, 1, 999).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-t", title,
                "-o", $"media={settings.Media}",
                "-o", "print-scaling=none",
                "--", file
            ]);

            var sent = await RunAsync("lp", arguments);

            return sent.ExitCode == 0
                ? sent.Output.Trim()
                : throw new InvalidOperationException(sent.Error.Trim() is { Length: > 0 } said ? said : "CUPS did not take the job.");
        }
        finally
        {
            // lp has copied it into the spool by the time it returns, so nothing is waiting on it.
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Runs one of the CUPS commands and waits for it. A command that is not installed comes back
    /// as a failure like any other, since to the caller a missing `lp` and a refusing one are both
    /// "this cannot print".
    /// </summary>
    static async Task<(int ExitCode, string Output, string Error)> RunAsync(string command, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        // Asked for in English, because one of the answers is read rather than shown.
        startInfo.Environment["LC_ALL"] = "C";

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return (-1, string.Empty, $"{command} could not be started.");
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();
            return (process.ExitCode, await output, await error);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty, $"{command} is not installed. Printing goes through CUPS.");
        }
    }
}
