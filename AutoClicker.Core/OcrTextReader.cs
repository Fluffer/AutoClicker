using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AutoClicker;

/// <summary>
/// Reads screen text through the Windows built-in OCR engine (<c>Windows.Media.Ocr</c>, the
/// same engine behind PowerToys / the legacy OCR API), so "find the word 'Submit'" works with
/// no third-party OCR dependency. Requires the <c>net10.0-windows10.0.17763.0</c> target
/// framework, which pulls in the WinRT projections that expose
/// <see cref="Windows.Media.Ocr.OcrEngine"/>, <see cref="Windows.Graphics.Imaging"/> and
/// <see cref="Windows.Storage.Streams"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Interop.</b> A <see cref="Bitmap"/> has no direct WinRT representation, so it is saved
/// to an in-memory BMP stream and fed to <see cref="BitmapDecoder"/> over an
/// <see cref="InMemoryRandomAccessStream"/>. BMP is chosen deliberately: it is opaque, so the
/// decoder yields <c>Bgra8</c> with premultiplied alpha — exactly the pixel format
/// <see cref="OcrEngine.RecognizeAsync(SoftwareBitmap)"/> accepts — with no conversion step.
/// WinRT async methods are bridged with <c>AsTask().GetAwaiter().GetResult()</c>; this runs on
/// the engine's worker thread, so blocking is fine and keeps the API synchronous for the runner.
/// </para>
/// <para>
/// <b>Language.</b> The engine is created from the user's profile languages
/// (<see cref="OcrEngine.TryCreateFromUserProfileLanguages"/>). A machine with no OCR language
/// pack yields null and the feature throws with a clear message rather than guessing.
/// </para>
/// </remarks>
internal static class OcrTextReader
{
    /// <summary>Regex evaluation timeout, so a pathological pattern cannot hang the run.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>False when Windows OCR has no language pack installed on this machine.</summary>
    public static bool IsAvailable => TryCreateEngine() is not null;

    /// <summary>
    /// OCRs <paramref name="image"/> and looks for <paramref name="query"/>. Matching is
    /// case-insensitive; word-level first (the target is "click this word"), then a whole-line
    /// <c>Contains</c> fallback. The returned box is the matched word/line's bounding rectangle
    /// in <paramref name="image"/> coordinates.
    /// </summary>
    /// <exception cref="InvalidOperationException">OCR is unavailable, or the regex is invalid.</exception>
    public static bool TryFind(Bitmap image, string query, bool regex, out Rectangle box, out double score)
    {
        box = Rectangle.Empty;
        score = 0;

        if (string.IsNullOrWhiteSpace(query)) return false;

        OcrEngine engine = TryCreateEngine()
            ?? throw new InvalidOperationException(
                "Windows OCR is not available on this machine (no OCR language pack). " +
                "Install a language pack under Settings → Time & Language, or use a Find image action instead.");

        using SoftwareBitmap bitmap = ToSoftwareBitmap(image);
        OcrResult result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

        // Word-level first: the whole point of FindText is clicking a word.
        foreach (OcrLine line in result.Lines)
        {
            foreach (OcrWord word in line.Words)
            {
                if (Matches(word.Text, query, regex))
                {
                    box = ToRect(word.BoundingRect);
                    score = 1.0;
                    return true;
                }
            }
        }

        // Fall back to "the line contains the query" — covers labels OCR splits oddly.
        if (!regex)
        {
            foreach (OcrLine line in result.Lines)
            {
                if (line.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    box = UnionOfWords(line);
                    score = 1.0;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Matches(string text, string query, bool regex)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!regex) return text.Equals(query, StringComparison.OrdinalIgnoreCase);

        try
        {
            return Regex.IsMatch(text, query, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"The FindText pattern \"{query}\" is not a valid regular expression: {ex.Message}");
        }
    }

    private static OcrEngine? TryCreateEngine()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch
        {
            // WinRT activation can fail in odd ways (e.g. a stripped-down Windows image);
            // treat any failure as "not available" so the caller can report it cleanly.
            return null;
        }
    }

    /// <summary>Bounding box of a whole line: the union of its words' boxes.</summary>
    private static Rectangle UnionOfWords(OcrLine line)
    {
        Rectangle? union = null;
        foreach (OcrWord word in line.Words)
        {
            Rectangle r = ToRect(word.BoundingRect);
            union = union is null ? r : Rectangle.Union(union.Value, r);
        }
        return union ?? Rectangle.Empty;
    }

    private static Rectangle ToRect(Rect r) =>
        new((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));

    /// <summary>Bitmap → SoftwareBitmap via an in-memory BMP stream (see class remarks).</summary>
    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Bmp);

        var ras = new InMemoryRandomAccessStream();
        var writer = new DataWriter(ras.GetOutputStreamAt(0));
        writer.WriteBytes(ms.ToArray());
        writer.StoreAsync().AsTask().GetAwaiter().GetResult();
        writer.DetachStream();
        ras.Seek(0);

        BitmapDecoder decoder = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
        return decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
    }
}
