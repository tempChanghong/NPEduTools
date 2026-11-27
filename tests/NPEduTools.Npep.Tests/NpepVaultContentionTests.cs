using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class NpepVaultContentionTests
{
    [Fact]
    public async Task Credential_save_recovers_when_a_short_file_reader_releases_the_original()
    {
        using var directory = new TestDirectory();
        using var vault = new NpepVault(directory.Path);
        vault.Save(new() { ["revision"] = 1 });
        string path = Path.Combine(directory.Path, "npep.credentials.dpapi");
        Task save;
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            save = Task.Run(() =>
            {
                started.SetResult();
                vault.Save(new() { ["revision"] = 2 });
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.WhenAny(save, Task.Delay(120));
        }
        await save.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["revision"] = 2 }, vault.Load()));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task An_occupied_credential_file_retains_its_original_bytes_when_save_cannot_finish()
    {
        using var directory = new TestDirectory();
        using var vault = new NpepVault(directory.Path);
        vault.Save(new() { ["revision"] = 1 });
        string path = Path.Combine(directory.Path, "npep.credentials.dpapi");
        byte[] original = File.ReadAllBytes(path);
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(async () =>
                await Task.Run(() => vault.Save(new() { ["revision"] = 2 })).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["revision"] = 1 }, vault.Load()));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
}
