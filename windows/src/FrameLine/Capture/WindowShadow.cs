using System;

namespace FrameLine.Capture;

/// Window capture finishing: Windows 11 rounded corners and a soft drop shadow close to the one
/// `screencapture -w` bakes into macOS window shots. Works on straight-alpha BGRA buffers.
internal static class WindowShadow
{
    // In DIPs, scaled by the window's monitor DPI.
    private const double Sigma = 14, OffsetY = 14, Opacity = 0.45;
    private const double MarginSide = 42, MarginTop = 28, MarginBottom = 56;

    public static void RoundCorners(byte[] px, int w, int h, double radius)
    {
        int r = (int)Math.Ceiling(radius);
        if (r <= 0 || w < 2 * r || h < 2 * r) return;
        for (int y = 0; y < r; y++)
        for (int x = 0; x < r; x++)
        {
            double dx = radius - (x + 0.5), dy = radius - (y + 0.5);
            double cover = Math.Clamp(radius - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
            if (cover >= 1) continue;
            Fade(px, w, x, y, cover);
            Fade(px, w, w - 1 - x, y, cover);
            Fade(px, w, x, h - 1 - y, cover);
            Fade(px, w, w - 1 - x, h - 1 - y, cover);
        }
    }

    private static void Fade(byte[] px, int w, int x, int y, double cover)
    {
        int i = (y * w + x) * 4 + 3;
        px[i] = (byte)Math.Round(px[i] * cover);
    }

    /// Returns a larger buffer: the window centred on its shadow. `w`/`h` are updated.
    public static byte[] Apply(byte[] src, ref int w, ref int h, double scale)
    {
        int side = (int)Math.Round(MarginSide * scale);
        int top = (int)Math.Round(MarginTop * scale);
        int bottom = (int)Math.Round(MarginBottom * scale);
        int offY = (int)Math.Round(OffsetY * scale);
        int sw = w, sh = h;
        int W = sw + 2 * side, H = sh + top + bottom;

        var mask = new int[checked(W * H)];
        for (int y = 0; y < sh; y++)
        {
            int ty = y + top + offY;
            if (ty < 0 || ty >= H) continue;
            for (int x = 0; x < sw; x++) mask[ty * W + x + side] = src[(y * sw + x) * 4 + 3];
        }

        // Three box passes per axis approximate a gaussian of the given sigma.
        double sigma = Sigma * scale;
        int radius = Math.Max(1, (int)Math.Round((Math.Sqrt(12 * sigma * sigma / 3 + 1) - 1) / 2));
        var tmp = new int[mask.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxHorizontal(mask, tmp, W, H, radius);
            BoxVertical(tmp, mask, W, H, radius);
        }

        var dst = new byte[checked(W * H * 4)];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int o = (y * W + x) * 4;
            double sa = mask[y * W + x] / 255.0 * Opacity;
            int wx = x - side, wy = y - top;
            if (wx >= 0 && wy >= 0 && wx < sw && wy < sh)
            {
                int s = (wy * sw + wx) * 4;
                double wa = src[s + 3] / 255.0;
                double ra = wa + sa * (1 - wa);
                if (ra <= 0) continue;
                // Shadow is black: only the window contributes colour.
                dst[o] = (byte)Math.Round(src[s] * wa / ra);
                dst[o + 1] = (byte)Math.Round(src[s + 1] * wa / ra);
                dst[o + 2] = (byte)Math.Round(src[s + 2] * wa / ra);
                dst[o + 3] = (byte)Math.Round(ra * 255);
            }
            else
            {
                dst[o + 3] = (byte)Math.Round(sa * 255);
            }
        }
        w = W; h = H;
        return dst;
    }

    private static void BoxHorizontal(int[] src, int[] dst, int w, int h, int r)
    {
        int div = 2 * r + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w, sum = 0;
            for (int x = 0; x <= r && x < w; x++) sum += src[row + x];
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = sum / div;
                int add = x + r + 1, sub = x - r;
                if (add < w) sum += src[row + add];
                if (sub >= 0) sum -= src[row + sub];
            }
        }
    }

    private static void BoxVertical(int[] src, int[] dst, int w, int h, int r)
    {
        int div = 2 * r + 1;
        for (int x = 0; x < w; x++)
        {
            int sum = 0;
            for (int y = 0; y <= r && y < h; y++) sum += src[y * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = sum / div;
                int add = y + r + 1, sub = y - r;
                if (add < h) sum += src[add * w + x];
                if (sub >= 0) sum -= src[sub * w + x];
            }
        }
    }
}
