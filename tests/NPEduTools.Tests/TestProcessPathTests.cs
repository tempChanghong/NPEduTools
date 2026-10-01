namespace NPEduTools.Tests;

public sealed class TestProcessPathTests
{
    [Fact]
    public void Isolated_artifacts_launch_matching_peer_and_pivot()
    {
        string root = Path.GetFullPath(".");
        string bin = Path.Combine(root, ".artifacts", "noise-tests", "bin");
        Assert.Equal(Path.Combine(bin, "Peer", "debug", "Peer.dll"),
            TestProcess.ResolveAssemblyPath(root, Path.Combine(bin, "NPEduTools.Tests", "debug"), "Peer", true));
    }

    [Fact]
    public void Conventional_output_retains_configuration()
    {
        string root = Path.GetFullPath(".");
        Assert.Equal(Path.Combine(root, "src", "Host", "bin", "Release", "net10.0", "Host.dll"),
            TestProcess.ResolveAssemblyPath(root, Path.Combine(root, "tests", "NPEduTools.Tests", "bin", "Release", "net10.0"), "Host", false));
    }
}
