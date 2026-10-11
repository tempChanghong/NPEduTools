using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed class StudyHomeworkTests
{
    private sealed class Clock : TimeProvider { public long Ms; public override long TimestampFrequency => 1000; public override long GetTimestamp() => Ms; }
    private static StudyHomeworkItem Work(string id, string text) => new(id, 1, "物理", "", text, [new("one", "行政一班")]);
    [Fact]
    public void Long_homework_bounds_each_measurement_to_a_page_instead_of_repeatedly_shaping_the_whole_body()
    {
        string content = string.Concat(Enumerable.Repeat("完成作业。\n",20000)); int largest = 0;
        var pages = StudyHomeworkPages.Build([Work("long",content)],(text,width) =>
        { largest=Math.Max(largest,text.Length);return Math.Ceiling(text.Length/(width/26))*30; });
        Assert.True(largest<=1024);Assert.Equal(content,string.Concat(pages.SelectMany(page=>page.Items).Select(part=>part.Text)));
    }
    [Fact]
    public void Measured_pages_preserve_long_multiline_text_and_graphemes()
    {
        string text = string.Concat(Enumerable.Repeat("🙂e\u0301实验题。\n", 300));
        var item = Work("long", text);
        var pages = StudyHomeworkPages.Build([Work("short", "题目 1"), item, item, Work("empty", " ")],
            (s, width) => Math.Max(1, (int)Math.Ceiling(s.Length / (width / 26))) * 30);
        Assert.True(pages.Length > 1);
        var parts = pages.SelectMany(page => page.Items).Where(part => part.Item.Id == "long").ToArray();
        Assert.Equal(text, string.Concat(parts.Select(part => part.Text)));
        Assert.All(parts, part => { Assert.False(char.IsLowSurrogate(part.Text[0])); Assert.NotEqual('\u0301', part.Text[0]); });
        Assert.Equal(1, pages.SelectMany(page => page.Items).Count(part => part.Item.Id == "short"));
        Assert.DoesNotContain(pages.SelectMany(page => page.Items), part => part.Item.Id == "empty");
    }
    [Fact]
    public void Hidden_overlay_stall_and_single_page_update_give_full_reading_interval()
    {
        var clock = new Clock(); var pager = new StudyHomeworkPager(clock); pager.Reset(3);
        Assert.False(pager.Advance(true)); clock.Ms = 59000; Assert.False(pager.Advance(true));
        pager.Advance(false); clock.Ms += 180000; Assert.False(pager.Advance(true));
        Assert.Equal(60, pager.RemainingSeconds); clock.Ms += 59999; Assert.False(pager.Advance(true));
        clock.Ms++; Assert.True(pager.Advance(true)); Assert.Equal(1, pager.Page);
        clock.Ms += 600000; Assert.True(pager.Advance(true)); Assert.Equal(2, pager.Page); Assert.Equal(60, pager.RemainingSeconds);
        pager.Reset(1); Assert.False(pager.Advance(true)); clock.Ms+=59000;Assert.False(pager.Advance(true));
        Assert.Equal(1,pager.RemainingSeconds);clock.Ms+=1000;Assert.True(pager.Advance(true,true));
        pager.Configure(90); Assert.Equal(90, pager.RemainingSeconds); Assert.Throws<ArgumentOutOfRangeException>(() => pager.Configure(0));
    }
    [Fact]
    public async Task Host_cache_is_complete_bounded_scoped_date_fenced_and_independent_of_capture_policy()
    {
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/study-device-cases.json")))![0]!["value"]!.DeepClone().AsObject();
        var clock = new Clock(); long sequence = 0; var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        var now = new DateTimeOffset(2026,10,12,18,58,0,TimeSpan.Zero);
        await using var service = new StudySettingsService(() => new(connection, bridge, ++sequence, 0, now.AddMilliseconds(clock.Ms), 0, "Advancing", "synthetic"), () => false, clock, false);
        service.BindStudy(config["identity"]!.ToJsonString()); service.ApplyStudy(config);
        clock.Ms += 500; service.ApplyStudy(config); clock.Ms += 500; service.ApplyStudy(config);
        var request = service.StudyHomeworkRequest(); Assert.NotNull(request); Assert.Equal("2026-10-12", request.Text("date"));
        var snapshot = new StudyHomeworkSnapshot(request.Text("version"), request.Text("date"), [Work("long", new string('作', 9000))]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Protocol.Json); Assert.True(bytes.Length > 16384);
        string id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        service.ApplyStudyHomework(JsonNode.Parse(bytes)!.AsObject(), bytes, id);
        var meta = service.Snapshot().Homework!; Assert.Equal("READY", meta.State);
        using var complete = new MemoryStream();
        for (int index = 0; index < meta.Chunks; index++)
        {
            var read = new HostRequest(1, Guid.NewGuid(), "study.homework", StudyHomework: new(id, index)); Assert.Null(Protocol.Validate(read));
            var response = service.ReadHomework(read); Assert.Null(response.Noise); Assert.Null(response.Study);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(response, Protocol.Json).Length < Protocol.MaxFrameBytes);
            complete.Write(Convert.FromBase64String(response.HomeworkChunk!.Data));
        }
        Assert.Equal(bytes, complete.ToArray()); Assert.Equal(snapshot.Items[0].Content, StudyHomeworkContract.Parse(complete.ToArray()).Items[0].Content);
        service.StudyHomeworkFailed("HOMEWORK_OFFLINE"); Assert.Equal("APPLIED", service.Snapshot().PolicyState); Assert.Equal("UNAVAILABLE", service.Snapshot().Homework!.State);
        service.ApplyStudyHomework(JsonNode.Parse(bytes)!.AsObject(), bytes, id);
        for (int tick=0;tick<120;tick++) { clock.Ms+=500;service.ApplyStudy(config); }
        Assert.Equal("APPLIED",service.Snapshot().PolicyState);Assert.Null(service.Snapshot().Homework!.SnapshotId);
        service.ApplyStudyHomework(JsonNode.Parse(bytes)!.AsObject(), bytes, id); config["version"] = new string('b', 64); service.ApplyStudy(config);
        Assert.Null(service.Snapshot().Homework!.SnapshotId);
        Assert.Throws<NpepException>(() => service.ApplyStudyHomework(JsonNode.Parse(bytes)!.AsObject(), bytes, id));
        service.BindStudy(null); Assert.Null(service.StudyHomeworkRequest()); Assert.Null(service.Snapshot().Homework!.SnapshotId);
    }
    [Fact]
    public void Contract_rejects_duplicate_ids_fields_invalid_calendar_and_local_scope_injection()
    {
        var snapshot = new StudyHomeworkSnapshot(new string('a',64), "2026-10-12", [Work("one", "题 1")]);
        var json = JsonSerializer.Serialize(snapshot, Protocol.Json);
        Assert.Throws<InvalidDataException>(() => StudyHomeworkContract.Parse(System.Text.Encoding.UTF8.GetBytes(json.Replace("\"date\":", "\"date\":\"2026-10-12\",\"date\":"))));
        Assert.Throws<InvalidDataException>(() => StudyHomeworkContract.Parse(JsonSerializer.SerializeToUtf8Bytes(snapshot with { Date = "2026-02-30" }, Protocol.Json)));
        Assert.Throws<InvalidDataException>(() => StudyHomeworkContract.Parse(JsonSerializer.SerializeToUtf8Bytes(snapshot with { Items = [snapshot.Items[0],snapshot.Items[0]] }, Protocol.Json)));
        Assert.NotNull(Protocol.Validate(new(1, Guid.NewGuid(), "study.status", StudyHomework: new(new string('a',64),0))));
        Assert.False(Protocol.NoiseInterruption(new(1, Guid.NewGuid(), "study.homework", StudyHomework: new(new string('a',64),0))));
    }
}
