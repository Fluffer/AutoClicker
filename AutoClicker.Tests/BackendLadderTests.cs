using Xunit;

namespace AutoClicker.Tests;

public class BackendLadderTests
{
    [Theory]
    [InlineData(0, 1)] // SendInput blocked -> PostMessage
    [InlineData(1, 0)] // PostMessage failed -> SendInput
    [InlineData(2, 1)] // UIA invoke failed -> PostMessage
    public void NextBackendOnFailure_steps_down_the_ladder(int method, int expected)
    {
        Assert.Equal(expected, SequenceRunner.NextBackendOnFailure(method));
    }
}
