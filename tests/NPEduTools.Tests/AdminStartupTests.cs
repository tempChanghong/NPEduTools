using System.Xml.Linq;
using NPEduTools.ClassIsland.Admin;

namespace NPEduTools.Tests;

public class AdminStartupTests
{
    [Theory]
    [InlineData("Stopped", "Enabled", LaunchChoice.ScheduledTask)]
    [InlineData("Stopped", "Missing", LaunchChoice.Ordinary)]
    [InlineData("Stopped", "Disabled", LaunchChoice.Ordinary)]
    [InlineData("Stopped", "Conflict", LaunchChoice.Reject)]
    [InlineData("Stopped", "Unreadable", LaunchChoice.Reject)]
    [InlineData("Standard", "Enabled", LaunchChoice.OfferAdministratorRestart)]
    [InlineData("Standard", "Missing", LaunchChoice.Existing)]
    [InlineData("Standard", "Disabled", LaunchChoice.Existing)]
    [InlineData("Administrator", "Enabled", LaunchChoice.Existing)]
    [InlineData("Administrator", "Conflict", LaunchChoice.Existing)]
    [InlineData("Unknown", "Enabled", LaunchChoice.Reject)]
    [InlineData("Unknown", "Missing", LaunchChoice.Reject)]
    public void LaunchHonorsPrivilegePreferenceWithoutReplacingExistingInstances(string process, string task, LaunchChoice expected)
        => Assert.Equal(expected, ClassIslandLaunchPolicy.Choose(process, task));

    private const string Exe = @"C:\教室工具 & 教学\ClassIsland.exe";
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private static string? Resolve(string value) => value is "Teacher" or Sid ? Sid : value;
    private static bool Compatible(string xml) => TaskDefinitionPolicy.IsCompatible(xml, Exe, Sid, Resolve);

    [Fact]
    public void DefinitionEscapesPathAndMatchesInteractiveElevatedLogin()
    {
        string xml = TaskDefinitionPolicy.Create(Exe, Sid);
        Assert.True(Compatible(xml));
        var doc = XDocument.Parse(xml); var ns = TaskDefinitionPolicy.Ns;
        Assert.Equal(Exe, doc.Descendants(ns + "Command").Single().Value);
        Assert.Equal("PT0S", doc.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("false", doc.Descendants(ns + "StopIfGoingOnBatteries").Single().Value);
    }

    [Theory]
    [InlineData("Command", @"C:\Other\ClassIsland.exe")]
    [InlineData("WorkingDirectory", @"C:\Other")]
    [InlineData("UserId", "S-1-5-18")]
    [InlineData("LogonType", "ServiceAccount")]
    [InlineData("RunLevel", "LeastPrivilege")]
    public void RepointedOrForeignTasksAreNotOwned(string element, string value)
    {
        var doc = XDocument.Parse(TaskDefinitionPolicy.Create(Exe, Sid));
        doc.Descendants(TaskDefinitionPolicy.Ns + element).First().Value = value;
        Assert.False(Compatible(doc.ToString()));
    }

    [Fact]
    public void AdditionalActionsAndArgumentsAreNotOwned()
    {
        var doc = XDocument.Parse(TaskDefinitionPolicy.Create(Exe, Sid)); var ns = TaskDefinitionPolicy.Ns;
        doc.Descendants(ns + "Exec").Single().Add(new XElement(ns + "Arguments", "--import-v2 other"));
        Assert.False(Compatible(doc.ToString()));
        doc.Descendants(ns + "Arguments").Remove();
        doc.Descendants(ns + "Actions").Single().Add(new XElement(ns + "ComHandler"));
        Assert.False(Compatible(doc.ToString()));
    }

    [Fact]
    public void PluginAccountNamesAreResolvedButBlankLogonUserIsRejected()
    {
        var doc = XDocument.Parse(TaskDefinitionPolicy.Create(Exe, Sid)); var ns = TaskDefinitionPolicy.Ns;
        foreach (var user in doc.Descendants(ns + "UserId")) user.Value = "Teacher";
        Assert.True(Compatible(doc.ToString()));
        doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Remove();
        Assert.False(Compatible(doc.ToString()));
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<!DOCTYPE Task [<!ENTITY external SYSTEM 'file:///C:/secret'>]><Task>&external;</Task>")]
    public void MalformedAndExternalEntityDefinitionsAreRejected(string xml) => Assert.False(Compatible(xml));

    [Fact]
    public void SnapshotDetectsTaskAppearanceAndChangesDuringUac()
    {
        string xml = TaskDefinitionPolicy.Create(Exe, Sid);
        Assert.NotEqual(TaskDefinitionPolicy.Fingerprint(null), TaskDefinitionPolicy.Fingerprint(xml));
        Assert.NotEqual(TaskDefinitionPolicy.Fingerprint(xml), TaskDefinitionPolicy.Fingerprint(xml.Replace("HighestAvailable", "LeastPrivilege")));
    }
}
