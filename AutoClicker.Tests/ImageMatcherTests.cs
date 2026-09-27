using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// Pure template-matching tests. Everything operates on synthetic bitmaps — no screen
/// capture, no display dependency — so these run identically everywhere.
/// </summary>
public class ImageMatcherTests
{
    // ---- Fixtures ----

    private static Bitmap MakeScreen(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(40, 40, 40));
        return bmp;
    }

    /// <summary>A distinctive, non-flat template (a flat colour has undefined NCC).</summary>
    private static Bitmap MakeTemplate(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, Color.FromArgb((x * 7) % 256, (y * 11) % 256, (x + y) % 256));
        return bmp;
    }

    /// <summary>Copies the patch into the screen pixel-for-pixel, so the match is exact.</summary>
    private static void Plant(Bitmap screen, Bitmap patch, int px, int py)
    {
        for (int y = 0; y < patch.Height; y++)
            for (int x = 0; x < patch.Width; x++)
                screen.SetPixel(px + x, py + y, patch.GetPixel(x, y));
    }

    // ---- Correctness ----

    [Fact]
    public void Finds_planted_template_at_the_exact_location()
    {
        using var screen = MakeScreen(500, 400);
        using var template = MakeTemplate(60, 60);
        Plant(screen, template, 123, 87);

        bool found = ImageMatcher.TryFind(screen, template, 0.85, out Point location, out double score);

        Assert.True(found);
        Assert.Equal(123, location.X);
        Assert.Equal(87, location.Y);
        Assert.True(score > 0.99, $"expected ~1.0, got {score}");
    }

    [Fact]
    public void Dissimilar_screen_is_not_found_above_threshold()
    {
        using var screen = MakeScreen(300, 200);        // uniform dark background
        using var template = MakeTemplate(40, 40);      // a pattern that is nowhere present

        bool found = ImageMatcher.TryFind(screen, template, 0.85, out _, out double score);

        Assert.False(found);
        Assert.True(score < 0.85, $"expected a low score, got {score}");
    }

    [Fact]
    public void Threshold_boundary_accepts_at_score_and_rejects_above()
    {
        using var screen = MakeScreen(300, 200);
        using var template = MakeTemplate(60, 60);
        Plant(screen, template, 50, 40);
        screen.SetPixel(50, 40, Color.Black); // corrupt one pixel to push the score below 1.0

        // Calibrate the actual best score, then assert the boundary lands exactly around it.
        Assert.True(ImageMatcher.TryFind(screen, template, 0.0, out _, out double score));
        Assert.True(score > 0.9 && score < 1.0, $"expected a near-1.0 score, got {score}");

        Assert.True(ImageMatcher.TryFind(screen, template, score, out _, out _));
        Assert.False(ImageMatcher.TryFind(screen, template, score + 0.01, out _, out _));
    }

    [Fact]
    public void Template_larger_than_the_screen_is_not_found()
    {
        using var screen = new Bitmap(50, 50);
        using var template = new Bitmap(60, 60);

        Assert.False(ImageMatcher.TryFind(screen, template, 0.5, out _, out _));
    }

    // ---- Performance smoke (generous bound; correctness is what matters) ----

    [Fact]
    public void Matching_a_60x60_template_in_a_500x400_region_is_fast()
    {
        using var screen = MakeScreen(500, 400);
        using var template = MakeTemplate(60, 60);
        Plant(screen, template, 200, 150);

        var sw = Stopwatch.StartNew();
        bool found = ImageMatcher.TryFind(screen, template, 0.85, out _, out _);
        sw.Stop();

        Assert.True(found);
        // Generous 2 s bound so this never flakes on a slow CI box; the real target is ~300 ms.
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
