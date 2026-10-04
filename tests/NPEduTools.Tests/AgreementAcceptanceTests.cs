using System.Text.Json;
using NPEduTools.App;

namespace NPEduTools.Tests;

public sealed class AgreementAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NPEduTools.Tests", Guid.NewGuid().ToString("N"));
    private string PathFor => Path.Combine(_root, "agreements.json");
    private static readonly IReadOnlyList<AgreementDefinition> Definitions = AgreementCatalog.Load();
    private static AgreementAcceptanceRecord Current => AgreementAcceptanceRecord.Create(Definitions, Definitions.Select(a => a.Id), DateTimeOffset.UtcNow);

    [Fact]
    public void EmbeddedTextsAreFinalAndGplIsAvailableWithoutFourthConsent()
    {
        Assert.Equal(3, Definitions.Count);
        foreach (var definition in Definitions)
        {
            Assert.Contains("生效日期：2026年10月4日", definition.Text);
            Assert.DoesNotContain("审核稿", definition.Text);
            Assert.DoesNotContain("待补充", definition.Text);
            Assert.DoesNotContain("联系邮箱", definition.Text);
            Assert.Contains("https://novark.ink", definition.Text);
        }
        Assert.Contains("GNU GENERAL PUBLIC LICENSE", AgreementCatalog.License.Text);
        Assert.DoesNotContain(Definitions, d => d.Id == AgreementCatalog.License.Id);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void PartialSelectionCannotBeSavedAsAcceptance(int count) => Assert.Throws<InvalidDataException>(() =>
        AgreementAcceptanceRecord.Create(Definitions, Definitions.Take(count).Select(a => a.Id), DateTimeOffset.UtcNow));

    [Fact]
    public void ConsentSurvivesRestartAndContentChangeRequiresNewConfirmation()
    {
        var store = new AgreementAcceptanceStore(PathFor);
        Assert.Null(store.Read());
        store.Save(Current);
        var saved = store.Read()!;
        Assert.True(saved.IsCurrent(Definitions));
        Assert.All(saved.Agreements, a => Assert.Equal(TimeSpan.Zero, a.AcceptedAtUtc.Offset));
        var changed = Definitions.Select(a => a.Id == "npep-privacy" ? a with { Text = a.Text + "\n变更" } : a).ToArray();
        Assert.False(saved.IsCurrent(changed));
        Assert.True(saved.HasAccepted(changed[0]));
        Assert.False(saved.HasAccepted(changed[2]));
        Assert.NotEqual(AgreementAcceptanceStore.PathFor("a"), AgreementAcceptanceStore.PathFor("b"));
    }

    [Fact]
    public void OldOnboardingCompletionDoesNotGiveConsent()
    {
        var store = new OnboardingStore(Path.Combine(_root, "onboarding.json"));
        store.Save(new(Step: "review", Completed: true));
        Assert.Null(new AgreementAcceptanceStore(PathFor).Read());
    }

    [Theory]
    [InlineData("null")] [InlineData("{\"SchemaVersion\":999,\"Agreements\":[]}")] [InlineData("broken json")]
    public void InvalidEvidenceIsPreservedAndExplicitReplacementIsBackedUp(string json)
    {
        Directory.CreateDirectory(_root); File.WriteAllText(PathFor, json);
        Assert.NotNull(Record.Exception(() => new AgreementAcceptanceStore(PathFor).Read()));
        Assert.Equal(json, File.ReadAllText(PathFor));
        new AgreementAcceptanceStore(PathFor).Save(Current);
        Assert.Equal(json, File.ReadAllText(Directory.GetFiles(_root, "*.invalid-*").Single()));
        Assert.True(new AgreementAcceptanceStore(PathFor).Read()!.IsCurrent(Definitions));
    }

    [Fact]
    public void DuplicateOrUnknownAgreementsAndMissingTimestampsAreRejected()
    {
        var record = Current;
        var store = new AgreementAcceptanceStore(PathFor);
        Assert.Throws<InvalidDataException>(() => store.Save(record with { Agreements = [record.Agreements[0], record.Agreements[0]] }));
        Assert.Throws<InvalidDataException>(() => store.Save(record with { Agreements = [record.Agreements[0] with { Id = "anything" }] }));
        Assert.Throws<InvalidDataException>(() => store.Save(record with { Agreements = [record.Agreements[0] with { AcceptedAtUtc = default }] }));
    }

    [Fact]
    public void FailedReplacementKeepsOriginalAndDoesNotLeaveTempFiles()
    {
        var store = new AgreementAcceptanceStore(PathFor); store.Save(Current);
        string before = File.ReadAllText(PathFor);
        using (var locked = new FileStream(PathFor, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.NotNull(Record.Exception(() => store.Save(Current)));
        Assert.Equal(before, File.ReadAllText(PathFor));
        Assert.Single(Directory.GetFiles(_root));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
