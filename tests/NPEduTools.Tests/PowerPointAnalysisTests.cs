using System.Text.Json;
using NPEduTools.PowerPoint.Diagnostics;

namespace NPEduTools.Tests;

public sealed class PowerPointAnalysisTests
{
    private static string Frame(int sequence, long ms, string type, object data) => JsonSerializer.Serialize(new
    { schemaVersion = 1, sequence, monotonicMs = ms, type, data });
    private static string Start => Frame(1, 0, "environment", new { readOnly = true });
    private static object Summary(bool loss = false) => new { droppedInputs = loss ? 1 : 0, callbackErrors = 0, droppedSnapshots = 0, limitReached = false };
    private static ShowTarget Target => new("same-show", 1, 2, 3, 0, 0, 1920, 1080, 144, true, 256, 1, 0, 2, 1, 1);
    private static ShowSnapshot Show(ShowTarget? target = null) => new(DateTimeOffset.UtcNow, "Showing", "16.0", [target ?? Target]);
    private static TraceAnalysis Analyze(params string[] lines) => TraceAnalysis.Read(new StringReader(string.Join('\n', lines)));

    [Fact]
    public void EmptyInputWithValidShowDoesNotPassTouchValidation()
    {
        var result = Analyze(Start, Frame(2, 100, "show", Show()), Frame(3, 200, "summary", Summary()));
        Assert.True(result.StructurallyComplete);
        Assert.False(result.TargetTouchValidationPassed);
        Assert.Equal(0, result.TouchDowns);
        Assert.Contains("不能认定设备没有触摸屏", result.Markdown());
    }

    [Fact]
    public void SourceIsDerivedFromRawFlagsNotTheLoggedLabel()
    {
        var input = new InputSample(100, "Down", 10, 10, 0xff515780, 1, Target);
        var result = Analyze(Start, Frame(2, 100, "input", new { sample = input, source = "TouchMarked" }), Frame(3, 200, "summary", Summary()));
        Assert.Equal(1, result.Sources["Injected"]);
        Assert.Equal(0, result.TouchDowns);
    }

    [Fact]
    public void SameSlideAnimationIsSeparateFromSlideChange()
    {
        var result = Analyze(Start, Frame(2, 100, "show", Show()), Frame(3, 350, "show", Show(Target with { ClickIndex = 1 })),
            Frame(4, 600, "show", Show(Target with { SlideId = 257, SlideIndex = 2 })), Frame(5, 700, "summary", Summary()));
        Assert.Equal(1, result.AnimationChanges);
        Assert.Equal(1, result.SlideChanges);
        Assert.Contains("不能归因于某次触摸", result.Markdown());
    }

    [Theory]
    [InlineData(true, 350)]
    [InlineData(false, 2000)]
    public void ReconnectionOrLongSamplingGapDoesNotInventProgress(bool changeIdentity, int time)
    {
        var target = Target with { Id = changeIdentity ? "new-worker" : Target.Id, SlideId = 257 };
        var result = Analyze(Start, Frame(2, 100, "show", Show()), Frame(3, time, "show", Show(target)), Frame(4, time + 1, "summary", Summary()));
        Assert.Equal(0, result.SlideChanges);
        Assert.Equal(2, result.ContextBreaks);
    }

    [Theory]
    [InlineData("missing-summary")]
    [InlineData("sequence-gap")]
    [InlineData("malformed-line")]
    [InlineData("loss")]
    [InlineData("after-summary")]
    [InlineData("backwards-time")]
    public void InterruptedOrCorruptLogsCannotBeCalledComplete(string mode)
    {
        var lines = new List<string> { Start, Frame(2, 100, "show", Show()) };
        if (mode == "malformed-line") lines.Add("{\"truncated\":");
        if (mode != "missing-summary") lines.Add(Frame(mode == "sequence-gap" ? 9 : 3, mode == "backwards-time" ? 50 : 200, "summary", Summary(mode == "loss")));
        if (mode == "after-summary") lines.Add(Frame(4, 300, "show", Show()));
        var result = Analyze(lines.ToArray());
        Assert.False(result.StructurallyComplete);
        Assert.False(result.TargetTouchValidationPassed);
    }

    [Fact]
    public void CancelEventIsNotCountedAsMouseInput()
    {
        var result = Analyze(Start, Frame(2, 100, "input", new { sample = new InputSample(100, "Cancel", 0, 0, 0, 0, null) }),
            Frame(3, 200, "summary", Summary()));
        Assert.Equal(0, result.InputEvents);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public void FeaturesReportPresenceButNeverInferSafeBackground()
    {
        var features = new SlideFeatures(DateTimeOffset.UtcNow, "PartialTopLevel", 70, 1, 1, 2, 1, 1);
        var result = Analyze(Start, Frame(2, 100, "show", Show(Target with { Features = features })), Frame(3, 200, "summary", Summary()));
        Assert.True(result.ObservedActions && result.ObservedLinks && result.ObservedTriggers && result.ObservedMedia && result.ObservedGroups);
        Assert.True(result.IncompleteFeatureInventory);
        Assert.Contains("零计数不能证明", result.Markdown());
    }

    [Fact]
    public void UnknownFieldsAndOldTraceWithoutFeaturesRemainReadable()
    {
        var result = Analyze(Start, Frame(2, 100, "show", Show()), Frame(3, 200, "summary", Summary()));
        Assert.Equal(0, result.FeatureFrames);
        Assert.Contains("可能来自旧版工具", result.Markdown());
    }

    [Fact]
    public void OversizedLineIsRejectedBeforeJsonDeserialization()
        => Assert.Throws<InvalidDataException>(() => Analyze(new string('a', 65537)));

    [Fact]
    public void MalformedTypedPayloadDoesNotCrashWholeAnalysis()
    {
        var result = Analyze(Start, Frame(2, 100, "show", new { Status = "Showing", Targets = new object?[] { null } }),
            Frame(3, 200, "input", new { sample = new { Kind = 123 } }), Frame(4, 300, "summary", Summary()));
        Assert.Equal(2, result.InvalidLines);
        Assert.False(result.StructurallyComplete);
    }

    [Fact]
    public void AutomaticReportCanReadFlushedLogBeforeWriterClosesAndNeverOverwrites()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NPEduTools.PowerPointReports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "trace.jsonl");
        var ownedFiles = new List<string> { path };
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream) { AutoFlush = true })
            {
                writer.WriteLine(Start);
                writer.WriteLine(Frame(2, 100, "summary", Summary()));
                long length = stream.Length;
                string first = TraceAnalysis.WriteReport(path);
                ownedFiles.Add(first);
                string second = TraceAnalysis.WriteReport(path);
                ownedFiles.Add(second);
                Assert.NotEqual(first, second);
                Assert.Equal(length, stream.Length);
                Assert.Contains("日志结构完整", File.ReadAllText(first));
            }
        }
        finally
        {
            foreach (string file in ownedFiles) File.Delete(file);
            Directory.Delete(directory); // Only the empty, test-created directory; no recursive cleanup.
        }
    }
}
