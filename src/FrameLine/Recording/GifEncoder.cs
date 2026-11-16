using System;
using System.Collections.Generic;
using System.IO;

namespace FrameLine.Recording;

/// Minimal animated GIF89a writer: per-frame delays, NETSCAPE2.0 infinite loop, a local palette per
/// frame, and inter-frame optimisation (only the changed rectangle is stored, unchanged pixels inside
/// it are transparent). WPF's GifBitmapEncoder writes neither delays nor the loop block.
internal sealed class GifEncoder
{
    private const int Tolerance = 6;   // per channel; absorbs thumbnail JPEG noise on static content

    private readonly Stream output;
    private readonly int width, height;
    private readonly byte[] reference;          // what the viewer currently shows (BGRA)
    private readonly ColorQuantizer quantizer = new();
    private bool hasFrame;
    private PendingFrame? pending;

    private sealed class PendingFrame
    {
        public int X, Y, W, H, Delay, PaletteBits, Transparent = -1;
        public byte[] Palette = Array.Empty<byte>();
        public byte[] Indices = Array.Empty<byte>();
    }

    public GifEncoder(Stream output, int width, int height)
    {
        if (width < 1 || height < 1 || width > 65535 || height > 65535) throw new ArgumentOutOfRangeException(nameof(width));
        this.output = output;
        this.width = width;
        this.height = height;
        reference = new byte[width * height * 4];
        WriteHeader();
    }

    /// Adds a BGRA frame (width x height) shown for delayCs hundredths of a second.
    public void AddFrame(byte[] bgra, int delayCs)
    {
        if (bgra.Length < width * height * 4) throw new ArgumentException("frame too small", nameof(bgra));
        delayCs = Math.Clamp(delayCs, 2, 65535);

        int x0, y0, x1, y1;
        if (!hasFrame) { x0 = 0; y0 = 0; x1 = width - 1; y1 = height - 1; }
        else if (!ChangedBounds(bgra, out x0, out y0, out x1, out y1))
        {
            // Identical frame: lengthen the previous one instead of storing nothing.
            if (pending != null) pending.Delay = Math.Min(65535, pending.Delay + delayCs);
            return;
        }

        Flush();
        pending = Encode(bgra, x0, y0, x1 - x0 + 1, y1 - y0 + 1, useTransparency: hasFrame, delayCs);
        hasFrame = true;
    }

    public void Finish()
    {
        Flush();
        output.WriteByte(0x3B);
        output.Flush();
    }

    private bool ChangedBounds(byte[] px, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = width; y0 = height; x1 = -1; y1 = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                if (!Differs(px, i)) continue;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                y1 = y;
            }
        }
        return x1 >= 0;
    }

    private bool Differs(byte[] px, int i) =>
        Math.Abs(px[i] - reference[i]) > Tolerance ||
        Math.Abs(px[i + 1] - reference[i + 1]) > Tolerance ||
        Math.Abs(px[i + 2] - reference[i + 2]) > Tolerance;

    private PendingFrame Encode(byte[] px, int fx, int fy, int fw, int fh, bool useTransparency, int delay)
    {
        quantizer.Reset();
        var changed = new bool[fw * fh];
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
            {
                int i = ((fy + y) * width + fx + x) * 4;
                bool c = !useTransparency || Differs(px, i);
                changed[y * fw + x] = c;
                if (c) quantizer.Add(px[i], px[i + 1], px[i + 2]);
            }

        quantizer.Build(useTransparency ? 255 : 256);
        int colors = quantizer.ColorCount;
        int transparent = useTransparency ? colors : -1;
        int total = Math.Max(2, colors + (useTransparency ? 1 : 0));
        int bits = 1;
        while ((1 << bits) < total) bits++;

        var palette = new byte[(1 << bits) * 3];
        Buffer.BlockCopy(quantizer.Palette, 0, palette, 0, colors * 3);

        var indices = new byte[fw * fh];
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
            {
                int k = y * fw + x;
                if (!changed[k]) { indices[k] = (byte)transparent; continue; }
                int i = ((fy + y) * width + fx + x) * 4;
                int idx = quantizer.IndexOf(px[i], px[i + 1], px[i + 2]);
                indices[k] = (byte)idx;
                // The viewer now shows the palette colour; compare later frames against the source
                // value so tolerance does not accumulate drift.
                reference[i] = px[i]; reference[i + 1] = px[i + 1]; reference[i + 2] = px[i + 2];
            }

        return new PendingFrame
        {
            X = fx, Y = fy, W = fw, H = fh, Delay = delay, PaletteBits = bits,
            Transparent = transparent, Palette = palette, Indices = indices,
        };
    }

    private void Flush()
    {
        var f = pending;
        if (f == null) return;
        pending = null;

        // Graphic Control Extension: disposal 1 (leave in place), optional transparency.
        output.WriteByte(0x21); output.WriteByte(0xF9); output.WriteByte(4);
        output.WriteByte((byte)((1 << 2) | (f.Transparent >= 0 ? 1 : 0)));
        WriteU16(f.Delay);
        output.WriteByte((byte)(f.Transparent >= 0 ? f.Transparent : 0));
        output.WriteByte(0);

        // Image descriptor with a local colour table.
        output.WriteByte(0x2C);
        WriteU16(f.X); WriteU16(f.Y); WriteU16(f.W); WriteU16(f.H);
        output.WriteByte((byte)(0x80 | (f.PaletteBits - 1)));
        output.Write(f.Palette, 0, f.Palette.Length);

        LzwWriter.Write(output, f.Indices, Math.Max(2, f.PaletteBits));
    }

    private void WriteHeader()
    {
        output.Write("GIF89a"u8);
        WriteU16(width); WriteU16(height);
        output.WriteByte(0x70);   // no global colour table, 8-bit colour resolution
        output.WriteByte(0);      // background index
        output.WriteByte(0);      // square pixels
        // NETSCAPE2.0 application extension: loop forever.
        output.WriteByte(0x21); output.WriteByte(0xFF); output.WriteByte(11);
        output.Write("NETSCAPE2.0"u8);
        output.WriteByte(3); output.WriteByte(1); WriteU16(0); output.WriteByte(0);
    }

    private void WriteU16(int v) { output.WriteByte((byte)(v & 0xFF)); output.WriteByte((byte)((v >> 8) & 0xFF)); }
}

