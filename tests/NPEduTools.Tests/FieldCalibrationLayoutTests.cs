using System.Text.Json.Nodes;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class FieldCalibrationLayoutTests
{
    [Fact]
    public void ConnectionSettingsUseAdjustedSchoolClockAndExpireItWithoutWindowsFallback()
    {
        var tracker = new NPEduTools.Core.SchoolClockTracker();
        var adjusted = new DateTimeOffset(2026, 10, 12, 19, 0, 10, TimeSpan.Zero);
        tracker.Accept(new(Guid.NewGuid(), Guid.NewGuid(), 1, 1, adjusted, 0, "Advancing", "fixture adjusted time"), TimeSpan.Zero, 0);
        Assert.Equal("2026-10-12 19:00:10", ClassIslandClockPresentation.From(tracker.Read(TimeSpan.Zero)).Clock);
        Assert.Equal("--:--:--", ClassIslandClockPresentation.From(tracker.Read(TimeSpan.FromSeconds(4))).Clock);
        tracker.Unavailable("disconnected", TimeSpan.FromSeconds(4));
        Assert.Contains("不会用 Windows", ClassIslandClockPresentation.From(tracker.Read(TimeSpan.FromSeconds(4))).Detail);
    }
    [Fact]
    public async Task NodeSchoolCredentialVerifiesWithoutSensorsOrAnExitGrant()
    {
        // Produced with the KV's node:crypto pbkdf2Sync, UTF-8, SHA-256, 210000 iterations.
        const string secret = "学校测试口令-2026";
        string folder = Path.Combine(Path.GetTempPath(), "field-calibration-" + Guid.NewGuid());
        try
        {
            var store = new NoiseManagementStore(folder);
            Assert.Null(store.Configure(null, "obsolete-local-password", false));
            Assert.Null(store.BindSchool("school-class"));
            var credential = new JsonObject { ["revision"] = 3L, ["verifier"] = new JsonObject {
                ["iterations"] = 210000, ["salt"] = "AAECAwQFBgcICQoLDA0ODw==", ["hash"] = "sWANXYOAdHSuca7HelJIzDtoGikNFQ9egGQcFgqpNAo=" } };
            Assert.Null(store.ApplySchool("school-class", credential));
            await using var noise = new NoiseService(_ => throw new Exception("must not open microphone"),
                () => throw new Exception("must not enumerate microphones"), management: store);
            HostResponse Verify(string value) => noise.ManagementHandle(new(1, Guid.NewGuid(), "noise.management.command", NoiseManagement: new("verify", value)));
            var result = Verify(secret);
            Assert.Equal("Succeeded", result.Outcome); Assert.Null(result.NoiseProtection!.Ticket);
            Assert.Equal("School", result.NoiseProtection.ManagementSource); Assert.Equal(3, result.NoiseProtection.CredentialRevision);
            Assert.Equal("MANAGEMENT_SECRET_INVALID", Verify("obsolete-local-password").ErrorCode);
            Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(result, Protocol.Json));
            for (int i = 0; i < 4; i++) Verify("wrong-password");
            Assert.Equal("MANAGEMENT_RATE_LIMITED", Verify(secret).ErrorCode);
            Assert.Contains("一分钟", CalibrationAccess.Message("MANAGEMENT_RATE_LIMITED", "", result.NoiseProtection));
            // Reloading uses the school latch, not the retired local password.
            var reloaded = new NoiseManagementStore(folder);
            Assert.Null(reloaded.VerifyCalibration(secret));
            Assert.Equal("MANAGEMENT_SECRET_INVALID", reloaded.VerifyCalibration("obsolete-local-password"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(1280, 680)] // 2560x1440, Windows 200%, taskbar/work area excluded.
    [InlineData(1280, 720)]
    [InlineData(1707, 920)]
    [InlineData(2560, 1400)]
    public void DensityFitsWindowsScaledWorkArea(double width, double height)
    {
        double scale = DesktopDensity.Resolve(0, width, height);
        Assert.InRange(scale, .5, .9);
        Assert.True(1040 * scale <= width - 32); Assert.True(760 * scale <= height - 32);
        Assert.Equal(.8, DesktopDensity.Resolve(.8, 2560, 1400));
        Assert.Equal(1, DesktopDensity.Resolve(1, 2560, 1400));
    }

    [Fact]
    public void BridgeFilesDisabledAndFreshRuntimeAreDistinct()
    {
        string root = Path.Combine(Path.GetTempPath(), "bridge-inspection-" + Guid.NewGuid());
        string plugin = Path.Combine(root, "data", "Plugins", "npedutools.recordingbridge");
        string executable = Path.Combine(root, "ClassIsland.exe");
        try
        {
            Assert.Contains("未找到", ClassIslandBridgeInspection.Describe(executable, null, installedDataRoot: root));
            Directory.CreateDirectory(plugin);
            File.WriteAllText(Path.Combine(plugin, "manifest.yml"), "id: npedutools.recordingbridge\nversion: 0.2.0.0\n");
            Assert.Contains("文件不完整", ClassIslandBridgeInspection.Describe(executable, null, installedDataRoot: root));
            File.WriteAllText(Path.Combine(plugin, "NPEduTools.ClassIsland.Bridge.dll"), "fixture");
            Assert.Contains("运行连接未确认", ClassIslandBridgeInspection.Describe(executable, null, installedDataRoot: root));
            File.WriteAllText(Path.Combine(plugin, ".disabled"), "");
            Assert.Contains("已禁用", ClassIslandBridgeInspection.Describe(executable, null, installedDataRoot: root));
            var frame = new SchoolClockFrame(Guid.NewGuid(), Guid.NewGuid(), 2, 0, DateTimeOffset.UtcNow, 50, "Advancing", "fixture");
            Assert.Contains("已加载并连接", ClassIslandBridgeInspection.Describe(executable, frame, installedDataRoot: root));
            Assert.DoesNotContain("已加载并连接", ClassIslandBridgeInspection.Describe(executable, frame with { AgeMs = 3000 }, installedDataRoot: root));
            Assert.DoesNotContain("已加载并连接", ClassIslandBridgeInspection.Describe(executable, frame, 3100, root));
            Assert.DoesNotContain("已加载并连接", ClassIslandBridgeInspection.Describe(executable, frame with { BridgeInstanceId = Guid.Empty }, installedDataRoot: root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
