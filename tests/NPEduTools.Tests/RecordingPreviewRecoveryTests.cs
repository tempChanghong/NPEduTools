using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class RecordingPreviewRecoveryTests
{
    [Theory]
    [InlineData("Message")]
    [InlineData("Action")]
    [InlineData("Subject")]
    public void InvalidEventTextIsRejectedAndOriginalFileIsPreserved(string field)
    {
        string directory = Path.Combine(Path.GetTempPath(), "NPEduTools.Test.preview-event-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "preview.json");
        Directory.CreateDirectory(directory);
        var entry = new PreviewEvent(null, "应开始", "语文", "隔离测试");
        entry = field switch { "Message" => entry with { Message = null! }, "Action" => entry with { Action = null! }, _ => entry with { Subject = null! } };
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(new PreviewState(2, new(), [entry], []));
        try
        {
            File.WriteAllBytes(path, original);
            Assert.Throws<InvalidDataException>(() => new RecordingPreviewStore(path).Read());
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void UnknownSchoolTimeAndEmptyTextRemainReadable()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NPEduTools.Test.preview-valid-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "preview.json");
        Directory.CreateDirectory(directory);
        var state = new PreviewState(2, new(), [new(null, "应停止", "语文", ""), new(null, "跳过", "", "学校时间未知")], []);
        try
        {
            var store = new RecordingPreviewStore(path);
            store.Save(state);
            Assert.Equal(state.Events, store.Read().Events);
        }
        finally { Directory.Delete(directory, true); }
    }
}
