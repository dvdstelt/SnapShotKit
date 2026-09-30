using System.Text.Json;
using SnapShotKit.Contracts;

namespace SnapShotKit.Editor;

/// <summary>
/// An object copied off the canvas, on its way to being pasted back.
///
/// On the Wayland clipboard rather than held in the window, because every editor window is a
/// process of its own: an arrow copied in one has to paste into another. Under a media type of its
/// own, so nothing else mistakes it for something it can use, and so a picture copied anywhere
/// afterwards replaces it the way any copy replaces the one before.
///
/// A picture goes with its pixels. What a layer points at is an entry in the snapshot it came from,
/// and the snapshot it is pasted into may never have had it.
/// </summary>
static class ObjectClipboard
{
    /// <summary>
    /// JSON underneath, but not called that. wl-copy offers anything it takes for text as plain text
    /// as well, and a "+json" type counts, which would paste the object's innards into whatever
    /// text field somebody pressed Ctrl+V in next.
    /// </summary>
    public const string Type = "application/x-snapshotkit-object";

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public sealed class Copied
    {
        /// <summary>Tells one copy from the next, so pasting the same one again knows to land further along.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The snapshot it was copied out of, so a paste back into it can stand clear of the original.</summary>
        public string From { get; set; } = string.Empty;

        public Annotation Annotation { get; set; } = null!;

        /// <summary>The picture a picture layer shows, or null for anything that is not one.</summary>
        public byte[]? Picture { get; set; }
    }

    public static bool TryCopy(Copied copied, out string error) =>
        WaylandClipboard.TryCopy(JsonSerializer.SerializeToUtf8Bytes(copied, Json), Type, out error);

    /// <summary>The object on the clipboard, or null when what is there is anything else.</summary>
    public static async Task<Copied?> ReadAsync()
    {
        if (!(await WaylandClipboard.TypesAsync()).Contains(Type))
        {
            return null;
        }

        if (await WaylandClipboard.ReadAsync(Type) is not { } bytes)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Copied>(bytes, Json) is { Annotation: not null } copied ? copied : null;
        }
        catch (JsonException)
        {
            // From a later build that knows kinds of object this one does not. There is nothing to
            // paste that would mean the same thing, so nothing is pasted.
            return null;
        }
    }
}
