using System.Buffers.Binary;

namespace SnapShotKit.Spike.PortalCapture;

/// <summary>
/// Reads the dimensions out of a PNG header. The spike only needs to know how much of the desktop
/// the portal handed back, so decoding the pixels would be wasted work.
/// </summary>
internal static class Png
{
    static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static (int Width, int Height)? ReadDimensions(string path)
    {
        Span<byte> header = stackalloc byte[24];

        using var stream = File.OpenRead(path);
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
        {
            return null;
        }

        if (!header[..8].SequenceEqual(Signature) || !header[12..16].SequenceEqual("IHDR"u8))
        {
            return null;
        }

        return (BinaryPrimitives.ReadInt32BigEndian(header[16..20]), BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }
}
