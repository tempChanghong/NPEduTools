using System.Diagnostics;

namespace NPEduTools.Tests;

internal sealed class TestProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _error;
    private TestProcess(Process process)
    {
        _process = process;
        _error = process.StandardError.ReadToEndAsync();
    }

    public static ProcessStartInfo StartInfo(string project, bool testProject, params string[] args)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NPEduTools.sln")))
            directory = directory.Parent;
        string root = directory?.FullName ?? throw new InvalidOperationException("Repository not found.");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string localDotnet = Path.Combine(root, ".tools", "dotnet", "dotnet.exe");
        // Keep child processes on the same host chosen by our build script / test SDK.
        string? selectedHost = Environment.GetEnvironmentVariable("NPEEDUTOOLS_DOTNET_HOST")
            ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        string dotnet = selectedHost is not null && File.Exists(selectedHost)
            ? selectedHost : File.Exists(localDotnet) ? localDotnet : "dotnet";
        var info = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = root
        };
        info.ArgumentList.Add(Path.Combine(root, testProject ? "tests" : "src", project, "bin", configuration, "net10.0", project + ".dll"));
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    public static async Task<TestProcess> StartAsync(string project, bool testProject, params string[] args)
    {
        var result = new TestProcess(Process.Start(StartInfo(project, testProject, args))!);
        try
        {
            string? ready = await result._process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (ready is null || !(ready == "READY" || ready.StartsWith("Host ready:", StringComparison.Ordinal)))
                throw new InvalidOperationException($"Process did not become ready: {ready}; {await result._error.WaitAsync(TimeSpan.FromSeconds(2))}");
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await _error.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { _process.Dispose(); }
    }
}
