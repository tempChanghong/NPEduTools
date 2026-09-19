using System.IO;
using System.Text.Json;

namespace NPEduTools.App;

[Flags]
public enum OnboardingFeatures { None = 0, Shortcuts = 1, Touch = 2, Recording = 4, Automatic = 8 }

public sealed record OnboardingState(int Version = 1, OnboardingFeatures Features = OnboardingFeatures.Shortcuts,
    string Step = "welcome", string[]? Reviewed = null, string[]? Skipped = null, bool Completed = false, bool Deferred = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] Steps => StepsFor(Features);
    public static string[] StepsFor(OnboardingFeatures features) => ["welcome", "preferences",
        .. features.HasFlag(OnboardingFeatures.Automatic) ? new[] { "classisland" } : [],
        .. (features & (OnboardingFeatures.Recording | OnboardingFeatures.Automatic)) != 0 ? new[] { "recording" } : [], "review"];

    public OnboardingState Select(OnboardingFeatures features)
    {
        var steps = StepsFor(features);
        // A changed purpose requires checking its conditional steps again.
        return this with { Features = features, Step = "welcome", Completed = false,
            Reviewed = (Reviewed ?? []).Where(s => steps.Contains(s) && s == "preferences").ToArray(), Skipped = [] };
    }

    public OnboardingState Advance(bool skip)
    {
        if (Step is "review") return this with { Completed = true, Deferred = false };
        return this with { Step = Steps[Array.IndexOf(Steps, Step) + 1], Deferred = false,
            Reviewed = (Reviewed ?? []).Where(s => s != Step).Concat(skip ? [] : new[] { Step }).ToArray(),
            Skipped = (Skipped ?? []).Where(s => s != Step).Concat(skip ? new[] { Step } : []).ToArray() };
    }
    public OnboardingState Back() => this with { Step = Steps[Math.Max(0, Array.IndexOf(Steps, Step) - 1)], Completed = false };
    public bool ShouldShow(bool atLogin) => !atLogin && !Completed && !Deferred;
}

public sealed class OnboardingStore(string path)
{
    public static string PathFor(string endpoint) => StartupPreferencesStore.PathFor(endpoint)
        .Replace(".startup.json", ".onboarding.json", StringComparison.Ordinal);

    public static bool HasExistingSettings(string endpoint)
    {
        string prefix = StartupPreferencesStore.PathFor(endpoint).Replace(".startup.json", "", StringComparison.Ordinal);
        return new[] { ".json", ".startup.json", ".shortcuts.json", ".recording.json", ".recording-plans.json", ".recording-preview.json" }
            .Any(suffix => File.Exists(prefix + suffix));
    }

    public OnboardingState? Read()
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("引导记录过大。");
        var state = JsonSerializer.Deserialize<OnboardingState>(File.ReadAllText(path));
        Validate(state);
        return state;
    }

    public void Save(OnboardingState state)
    {
        Validate(state);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(OnboardingState? state)
    {
        if (state is null || state.Version != 1 || ((int)state.Features & ~15) != 0 || !state.Steps.Contains(state.Step) ||
            state.Completed && state.Step != "review" ||
            (state.Reviewed ?? []).Concat(state.Skipped ?? []).Any(s => !state.Steps.Contains(s) || s == "review") ||
            (state.Reviewed ?? []).Intersect(state.Skipped ?? []).Any())
            throw new InvalidDataException("引导记录无效或版本不受支持，原文件已保留。");
    }
}
