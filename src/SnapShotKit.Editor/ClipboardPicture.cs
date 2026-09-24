using SnapShotKit.Contracts;

namespace SnapShotKit.Editor;

/// <summary>
/// A picture on the clipboard, as PNG bytes ready to go into a snapshot.
///
/// Two things are worth pasting. The picture itself, which is what a screenshot tool, a browser's
/// "Copy image" or another SnapShotKit window puts there; and a picture file, which is what copying
/// one in the file manager puts there, as a path rather than as pixels. Both come out the same way,
/// so pasting a file copied in Files does what anyone would expect rather than nothing.
/// </summary>
public static class ClipboardPicture
{
    /// <summary>Offered as a file rather than as pixels. The first is what the file manager writes, the second what everything else does.</summary>
    static readonly string[] FileTypes = ["x-special/gnome-copied-files", "text/uri-list"];

    /// <summary>The picture, or null and the reason there is none to paste.</summary>
    public static async Task<(byte[]? Png, string Problem)> ReadAsync()
    {
        var offered = await WaylandClipboard.TypesAsync();

        if (offered.Count == 0)
        {
            return (null, "Nothing to paste. The clipboard is empty, or wl-paste is not installed.");
        }

        // PNG first, since it goes in exactly as it came. Then any other picture the importer reads,
        // in the order the offering application preferred them.
        var pictures = offered
            .Where(type => ImageImport.Formats.Any(format => format.MediaType == type))
            .OrderBy(type => type == "image/png" ? 0 : 1);

        foreach (var type in pictures)
        {
            if (await WaylandClipboard.ReadAsync(type) is { Length: > 0 } bytes && Convert(bytes) is { } png)
            {
                return (png, string.Empty);
            }
        }

        foreach (var type in FileTypes.Where(offered.Contains))
        {
            if (await WaylandClipboard.ReadAsync(type) is { } listed && FromFiles(listed) is { } png)
            {
                return (png, string.Empty);
            }
        }

        return (null, "Nothing to paste. The clipboard holds no picture.");
    }

    /// <summary>Whatever picture the bytes are, as PNG, or null when they are not a picture after all.</summary>
    static byte[]? Convert(byte[] bytes)
    {
        try
        {
            return ImageImport.ToPng(bytes).Png;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The first picture among a list of copied files.
    ///
    /// The GNOME form leads with a line saying whether the files were copied or cut, which is not a
    /// path and is skipped along with anything else that is not one. Only the first picture is
    /// taken: pasting is one picture at a time, and a folder of them is what importing is for.
    /// </summary>
    static byte[]? FromFiles(byte[] listed)
    {
        var lines = System.Text.Encoding.UTF8.GetString(listed)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || !uri.IsFile)
            {
                continue;
            }

            var path = uri.LocalPath;

            if (!ImageImport.Handles(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                return Convert(File.ReadAllBytes(path));
            }
            catch (Exception)
            {
                // Unreadable, or gone since it was copied. The next one might not be.
            }
        }

        return null;
    }
}
