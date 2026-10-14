using NPEduTools.ClassIsland.Admin;

namespace NPEduTools.Tests;

public sealed class AdminHelperPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NPEduTools-管理员路径-" + Guid.NewGuid().ToString("N"));
    private string Touch(string relative)
    {
        string path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    [Fact]
    public void Packaged_Host_and_App_resolve_the_same_root_Admin_component()
    {
        Touch("NPEduTools.App.exe"); Touch("Host/NPEduTools.Host.exe");
        string helper = Touch("Admin/NPEduTools.ClassIsland.Admin.exe");
        Assert.Equal(helper, AdminHelperPath.FromAppDirectory(_root));
        Assert.Equal(helper, AdminHelperPath.FromHostDirectory(Path.Combine(_root, "Host") + Path.DirectorySeparatorChar));
        Assert.False(File.Exists(Path.Combine(_root, "Host", "Admin", "NPEduTools.ClassIsland.Admin.exe")));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void Unrelated_Host_folder_never_searches_parent_or_PATH(bool appMarker, bool hostMarker)
    {
        if (appMarker) Touch("NPEduTools.App.exe");
        if (hostMarker) Touch("Host/NPEduTools.Host.exe");
        Touch("Admin/NPEduTools.ClassIsland.Admin.exe");
        string host = Path.Combine(_root, "Host");
        Assert.Equal(Path.Combine(host, "Admin", "NPEduTools.ClassIsland.Admin.exe"), AdminHelperPath.FromHostDirectory(host));
    }

    [Fact]
    public void Missing_packaged_component_does_not_fall_back_to_a_second_copy_in_Host()
    {
        Touch("NPEduTools.App.exe"); Touch("Host/NPEduTools.Host.exe");
        Touch("Host/Admin/NPEduTools.ClassIsland.Admin.exe");
        Assert.Equal(Path.Combine(_root, "Admin", "NPEduTools.ClassIsland.Admin.exe"),
            AdminHelperPath.FromHostDirectory(Path.Combine(_root, "Host")));
    }

    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Missing_component_has_a_specific_error_before_launch_or_authorization()
    {
        var reply = await AdminClient.RunAsync(AdminHelperPath.FromAppDirectory(_root), "status", "absent-classisland.exe");
        Assert.Equal("Failed", reply.Outcome);
        Assert.Equal("AdminHelperMissing", reply.ErrorCode);
        Assert.Contains("文件不存在或无法访问", reply.Message);
        Assert.Null(reply.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
