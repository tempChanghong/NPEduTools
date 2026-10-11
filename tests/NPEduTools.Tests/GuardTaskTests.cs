using System.Runtime.Versioning;
using System.Xml.Linq;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Tests;

[SupportedOSPlatform("windows")]
public sealed class GuardTaskTests
{
    [Theory]
    [InlineData("Guard")] [InlineData("Login")] [InlineData("App")] [InlineData("Host")]
    public void Launch_definitions_use_current_user_and_only_the_declared_bundle(string role)
    {
        var tasks = new GuardTasks(@"C:\NPEduTools test & 中文", "NPEduTools.Test.Guard.definition", "ClassIsland.Test");
        string xml = tasks.Definition(role, false);
        Assert.True(tasks.OwnedDefinition(xml, role));
        Assert.False(new GuardTasks(@"C:\Another-bundle", "NPEduTools.Test.Guard.definition", "ClassIsland.Test").OwnedDefinition(xml, role));
        var document = XDocument.Parse(xml); var ns = document.Root!.Name.Namespace;
        Assert.Equal("InteractiveToken", document.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("IgnoreNew", document.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("PT0S", document.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        if (role is "Guard" or "Login") Assert.All(document.Descendants(ns + "Interval"), interval => Assert.Equal("PT1M", interval.Value));
        if (role == "Guard") Assert.NotNull(document.Descendants(ns + "TimeTrigger").Single().Element(ns + "Repetition"));
        document.Descendants(ns + "Arguments").Single().Value += " --unexpected";
        Assert.False(tasks.OwnedDefinition(document.ToString(), role));
        Assert.False(tasks.OwnedDefinition("<broken", role));
    }
    [Fact]
    public void Default_tasks_resolve_session_number_at_launch_instead_of_persisting_it()
    {
        var tasks = new GuardTasks(@"C:\NPEduTools", PipeEndpoint.DefaultName, null);
        string guard = tasks.Definition("Guard"); string host = tasks.Definition("Host");
        Assert.Contains("--default-pipe", guard); Assert.DoesNotContain(PipeEndpoint.DefaultName, guard);
        Assert.DoesNotContain("--pipe", host); Assert.True(tasks.OwnedDefinition(host, "Host"));
    }
    [Fact]
    public void Invalid_endpoint_cannot_become_extra_command_line_arguments()
    {
        var tasks = new GuardTasks(@"C:\NPEduTools", "x\" --maintenance", null);
        Assert.Throws<ArgumentException>(() => tasks.Definition("Guard"));
    }
}
