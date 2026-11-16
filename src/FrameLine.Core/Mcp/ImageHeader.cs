using System.Buffers.Binary;

namespace FrameLine.Core.Mcp;

/// Pixel size of a PNG, JPEG, GIF or BMP read from its header, without decoding (Core has no
/// imaging library). Enough for take_screenshot, which only writes PNG or JPEG.
public static class ImageHeader
{
    public static (int Width, int Height)? TryGetPixelSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[32];
            int n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            var h = head.AsSpan(0, n);
            if (n >= 24 && h[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
                return Valid(BinaryPrimitives.ReadInt32BigEndian(h[16..]), BinaryPrimitives.ReadInt32BigEndian(h[20..]));
            if (n >= 10 && h[0] == (byte)'G' && h[1] == (byte)'I' && h[2] == (byte)'F')
                return Valid(BinaryPrimitives.ReadUInt16LittleEndian(h[6..]), BinaryPrimitives.ReadUInt16LittleEndian(h[8..]));
            if (n >= 26 && h[0] == (byte)'B' && h[1] == (byte)'M')
                return Valid(BinaryPrimitives.ReadInt32LittleEndian(h[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(h[22..])));
            if (n >= 2 && h[0] == 0xFF && h[1] == 0xD8)
            {
                stream.Position = 2;
                return Jpeg(stream);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static (int, int)? Valid(int w, int h) => w > 0 && h > 0 ? (w, h) : null;

    private static (int, int)? Jpeg(Stream s)
    {
        var buf = new byte[7];
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) return null;
            if (b != 0xFF) continue;
            int marker;
            do { marker = s.ReadByte(); } while (marker == 0xFF);
            if (marker < 0) return null;
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;   // no length
            if (marker is 0xD9 or 0xDA) return null;                                  // EOI / SOS before SOF
            if (s.ReadAtLeast(buf.AsSpan(0, 2), 2, throwOnEndOfStream: false) < 2) return null;
            int length = BinaryPrimitives.ReadUInt16BigEndian(buf);
            if (length < 2) return null;
            bool isSof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isSof)
            {
                if (s.ReadAtLeast(buf.AsSpan(0, 5), 5, throwOnEndOfStream: false) < 5) return null;
                return Valid(BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(3)), BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(1)));
            }
            s.Seek(length - 2, SeekOrigin.Current);
        }
    }
}
