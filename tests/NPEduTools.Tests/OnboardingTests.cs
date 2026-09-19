using NPEduTools.App;

namespace NPEduTools.Tests;

public sealed class OnboardingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NPEduTools.Tests", Guid.NewGuid().ToString("N"));
    private string PathFor => Path.Combine(_root, "progress.json");

    [Theory]
    [InlineData(OnboardingFeatures.None, 3)]
    [InlineData(OnboardingFeatures.Shortcuts | OnboardingFeatures.Touch, 3)]
    [InlineData(OnboardingFeatures.Recording, 4)]
    [InlineData(OnboardingFeatures.Automatic, 5)]
    public void OnlyRelevantStepsAreShown(OnboardingFeatures features, int count)
    {
        var state = new OnboardingState(Features: features);
        Assert.Equal(count, state.Steps.Length);
        Assert.Equal(features.HasFlag(OnboardingFeatures.Automatic), state.Steps.Contains("classisland"));
    }

    [Fact]
    public void DeferredProgressSurvivesRestartAndBackNavigation()
    {
        var store = new OnboardingStore(PathFor);
        Assert.Null(store.Read());
        var state = new OnboardingState(Features: OnboardingFeatures.Automatic).Advance(false).Advance(true);
        store.Save(state with { Deferred = true });
        var restored = store.Read()!;
        Assert.Equal("classisland", restored.Step);
        Assert.Contains("preferences", restored.Skipped!);
        Assert.False(restored.ShouldShow(false));
        Assert.Equal("preferences", restored.Back().Step);
        Assert.False(restored.Advance(false).Deferred);
    }

    [Fact]
    public void CompletionPreservesExplicitSkippedItems()
    {
        var state = new OnboardingState(Features: OnboardingFeatures.Automatic);
        while (state.Step != "review") state = state.Advance(state.Step is "classisland" or "recording");
        state = state.Advance(false);
        Assert.True(state.Completed);
        Assert.False(state.ShouldShow(false));
        Assert.Equal(new[] { "classisland", "recording" }, state.Skipped);
        var store = new OnboardingStore(PathFor); store.Save(state);
        Assert.True(store.Read()!.Completed);
    }

    [Fact]
    public void LoginNeverOpensWizardAndPurposeChangeInvalidatesConditionalChecks()
    {
        Assert.False(new OnboardingState().ShouldShow(true));
        Assert.True(new OnboardingState().ShouldShow(false));
        var state = new OnboardingState(Features: OnboardingFeatures.Automatic,
            Reviewed: ["preferences", "classisland", "recording"], Skipped: ["welcome"]);
        var changed = state.Select(OnboardingFeatures.Shortcuts);
        Assert.Equal(new[] { "preferences" }, changed.Reviewed);
        Assert.Empty(changed.Skipped!);
        Assert.Equal("welcome", changed.Step);
    }

    [Theory]
    [InlineData("{\"Version\":999}")]
    [InlineData("{\"Features\":32}")]
    [InlineData("{\"Step\":\"classisland\"}")]
    [InlineData("{\"Completed\":true}")]
    [InlineData("{\"Reviewed\":[\"preferences\"],\"Skipped\":[\"preferences\"]}")]
    [InlineData("null")]
    public void InvalidProgressIsRejectedAndOriginalIsPreserved(string json)
    {
        Directory.CreateDirectory(_root); File.WriteAllText(PathFor, json);
        Assert.Throws<InvalidDataException>(() => new OnboardingStore(PathFor).Read());
        Assert.Equal(json, File.ReadAllText(PathFor));
    }

    [Fact]
    public void FailedSaveKeepsOriginalProgressAndCleansTemporaryFile()
    {
        var store = new OnboardingStore(PathFor); store.Save(new());
        using (var file = new FileStream(PathFor, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => store.Save(new(Step: "preferences")));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("welcome", store.Read()!.Step);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void OversizedFileIsRejectedAndEndpointsHaveSeparatePaths()
    {
        Directory.CreateDirectory(_root); File.WriteAllText(PathFor, new string(' ', 16385));
        Assert.Throws<InvalidDataException>(() => new OnboardingStore(PathFor).Read());
        Assert.NotEqual(OnboardingStore.PathFor("a"), OnboardingStore.PathFor("b"));
    }

    [Fact]
    public void ExistingPreferencesAreDetectedWithoutModification()
    {
        string endpoint = "NPEduTools.Test.onboarding." + Guid.NewGuid().ToString("N");
        string path = StartupPreferencesStore.PathFor(endpoint);
        Assert.False(OnboardingStore.HasExistingSettings(endpoint));
        try
        {
            new StartupPreferencesStore(path).Save(new(EdgeOnlyAtLogin: false, EnableTouchOnLaunch: true));
            string original = File.ReadAllText(path);
            Assert.True(OnboardingStore.HasExistingSettings(endpoint));
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
