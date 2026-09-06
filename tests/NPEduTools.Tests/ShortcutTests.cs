using NPEduTools.App;

namespace NPEduTools.Tests;

public sealed class ShortcutTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NPEduTools.Tests", Guid.NewGuid().ToString("N"));
    private static ShortcutEntry Link(string name = "教学平台", string target = "https://example.com/course") => new(Guid.NewGuid(), name, "url", target);

    [Fact]
    public void RoundTripPreservesIdsNamesAndOrderIncludingMissingFiles()
    {
        var catalog = new ShortcutCatalog(Path.Combine(_directory, "items.json"));
        Assert.Empty(catalog.Read());
        var file = new ShortcutEntry(Guid.NewGuid(), "课件", "file", Path.Combine(_directory, "lesson.pptx"));
        var link = Link();
        catalog.Save([file, link]);
        Assert.Equal([file, link], catalog.Read());
        catalog.Save([link with { Name = "教学资源" }, file]);
        var reloaded = new ShortcutCatalog(Path.Combine(_directory, "items.json")).Read();
        Assert.Equal(link.Id, reloaded[0].Id);
        Assert.Equal("教学资源", reloaded[0].Name);
        Assert.Equal(file, reloaded[1]);
    }

    [Fact]
    public void BrokenOrNewerConfigurationIsPreserved()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "items.json");
        var catalog = new ShortcutCatalog(path);
        File.WriteAllText(path, "{broken");
        Assert.Throws<System.Text.Json.JsonException>(() => catalog.Read());
        Assert.Equal("{broken", File.ReadAllText(path));
        File.WriteAllText(path, "{\"Version\":9,\"Items\":[]}");
        Assert.Throws<InvalidDataException>(() => catalog.Read());
        Assert.Contains("9", File.ReadAllText(path));
    }

    [Fact]
    public void InvalidMutationDoesNotReplaceSavedCatalog()
    {
        var catalog = new ShortcutCatalog(Path.Combine(_directory, "items.json"));
        var entry = Link();
        catalog.Save([entry]);
        Assert.Throws<InvalidDataException>(() => catalog.Save([entry, entry]));
        Assert.Throws<InvalidDataException>(() => catalog.Save(Enumerable.Range(0, 25).Select(_ => Link()).ToArray()));
        Assert.Equal([entry], catalog.Read());
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("ms-settings:startupapps")]
    [InlineData("https://")]
    [InlineData("example.com")]
    public void WebsiteItemsAcceptOnlyExplicitHttpUrls(string target) =>
        Assert.Throws<InvalidDataException>(() => ShortcutCatalog.Normalize(Link(target: target)));

    [Fact]
    public void UrlLaunchDoesNotInterpretShellMetacharactersAsArguments()
    {
        const string target = "https://example.com/?q=lesson&next=%22test%22";
        var start = ShortcutCatalog.PrepareLaunch(Link(target: target));
        Assert.Equal(target, start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Equal("", start.Arguments);
        Assert.Empty(start.ArgumentList);
    }

    [Fact]
    public void FilesUseTheirExactPathAndMissingFilesFailBeforeShellDispatch()
    {
        Directory.CreateDirectory(_directory);
        string file = Path.Combine(_directory, "lesson & notes.txt");
        var entry = new ShortcutEntry(Guid.NewGuid(), "课堂笔记", "file", file);
        Assert.Throws<FileNotFoundException>(() => ShortcutCatalog.PrepareLaunch(entry));
        File.WriteAllText(file, "lesson");
        var start = ShortcutCatalog.PrepareLaunch(entry);
        Assert.Equal(file, start.FileName);
        Assert.Equal(_directory, start.WorkingDirectory);
        Assert.Equal("", start.Arguments);
        Assert.Equal("open", start.Verb);
    }

    [Theory]
    [InlineData("app", "relative.exe")]
    [InlineData("app", "C:\\test.cmd")]
    [InlineData("unknown", "https://example.com")]
    [InlineData("file", "C:\\file.txt\" & echo hi")]
    public void InvalidKindsOrPathsCannotBeLaunched(string kind, string target) =>
        Assert.Throws<InvalidDataException>(() => ShortcutCatalog.Normalize(new(Guid.NewGuid(), "test", kind, target)));

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
