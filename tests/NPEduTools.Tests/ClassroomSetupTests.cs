using NPEduTools.App;
using NPEduTools.ClassIsland.Admin;
using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed class ClassroomSetupTests
{
    private static readonly ExamAwareStatus Connected = new("C:/ExamAware.exe", 1, "Connected", "已连接", "1.5.2", false, true, 12345, CanSetAutoStart: true);
    private sealed class Fixture
    {
        public string? CiPath = "C:/ClassIsland.exe";
        public ExamAwareStatus Ea = Connected;
        public string TaskState = "Enabled";
        public bool ReadFails, InspectFails, Exists = true;
        public string? StorageWarning;
        public List<string> Reads = [];
        public int Inspections;
        public ClassroomSetupCheck Check => new((capability, token) =>
        {
            token.ThrowIfCancellationRequested(); Reads.Add(capability);
            if (capability == "classisland.config.get" && ReadFails) throw new IOException();
            return Task.FromResult(new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "",
                Launch: capability == "classisland.config.get" ? new(new(1, CiPath), null, StorageWarning) : null,
                ExamAware: capability == "examaware.status" ? Ea : null));
        }, path =>
        {
            Inspections++;
            if (InspectFails) throw new IOException();
            return Task.FromResult(new AdminResult("Succeeded", "", new(TaskState, "待核实", null, "Stopped", "", false)));
        }, path => Exists);
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Disabled")]
    public async Task Matching_enabled_or_disabled_task_is_configured(string state)
    {
        var fixture = new Fixture { TaskState = state };
        var report = await fixture.Check.RunAsync(default);
        Assert.True(report.AllReady); Assert.True(report.CanProceed);
        Assert.Equal(4, report.Items.Length);
        Assert.Equal(new[] { "classisland.config.get", "examaware.status" }, fixture.Reads);
        Assert.Equal(1, fixture.Inspections);
    }

    [Fact]
    public async Task All_missing_paths_are_reported_without_inspecting_a_task()
    {
        var fixture = new Fixture { CiPath = null, Ea = Connected with { ExecutablePath = null, BridgeState = "Disconnected" } };
        var report = await fixture.Check.RunAsync(default);
        Assert.False(report.CanProceed); Assert.Equal(4, report.Items.Length);
        Assert.Equal("待配置", report.Items[0].State); Assert.Equal("待配置", report.Items[2].State);
        Assert.Equal(0, fixture.Inspections);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Conflict")]
    [InlineData("Unknown")]
    public async Task Unsafe_or_unknown_task_blocks_switch_without_hiding_bridge(string state)
    {
        var report = await new Fixture { TaskState = state }.Check.RunAsync(default);
        Assert.False(report.CanProceed); Assert.True(report.Items[1].BlocksSwitch);
        Assert.Equal("已就绪", report.Items[3].State);
    }

    [Fact]
    public async Task Disconnected_bridge_is_unverified_but_can_be_connected_by_the_existing_switch_flow()
    {
        var report = await new Fixture { Ea = Connected with { BridgeState = "Disconnected", CanSetAutoStart = false, AutoStartRegistered = null, Packaged = null } }.Check.RunAsync(default);
        Assert.True(report.CanProceed); Assert.False(report.AllReady);
        Assert.Equal("待连接", report.Items[3].State);
    }

    [Fact]
    public async Task Connected_bridge_requires_packaged_app_and_startup_permission()
    {
        foreach (var ea in new[] { Connected with { Packaged = false }, Connected with { CanSetAutoStart = false },
            Connected with { AutoStartRegistered = null }, Connected with { BridgeState = "UnsupportedVersion" },
            Connected with { AutoStartChange = new(Guid.NewGuid(), "Sending", "", true) },
            Connected with { Quit = new(Guid.NewGuid(), "AwaitingExit", "", 42) } })
        {
            var report = await new Fixture { Ea = ea }.Check.RunAsync(default);
            Assert.False(report.CanProceed); Assert.True(report.Items[3].BlocksSwitch);
        }
    }

    [Fact]
    public async Task One_read_failure_does_not_suppress_the_other_app()
    {
        var fixture = new Fixture { ReadFails = true };
        var report = await fixture.Check.RunAsync(default);
        Assert.False(report.CanProceed); Assert.Equal("待核实", report.Items[0].State);
        Assert.Equal("已就绪", report.Items[2].State); Assert.Equal("已就绪", report.Items[3].State);
    }

    [Fact]
    public async Task Inaccessible_files_and_storage_warnings_are_not_success()
    {
        var missing = await new Fixture { Exists = false }.Check.RunAsync(default);
        Assert.False(missing.CanProceed); Assert.True(missing.Items[0].BlocksSwitch); Assert.True(missing.Items[2].BlocksSwitch);
        var warning = await new Fixture { StorageWarning = "配置损坏" }.Check.RunAsync(default);
        Assert.Equal("待核实", warning.Items[0].State); Assert.False(warning.CanProceed);
        var error = await new Fixture { InspectFails = true }.Check.RunAsync(default);
        Assert.Equal("待核实", error.Items[1].State); Assert.False(error.CanProceed);
    }

    [Fact]
    public async Task Recheck_reads_changed_paths_instead_of_caching_success()
    {
        var fixture = new Fixture(); var check = fixture.Check;
        Assert.True((await check.RunAsync(default)).AllReady);
        fixture.Exists = false;
        Assert.False((await check.RunAsync(default)).CanProceed);
    }

    [Fact]
    public async Task Cancellation_does_not_produce_a_ready_report()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Fixture().Check.RunAsync(cancel.Token));
    }
}
