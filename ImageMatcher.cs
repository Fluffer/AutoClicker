using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AutoClicker;

/// <summary>
/// Locates an image template inside a larger capture using normalized cross-correlation
/// (the <c>TM_CCOEFF_NORMED</c> measure OpenCV exposes), implemented in pure managed code
/// so the app ships no native OpenCV dependency (~60 MB of binaries) into its MSIX.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scale / DPI caveat.</b> Matching is exact-scale only. A template captured at 100%
/// display scale will not match the same element at 150% per-monitor DPI — the pixels
/// genuinely differ. Future work: a scale sweep (e.g. 0.9–1.1×). Templates should be
/// captured on the same display setup they are replayed against.
/// </para>
/// <para>
/// <b>Performance.</b> A naive full NCC over a 500×400 region with a 60×60 template is
/// ~150,000 windows × 3,600 pixels — too slow in pure C#. Four mitigations bring it under
/// target: (a) grayscale byte arrays (LockBits, no per-pixel GetPixel); (b) an integral
/// image + integral-of-squares so a window's mean and variance are O(1), enabling a cheap
/// mean-difference gate that rejects most windows before any pixel loop; (c) a coarse-to-fine
/// search — stride-2 first pass, then stride-1 refinement around the best candidates; and
/// (d) a hard cap on the region size so a pathological search area fails fast instead of
/// hanging.
/// </para>
/// </remarks>
internal static class ImageMatcher
{
    /// <summary>
    /// Largest capture we are willing to allocate and scan. Full 4K (3840×2160 ≈ 8.3M px)
    /// fits comfortably; a dual-4K virtual screen is refused with a clear message rather
    /// than grinding. Regions beyond this should be narrowed in the editor.
    /// </summary>
    private const int MaxRegionPixels = 16_777_216; // 4096 × 4096

    /// <summary>
    /// Early-rejection gate on mean luminance difference (grayscale levels 0-255). A window
    /// whose average brightness differs from the template's by more than this cannot be a
    /// good NCC match, so its expensive dot product is skipped. Kept generous (≈19% of the
    /// range) so a mild brightness offset does not hide a real match.
    /// </summary>
    private const double MeanGate = 48.0;

    /// <summary>How many stride-2 coarse candidates are refined at stride 1.</summary>
    private const int CoarseStride = 2;
    private const int KeepCandidates = 16;

    /// <summary>
    /// Captures a screen region into a 32bpp bitmap. Coordinates are virtual-screen
    /// coordinates, so negative values (a monitor left of / above the primary) are legal
    /// and handled by <see cref="Graphics.CopyFromScreen"/>, which the multi-monitor aware
    /// GDI path supports. The region is intersected with the virtual screen and an entirely
    /// off-screen or oversized request throws rather than silently capturing black.
    /// </summary>
    public static Bitmap CaptureRegion(Rectangle screenRect)
    {
        Rectangle region = Rectangle.Intersect(screenRect, SystemInformation.VirtualScreen);
        if (region.Width <= 0 || region.Height <= 0)
            throw new InvalidOperationException(
                $"The capture area {screenRect} is entirely off-screen. Point it at a visible display.");

        if ((long)region.Width * region.Height > MaxRegionPixels)
            throw new InvalidOperationException(
                $"The search area {region.Width}×{region.Height} is too large to scan. Narrow it in the editor.");

        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        // sourceX/sourceY are virtual-screen coordinates; destination is the bitmap origin.
        g.CopyFromScreen(region.X, region.Y, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>
    /// Searches <paramref name="screen"/> for the best normalized cross-correlation match of
    /// <paramref name="template"/> and reports whether it meets <paramref name="threshold"/>.
    /// </summary>
    /// <param name="screen">The region to search (already captured).</param>
    /// <param name="template">What to look for; must be no larger than the screen.</param>
    /// <param name="threshold">Minimum NCC score in [0,1] to accept a match.</param>
    /// <param name="location">Top-left of the best match, in screen bitmap coordinates.</param>
    /// <param name="score">The NCC score of the best match (0 when not found).</param>
    public static bool TryFind(Bitmap screen, Bitmap template, double threshold, out Point location, out double score)
    {
        location = Point.Empty;
        score = 0;

        int sw = screen.Width, sh = screen.Height;
        int tw = template.Width, th = template.Height;
        if (tw <= 0 || th <= 0 || tw > sw || th > sh) return false;

        threshold = double.IsNaN(threshold) ? 0.85 : Math.Clamp(threshold, 0.0, 1.0);

        byte[] s = Grayscale(screen);
        byte[] t = Grayscale(template);

        int area = tw * th;

        // Template statistics (computed once). The zero-mean template tz lets the numerator
        // Σ(a-ā)(b-b̄) reduce to Σ a·tz, the only per-window O(area) term left.
        long tSum = 0;
        foreach (byte v in t) tSum += v;
        double tMean = (double)tSum / area;

        short[] tz = new short[area];
        long tVar = 0;
        for (int i = 0; i < area; i++)
        {
            short d = (short)(t[i] - tMean);
            tz[i] = d;
            tVar += d * d;
        }

        // A flat template has no variance — every window scores NaN — so it can never be
        // uniquely located. Refuse it up front rather than returning garbage.
        if (tVar == 0) return false;

        BuildIntegrals(s, sw, sh, out long[] sum, out long[] sqSum);

        int maxX = sw - tw, maxY = sh - th;

        // Local scorer capturing the search arrays and template statistics.
        double ScoreAt(int x, int y)
        {
            long wSum = WindowSum(sum, sw, x, y, tw, th);
            double wMean = (double)wSum / area;

            // Cheap |ā-b̄| gate before any pixel work. See MeanGate.
            if (Math.Abs(wMean - tMean) > MeanGate) return -1.0;

            long wSq = WindowSum(sqSum, sw, x, y, tw, th);
            double var = (double)wSq - (double)wSum * wSum / area; // Σ(a-ā)²
            if (var <= 0) return 0.0;                              // flat window: undefined

            long dot = 0;
            for (int yy = 0; yy < th; yy++)
            {
                int srow = (y + yy) * sw + x;
                int trow = yy * tw;
                for (int xx = 0; xx < tw; xx++)
                    dot += s[srow + xx] * tz[trow + xx];
            }

            double denom = Math.Sqrt(var * tVar);
            return denom == 0 ? 0.0 : dot / denom;
        }

        double best = -1.0;
        Point bestLoc = Point.Empty;

        // Coarse pass: stride-2 grid. The global best is tracked directly; the top K
        // coarse candidates (by score) are kept for the stride-1 refinement below.
        var coarse = new List<(Point p, double s)>(KeepCandidates);
        for (int y = 0; y <= maxY; y += CoarseStride)
        {
            for (int x = 0; x <= maxX; x += CoarseStride)
            {
                double sc = ScoreAt(x, y);
                if (sc > best) { best = sc; bestLoc = new Point(x, y); }
                InsertTopK(coarse, new Point(x, y), sc);
            }
        }

        // Fine pass: stride 1 around each coarse candidate (±1 covers the true peak, which
        // can sit at an odd offset one pixel from any stride-2 sample).
        foreach (var (p, _) in coarse)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = p.X + dx, ny = p.Y + dy;
                    if (nx < 0 || ny < 0 || nx > maxX || ny > maxY) continue;
                    double sc = ScoreAt(nx, ny);
                    if (sc > best) { best = sc; bestLoc = new Point(nx, ny); }
                }
            }
        }

