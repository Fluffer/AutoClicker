using Xunit;

namespace AutoClicker.Tests;

public class OcrTextReaderTests
{
    [Fact]
    public void Ocr_availability_probe_does_not_throw()
    {
        // Windows OCR needs a language pack; on machines without one the feature reports
        // unavailable rather than throwing at run time. The probe itself must never throw,
        // and the actual recognition path is exercised manually (and skips gracefully via
        // a clear InvalidOperationException when the engine is absent).
        _ = OcrTextReader.IsAvailable;
    }
}
