using System;
using System.Collections.Generic;

namespace FrameLine.Recording;

/// Median-cut palette over a 15-bit (RGB 5-5-5) histogram. Screen content is mostly flat UI colours,
/// so no dithering: crisp text and much smaller GIFs. Every histogram bin maps to its nearest
/// palette entry, so mapping a pixel is a table lookup.
internal sealed class ColorQuantizer
{
    private const int Bins = 32768;
    private readonly int[] count = new int[Bins];
    private readonly long[] sumR = new long[Bins], sumG = new long[Bins], sumB = new long[Bins];
    private readonly short[] binToIndex = new short[Bins];
    private readonly List<int> used = new();

    public byte[] Palette { get; private set; } = Array.Empty<byte>();   // RGB triplets
    public int ColorCount { get; private set; }

    public static int Bin(byte b, byte g, byte r) => ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);

    public void Reset()
    {
        foreach (var bin in used) { count[bin] = 0; sumR[bin] = sumG[bin] = sumB[bin] = 0; }
        used.Clear();
    }

    public void Add(byte b, byte g, byte r)
    {
        int bin = Bin(b, g, r);
        if (count[bin]++ == 0) used.Add(bin);
        sumR[bin] += r; sumG[bin] += g; sumB[bin] += b;
    }

    public int IndexOf(byte b, byte g, byte r) => binToIndex[Bin(b, g, r)];

    /// Builds at most maxColors entries. Call after the Add pass.
    public void Build(int maxColors)
    {
        var boxes = new List<Box>();
        if (used.Count > 0) boxes.Add(MakeBox(new List<int>(used)));

        while (boxes.Count < maxColors)
        {
            int pick = -1; long best = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                var bx = boxes[i];
                if (bx.Bins.Count < 2) continue;
                long score = (long)bx.MaxRange * bx.Pixels;
                if (pick < 0 || score > best) { pick = i; best = score; }
            }
            if (pick < 0) break;
            var (a, c) = Split(boxes[pick]);
            boxes[pick] = a;
            boxes.Add(c);
        }

        var pal = new byte[boxes.Count * 3];
        for (int i = 0; i < boxes.Count; i++)
        {
            long n = 0, r = 0, g = 0, b = 0;
            foreach (var bin in boxes[i].Bins)
            {
                n += count[bin]; r += sumR[bin]; g += sumG[bin]; b += sumB[bin];
                binToIndex[bin] = (short)i;
            }
            n = Math.Max(1, n);
            pal[i * 3] = (byte)((r + n / 2) / n);
            pal[i * 3 + 1] = (byte)((g + n / 2) / n);
            pal[i * 3 + 2] = (byte)((b + n / 2) / n);
        }
        Palette = pal;
        ColorCount = boxes.Count;
        if (used.Count > ColorCount) RemapToNearest();
    }

    /// A box can be long on the channels it was never split on; mapping each bin to its nearest
    /// palette entry (instead of its box) removes the occasional far-off colour.
    private void RemapToNearest()
    {
        var pal = Palette;
        foreach (var bin in used)
        {
            long n = Math.Max(1, count[bin]);
            int r = (int)(sumR[bin] / n), g = (int)(sumG[bin] / n), b = (int)(sumB[bin] / n);
            int best = 0, bestD = int.MaxValue;
            for (int i = 0; i < ColorCount; i++)
            {
                int dr = pal[i * 3] - r, dg = pal[i * 3 + 1] - g, db = pal[i * 3 + 2] - b;
                int d = 2 * dr * dr + 4 * dg * dg + 3 * db * db;
                if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
            }
            binToIndex[bin] = (short)best;
        }
    }

    private sealed class Box
    {
        public List<int> Bins = new();
        public long Pixels;
        public int MaxRange;
        public int Channel;   // 0 = r, 1 = g, 2 = b
    }

    private static int Ch(int bin, int channel) => channel switch
    {
        0 => (bin >> 10) & 31,
        1 => (bin >> 5) & 31,
        _ => bin & 31,
    };

    private Box MakeBox(List<int> bins)
    {
        int[] lo = { 31, 31, 31 }, hi = { 0, 0, 0 };
        long px = 0;
        foreach (var bin in bins)
        {
            px += count[bin];
            for (int c = 0; c < 3; c++)
            {
                int v = Ch(bin, c);
                if (v < lo[c]) lo[c] = v;
                if (v > hi[c]) hi[c] = v;
            }
        }
        int ch = 0, range = -1;
        for (int c = 0; c < 3; c++)
            if (hi[c] - lo[c] > range) { range = hi[c] - lo[c]; ch = c; }
        return new Box { Bins = bins, Pixels = px, MaxRange = Math.Max(1, range + 1), Channel = ch };
    }

    /// Splits at the pixel-weighted median of the widest channel.
    private (Box, Box) Split(Box box)
    {
        int ch = box.Channel;
        box.Bins.Sort((x, y) => Ch(x, ch).CompareTo(Ch(y, ch)));
        long half = box.Pixels / 2, acc = 0;
        int cut = 1;
        for (int i = 0; i < box.Bins.Count - 1; i++)
        {
            acc += count[box.Bins[i]];
            cut = i + 1;
            if (acc >= half) break;
        }
        return (MakeBox(box.Bins.GetRange(0, cut)), MakeBox(box.Bins.GetRange(cut, box.Bins.Count - cut)));
    }
}
