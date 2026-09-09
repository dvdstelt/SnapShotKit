namespace SnapShotKit.Editor;

/// <summary>What an export comes out as.</summary>
public enum ExportFormat
{
    Png,
    Jpeg,
    Webp
}

/// <summary>Which folder the save dialog should open in.</summary>
public enum ExportFolder
{
    /// <summary>`~/Pictures/snapshotkit`, the folder the user browses.</summary>
    Exports,

    /// <summary>The folder holding the picture this snapshot was made out of. Only exists for an imported one.</summary>
    Origin
}

/// <summary>
/// How the next export should be written, and where the dialog should start looking.
///
/// One object for every format rather than one per format, because it is remembered between
/// sessions and a person who exports WebP at a particular quality wants that quality again next
/// week, including after a detour through JPEG. Settings for a format not currently chosen are
/// simply not read.
/// </summary>
public sealed class ExportSettings
{
    public ExportFormat Format { get; set; } = ExportFormat.Png;

    /// <summary>
    /// Whether transparency survives, for the formats that can carry it.
    ///
    /// Off means the picture is rendered onto white instead, and written without an alpha channel
    /// at all, which is both smaller and what a screenshot pasted into a document sits on. JPEG
    /// ignores this and always flattens, because it has no alpha to keep.
    /// </summary>
    public bool KeepTransparency { get; set; } = true;

    public int JpegQuality { get; set; } = 90;

    /// <summary>
    /// Lossless WebP, which is the default and the reason to reach for the format here.
    ///
    /// A screenshot is text and hairlines, which is exactly what lossy encoding smears. Lossless
    /// WebP is a PNG at roughly half the size; lossy is a smaller JPEG, and anyone who wants that
    /// trade has JPEG already. It stays on offer because a screenshot of a photograph is still a
    /// photograph.
    /// </summary>
    public bool WebpLossless { get; set; } = true;

    /// <summary>
    /// One number doing two jobs, because the encoder does the same.
    ///
    /// Lossy reads it as picture quality. Lossless reads it as how hard to work at making the file
    /// small, where every setting gives back the same pixels and only the size and the time differ.
    /// Kept as one value so that switching between the two does not silently move the other one.
    /// </summary>
    public int WebpQuality { get; set; } = 75;

    public ExportFolder Folder { get; set; } = ExportFolder.Exports;

    /// <summary>The extension this asks for, without the dot.</summary>
    public string Extension => Format switch
    {
        ExportFormat.Jpeg => "jpg",
        ExportFormat.Webp => "webp",
        _ => "png"
    };

    /// <summary>Whether the format can carry transparency at all, which is what decides if it is worth asking about.</summary>
    public bool CanKeepTransparency => Format is ExportFormat.Png or ExportFormat.Webp;

    /// <summary>Whether what comes out actually has an alpha channel, which is the question the renderer asks.</summary>
    public bool Transparent => CanKeepTransparency && KeepTransparency;

    public ExportSettings Copy() => new()
    {
        Format = Format,
        KeepTransparency = KeepTransparency,
        JpegQuality = JpegQuality,
        WebpLossless = WebpLossless,
        WebpQuality = WebpQuality,
        Folder = Folder
    };

    /// <summary>
    /// What a bare `--export out.webp` means: the extension picks the format, the rest are defaults.
    ///
    /// The command line has nowhere to put a dialog, and a flag per setting would be a lot of
    /// surface for something whose whole job is to render a file in a script.
    /// </summary>
    public static ExportSettings ForExtension(string path)
    {
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ExportFormat.Png,
            ".jpg" or ".jpeg" => ExportFormat.Jpeg,
            ".webp" => ExportFormat.Webp,
            var other => throw new NotSupportedException(
                $"{other} is not a format SnapShotKit writes. Use .png, .jpg or .webp.")
        };

        return new ExportSettings { Format = format };
    }
}
