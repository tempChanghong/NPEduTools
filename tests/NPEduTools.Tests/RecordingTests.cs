using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class RecordingTests
{
    [Theory]
    [InlineData(3840, 2160, 1080, 1920, 1080)]
    [InlineData(2560, 1600, 1080, 1728, 1080)]
    [InlineData(1080, 1920, 1080, 606, 1080)]
    [InlineData(1280, 720, 1080, 1280, 720)]
    [InlineData(3840, 1080, 1080, 1920, 540)]
    [InlineData(1365, 767, 720, 1280, 718)]
    public void OutputBoundsPreserveAspectWithoutUpscaling(int width, int height, int cap, int expectedWidth, int expectedHeight)
    {
        var size = RecordingContract.OutputSize(width, height, cap);
        Assert.Equal((expectedWidth, expectedHeight), size);
        Assert.True(size.Width <= width && size.Height <= height);
        Assert.Equal(0, size.Width % 2); Assert.Equal(0, size.Height % 2);
    }
    [Fact]
    public void RecordingRejectsUnboundedModesAndRelativePaths()
    {
        var valid = new RecordingOptions("display", Path.Combine(Path.GetTempPath(), "recordings"));
        Assert.Null(RecordingContract.Validate(valid));
        Assert.NotNull(RecordingContract.Validate(valid with { FramesPerSecond = 60 }));
        Assert.NotNull(RecordingContract.Validate(valid with { MaximumHeight = 2160 }));
        Assert.NotNull(RecordingContract.Validate(valid with { OutputDirectory = "relative" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecordingContract.OutputSize(20000, 20000, 1080));
    }
    [Theory]
    [InlineData("Recording", true, false)]
    [InlineData("Paused", true, false)]
    [InlineData("Saving", true, true)]
    [InlineData("Starting", true, true)]
    [InlineData("Saved", false, false)]
    [InlineData("Failed", false, false)]
    public void PausedAndFinalizingRecordingsStillOwnTheirSession(string phase, bool active, bool busy)
    {
        var state = new RecordingState(phase, "test");
        Assert.Equal(active, state.Active); Assert.Equal(busy, state.Busy);
    }
}
