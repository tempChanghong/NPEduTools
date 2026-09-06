using System.Runtime.Versioning;
using Microsoft.Win32;
using NPEduTools.App;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class AppStartupTests
{
    [Fact]
    public void PreferencesDefaultToNoAutoTouchAndPersistIndependentlyOfPlacement()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NPEduTools.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "startup.json");
        try
        {
            var store = new StartupPreferencesStore(path);
            Assert.Equal(new StartupPreferences(), store.Read());
            store.Save(new(EdgeOnlyAtLogin: false, EnableTouchOnLaunch: true));
            Assert.Equal(new StartupPreferences(EdgeOnlyAtLogin: false, EnableTouchOnLaunch: true), new StartupPreferencesStore(path).Read());
            File.WriteAllText(path, "{broken");
            Assert.Throws<System.Text.Json.JsonException>(() => store.Read());
            Assert.Equal("{broken", File.ReadAllText(path));
            File.WriteAllText(path, "{\"Version\":2,\"EnableTouchOnLaunch\":true}");
            Assert.Throws<InvalidDataException>(() => store.Read());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void RegistrationRoundTripsAndRefusesOtherTargets()
    {
        string subkey = @"Software\NPEduTools.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subkey, true);
            var startup = new LoginStartup(key, @"D:\Classroom Tools\NPEduTools.App.exe");
            key.SetValue("Unrelated", "untouched");
            Assert.Equal("Missing", startup.ReadState());
            startup.SetEnabled(true);
            Assert.Equal("\"" + @"D:\Classroom Tools\NPEduTools.App.exe" + "\" --startup", key.GetValue(LoginStartup.ValueName));
            Assert.Equal("Registered", startup.ReadState());
            startup.SetEnabled(true);
            startup.SetEnabled(false);
            startup.SetEnabled(false);
            Assert.Equal("Missing", startup.ReadState());
            Assert.Equal("untouched", key.GetValue("Unrelated"));
            key.SetValue(LoginStartup.ValueName, "another program");
            Assert.Equal("Conflict", startup.ReadState());
            Assert.Throws<InvalidOperationException>(() => startup.SetEnabled(true));
            Assert.Throws<InvalidOperationException>(() => startup.SetEnabled(false));
            Assert.Equal("another program", key.GetValue(LoginStartup.ValueName));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(subkey, false); }
    }

    [Theory]
    [InlineData("relative\\NPEduTools.App.exe")]
    [InlineData("D:\\bad\"name\\NPEduTools.App.exe")]
    [InlineData("D:\\other.exe")]
    public void InvalidStartupTargetsAreRejected(string path) => Assert.Throws<InvalidDataException>(() => LoginStartup.BuildCommand(path));

    [Fact]
    public void LoginFlagCanBeCombinedWithIsolatedEndpoints()
    {
        Assert.Equal(new AppLaunchOptions("default", null, false), AppLaunchOptions.Parse([], "default"));
        Assert.Equal(new AppLaunchOptions("private", "peer", true),
            AppLaunchOptions.Parse(["--pipe", "private", "--startup", "--classisland-pipe", "peer"], "default"));
    }

    [Theory]
    [InlineData("--startup --startup")]
    [InlineData("--pipe --startup")]
    [InlineData("--unknown")]
    [InlineData("--pipe invalid/name")]
    [InlineData("--pipe a --pipe b")]
    public void MalformedArgumentsCannotStartAnotherEndpoint(string arguments) =>
        Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(arguments.Split(' '), "default"));
}