        if (best >= threshold)
        {
            location = bestLoc;
            score = best;
            return true;
        }
        return false;
    }

    /// <summary>Keeps only the <see cref="KeepCandidates"/> highest-scoring entries.</summary>
    private static void InsertTopK(List<(Point p, double s)> list, Point p, double s)
    {
        if (list.Count < KeepCandidates) { list.Add((p, s)); return; }
        int worst = 0;
        for (int i = 1; i < list.Count; i++)
            if (list[i].s < list[worst].s) worst = i;
        if (s > list[worst].s) list[worst] = (p, s);
    }

    // ---- Grayscale / integral plumbing ----

    /// <summary>Converts a 32bpp bitmap to a luminance byte array (Rec. 601 weights).</summary>
    private static byte[] Grayscale(Bitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var rect = new Rectangle(0, 0, w, h);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var gray = new byte[w * h];
        try
        {
            int stride = data.Stride;
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * stride), row, 0, stride);
                int dest = y * w;
                for (int x = 0; x < w; x++)
                {
                    // 32bppArgb is stored BGRA in little-endian memory order.
                    byte b = row[x * 4];
                    byte g = row[x * 4 + 1];
                    byte r = row[x * 4 + 2];
                    gray[dest + x] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return gray;
    }

    /// <summary>Builds the sum and sum-of-squares integral images in a single pass.</summary>
    private static void BuildIntegrals(byte[] gray, int w, int h, out long[] sum, out long[] sqSum)
    {
        int stride = w + 1;
        sum = new long[stride * (h + 1)];
        sqSum = new long[stride * (h + 1)];

        for (int y = 0; y < h; y++)
        {
            long rowSum = 0, rowSq = 0;
            int row = y * w;
            int outRow = (y + 1) * stride;
            int prevRow = y * stride;
            for (int x = 0; x < w; x++)
            {
                byte v = gray[row + x];
                rowSum += v;
                rowSq += v * v;
                sum[outRow + x + 1] = sum[prevRow + x + 1] + rowSum;
                sqSum[outRow + x + 1] = sqSum[prevRow + x + 1] + rowSq;
            }
        }
    }

    /// <summary>Sum of the tw×th window at (x,y) via the integral image, in O(1).</summary>
    private static long WindowSum(long[] integral, int w, int x, int y, int tw, int th)
    {
        int stride = w + 1;
        long a = integral[y * stride + x];
        long b = integral[y * stride + (x + tw)];
        long c = integral[(y + th) * stride + x];
        long d = integral[(y + th) * stride + (x + tw)];
        return d - b - c + a;
    }
}
