using System.Diagnostics;

namespace SnapShotKit.Contracts;

/// <summary>
/// Puts an image on the Wayland clipboard, and reads what is on it.
///
/// Through wl-copy rather than the toolkit, because of how Wayland's clipboard works: the offer
/// belongs to the process that made it, and it dies with that process. The overlay exits the moment
/// it hands back a region, and GNOME ships no clipboard manager to take the offer over, so a copy
/// made by the toolkit would vanish before anyone could paste it. wl-copy forks a holder that stays
/// alive for exactly this reason.
///
/// Reading goes through wl-paste to match, and because what it hands back is the bytes exactly as
/// they were offered: a PNG copied out of a browser arrives as that PNG, rather than decoded and
/// encoded again on the way through a toolkit bitmap.
/// </summary>
/// <remarks>
/// Named for the platform rather than simply Clipboard, both because it is specific to Wayland and
/// because every Avalonia window already inherits a Clipboard property that a plainer name would
/// shadow.
/// </remarks>
public static class WaylandClipboard
{
    /// <summary>
    /// The media types on offer, best first as the offering application listed them, or none.
    ///
    /// Asynchronous and bounded, since a clipboard is served by whichever application copied to it,
    /// and one that has stopped answering must not take the editor's window down with it.
    /// </summary>
    public static async Task<IReadOnlyList<string>> TypesAsync()
    {
        var listed = await PasteAsync("--list-types");

        return listed is null
            ? []
            : [.. System.Text.Encoding.UTF8.GetString(listed).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>What is on offer as <paramref name="type"/>, or null when it is not, or cannot be had.</summary>
    public static Task<byte[]?> ReadAsync(string type) => PasteAsync("--no-newline", "--type", type);

    /// <summary>
    /// Runs wl-paste and hands back everything it wrote.
    ///
    /// Null for every way of getting nothing: an empty clipboard, which wl-paste reports by failing,
    /// a type not on offer, an application that never answers, and wl-paste not being installed.
    /// Whoever asked only wants to know whether there is something to paste.
    /// </summary>
    static async Task<byte[]?> PasteAsync(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("wl-paste")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return null;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var buffer = new MemoryStream();

            // Both streams drained together. A process that fills the one nobody is reading blocks
            // on it, and then never finishes writing the other.
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);

            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(buffer, timeout.Token);
                await errors;
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 ? buffer.ToArray() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Copies PNG bytes, returning false and a reason rather than throwing.</summary>
    public static bool TryCopyPng(byte[] png, out string error)
    {
        try
        {
            var startInfo = new ProcessStartInfo("wl-copy")
            {
                RedirectStandardInput = true,
                // The forked holder inherits whatever it is not given, and it lives until the
                // clipboard is replaced. Left inheriting, it holds the caller's stdout open for
                // hours, which hangs anything reading that output and waiting for it to close.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            startInfo.ArgumentList.Add("--type");
            startInfo.ArgumentList.Add("image/png");

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                error = "wl-copy could not be started.";
                return false;
            }

            using (var input = process.StandardInput.BaseStream)
            {
                input.Write(png);
            }

            // wl-copy forks its holder and the foreground exits immediately, so this returns as soon
            // as the clipboard has been handed over rather than waiting on the holder.
            process.WaitForExit(TimeSpan.FromSeconds(5));

            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception is System.ComponentModel.Win32Exception
                ? "wl-copy is not installed. Install wl-clipboard to copy images."
                : exception.Message;

            return false;
        }
    }
}
