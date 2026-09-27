using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// Runner integration for <see cref="ActionKind.FindImage"/>. Only the timeout paths are
/// testable headlessly: a found match requires a real screen capture, so the capture-and-click
/// path is covered by manual verification while the pure search lives in
/// <see cref="ImageMatcherTests"/>. A 1×1 search region can never contain a 10×10 template, so
/// the lookup fails fast and the wait times out deterministically.
/// </summary>
public class VisualFindTests
{
    private static byte[] DummyPng(int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Red);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    [Fact]
    public void FindImage_times_out_without_throwing_when_not_aborting()
    {
        bool performed = true;
        var actions = new List<SeqAction>
        {
            new()
            {
                Kind = ActionKind.FindImage,
                TemplatePng = DummyPng(10, 10),
                SearchW = 1, SearchH = 1, // 1×1 region can never hold a 10×10 template
                PixelTimeoutMs = 60,
                PollIntervalMs = 10,
                AbortRunOnTimeout = false,
            },
        };

        var runner = new SequenceRunner(() => true, onStep: (idx, wasPerformed) => { if (idx == 0) performed = wasPerformed; });
        runner.RunSequence(actions, new RunOptions { Limited = true, Limit = 1 });

        Assert.False(performed);
    }

    [Fact]
    public void FindImage_times_out_and_throws_when_aborting()
    {
        var actions = new List<SeqAction>
        {
            new()
            {
                Kind = ActionKind.FindImage,
                TemplatePng = DummyPng(10, 10),
                SearchW = 1, SearchH = 1,
                PixelTimeoutMs = 60,
                PollIntervalMs = 10,
                AbortRunOnTimeout = true,
            },
        };

        var runner = new SequenceRunner(() => true);
        var ex = Assert.Throws<InvalidOperationException>(
            () => runner.RunSequence(actions, new RunOptions { Limited = true, Limit = 1 }));

        Assert.Contains("image", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