/// GIF-flavoured variable-length LZW (codes up to 12 bits, clear code when the table is full),
/// written as 255-byte data sub-blocks.
internal static class LzwWriter
{
    public static void Write(Stream s, byte[] pixels, int minCodeSize)
    {
        s.WriteByte((byte)minCodeSize);
        var bw = new BitWriter(s);
        int clear = 1 << minCodeSize, eoi = clear + 1;
        int codeSize = minCodeSize + 1, next = clear + 2;
        var table = new Dictionary<int, int>(4096);

        bw.Write(clear, codeSize);
        if (pixels.Length > 0)
        {
            int prefix = pixels[0];
            for (int i = 1; i < pixels.Length; i++)
            {
                int c = pixels[i];
                int key = (prefix << 8) | c;
                if (table.TryGetValue(key, out int code)) { prefix = code; continue; }

                bw.Write(prefix, codeSize);
                if (next < 4096)
                {
                    // The decoder widens its codes one entry later than it adds them, so widen when the
                    // code just assigned no longer fits the current size.
                    if (next == (1 << codeSize) && codeSize < 12) codeSize++;
                    table[key] = next++;
                }
                else
                {
                    bw.Write(clear, codeSize);
                    table.Clear();
                    codeSize = minCodeSize + 1;
                    next = clear + 2;
                }
                prefix = c;
            }
            bw.Write(prefix, codeSize);
            // The decoder adds an entry after this last code too, and may widen before reading EOI.
            if (next == (1 << codeSize) && codeSize < 12) codeSize++;
        }
        bw.Write(eoi, codeSize);
        bw.Flush();
        s.WriteByte(0);   // block terminator
    }

    private sealed class BitWriter
    {
        private readonly Stream s;
        private readonly byte[] block = new byte[255];
        private int blockLen, acc, bits;

        public BitWriter(Stream s) { this.s = s; }

        public void Write(int code, int size)
        {
            acc |= code << bits;
            bits += size;
            while (bits >= 8) { Put((byte)(acc & 0xFF)); acc >>= 8; bits -= 8; }
        }

        public void Flush()
        {
            if (bits > 0) { Put((byte)(acc & 0xFF)); acc = 0; bits = 0; }
            if (blockLen > 0) { s.WriteByte((byte)blockLen); s.Write(block, 0, blockLen); blockLen = 0; }
        }

        private void Put(byte b)
        {
            block[blockLen++] = b;
            if (blockLen == 255) { s.WriteByte(255); s.Write(block, 0, 255); blockLen = 0; }
        }
    }
}
