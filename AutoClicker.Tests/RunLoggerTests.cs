using System.Text.Json;
using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// RunLogger.DirectoryPath is process-global, so every test here redirects it to a unique
/// temp folder and restores it afterward — the same seam pattern ProfileStoreTests uses.
/// </summary>
[Collection("RunLogger")]
public class RunLoggerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _originalDir;

    public RunLoggerTests()
    {
        _originalDir = RunLogger.DirectoryPath;
        _dir = Path.Combine(Path.GetTempPath(), "AutoClickerRunLog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        RunLogger.DirectoryPath = _dir;
    }

    public void Dispose()
    {
        RunLogger.DirectoryPath = _originalDir;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Log_writes_valid_json_lines_that_read_back()
    {
        var logger = RunLogger.TryCreate();
        Assert.NotNull(logger);

        logger!.Log(0, 2, "Click", "Click Left", performed: true, x: 10, y: 20, note: "ok");
        logger.Log(1, 3, "Wait", "Wait", performed: false);
        logger.Dispose();

        Assert.NotNull(logger.FilePath);
        string[] lines = File.ReadAllLines(logger.FilePath!);
        Assert.Equal(2, lines.Length);

        using (var first = JsonDocument.Parse(lines[0]))
        {
            var root = first.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.Equal(2, root.GetProperty("ip").GetInt32());
            Assert.Equal("Click", root.GetProperty("kind").GetString());
            Assert.Equal(10, root.GetProperty("x").GetInt32());
            Assert.Equal(20, root.GetProperty("y").GetInt32());
            Assert.Equal("ok", root.GetProperty("note").GetString());
            Assert.True(root.GetProperty("performed").GetBoolean());
        }

        using (var second = JsonDocument.Parse(lines[1]))
        {
            var root = second.RootElement;
            Assert.Equal(1, root.GetProperty("pass").GetInt32());
            Assert.False(root.TryGetProperty("x", out _)); // no coordinates for a Wait step
        }
    }

    [Fact]
    public void Dispose_then_log_is_a_safe_no_op()
    {
        var logger = RunLogger.TryCreate();
        Assert.NotNull(logger);
        logger!.Dispose();
        logger.Log(0, 0, "Click", "Click Left", true, 1, 2); // must not throw
    }
}
