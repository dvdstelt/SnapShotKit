using System.Diagnostics;

namespace SnapShotKit.Contracts;

/// <summary>
/// Puts an image on the Wayland clipboard.
///
/// Through wl-copy rather than the toolkit, because of how Wayland's clipboard works: the offer
/// belongs to the process that made it, and it dies with that process. The overlay exits the moment
/// it hands back a region, and GNOME ships no clipboard manager to take the offer over, so a copy
/// made by the toolkit would vanish before anyone could paste it. wl-copy forks a holder that stays
/// alive for exactly this reason.
/// </summary>
/// <remarks>
/// Named for the platform rather than simply Clipboard, both because it is specific to Wayland and
/// because every Avalonia window already inherits a Clipboard property that a plainer name would
/// shadow.
/// </remarks>
public static class WaylandClipboard
{
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
