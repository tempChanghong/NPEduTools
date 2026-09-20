using NPEduTools.ClassIsland.Admin;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public sealed record ClassroomSetupItem(string Title, string State, string Detail, bool BlocksSwitch = false)
{
    public string Label => $"{Title} · {State}";
}

public sealed record ClassroomSetupReport(ClassroomSetupItem[] Items, DateTimeOffset CheckedAt)
{
    public bool CanProceed => Items.All(item => !item.BlocksSwitch);
    public bool AllReady => Items.All(item => item.State == "已就绪");
}

/// <summary>Read-only prerequisites. Never starts either app or changes a startup registration.</summary>
public sealed class ClassroomSetupCheck(
    Func<string, CancellationToken, Task<HostResponse>> read,
    Func<string, Task<AdminResult>> inspectTask,
    Func<string, bool> fileExists)
{
    public async Task<ClassroomSetupReport> RunAsync(CancellationToken token)
    {
        // Each branch reports its own failure, so a missing ClassIsland task does not hide ExamAware issues.
        var branches = await Task.WhenAll(CheckClassIslandAsync(token), CheckExamAwareAsync(token));
        token.ThrowIfCancellationRequested();
        return new(branches.SelectMany(x => x).ToArray(), DateTimeOffset.Now);
    }

    private async Task<ClassroomSetupItem[]> CheckClassIslandAsync(CancellationToken token)
    {
        ClassroomSetupItem program;
        string? path;
        try
        {
            var response = await read("classisland.config.get", token);
            if (response.Outcome != "Succeeded" || response.Launch is not { StorageWarning: null } data)
                return [Unknown("ClassIsland 程序", "无法读取已保存的位置，请打开程序设置检查配置。"),
                    Unknown("管理员自启动任务", "需要先核实 ClassIsland 程序位置。")];
            path = data.Settings.ExecutablePath;
            program = Program("ClassIsland 程序", path);
        }
        catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
        { return [Unknown("ClassIsland 程序", "后台未连接或配置读取失败，请重试。"), Unknown("管理员自启动任务", "尚未取得程序位置。")]; }

        if (program.BlocksSwitch)
            return [program, Unknown("管理员自启动任务", "请先保存有效的 ClassIsland 程序位置，再重新检查。")];
        ClassroomSetupItem task;
        try
        {
            var result = await inspectTask(path!);
            token.ThrowIfCancellationRequested();
            task = result.Outcome != "Succeeded" || result.Status is not { } status
                ? Unknown("管理员自启动任务", "只读检查未完成，请在管理员自启动设置中核实。 " + result.Message)
                : status.TaskState switch
                {
                    "Enabled" => new("管理员自启动任务", "已就绪", "匹配当前程序与用户，当前已开启。"),
                    "Disabled" => new("管理员自启动任务", "已就绪", "匹配当前程序与用户，当前已暂停；切换模式时按目标调整。"),
                    "Missing" => new("管理员自启动任务", "待配置", "尚未创建。请打开管理员自启动设置完成配置。", true),
                    "Conflict" => new("管理员自启动任务", "待处理", "同名任务与当前程序或用户不匹配，请在设置中核实；不会覆盖原任务。", true),
                    _ => Unknown("管理员自启动任务", status.TaskMessage)
                };
        }
        catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
        { task = Unknown("管理员自启动任务", "暂时无法读取任务，请检查管理员组件或在设置中核实权限。"); }
        return [program, task];
    }

    private async Task<ClassroomSetupItem[]> CheckExamAwareAsync(CancellationToken token)
    {
        try
        {
            var response = await read("examaware.status", token);
            if (response.Outcome != "Succeeded" || response.ExamAware is not { } state)
                return [Unknown("ExamAware2 程序", "无法读取配置，请打开考试看板设置检查。"), Unknown("考试看板桥接", "连接服务尚未就绪，请在考试看板页面查看原因。")];
            var program = Program("ExamAware2 程序", state.ExecutablePath);
            ClassroomSetupItem bridge = state.BridgeState switch
            {
                "Connected" when state.Packaged != true => new("考试看板桥接", "待处理", "请使用正式打包版本完成配对。", true),
                "Connected" when !state.CanSetAutoStart => new("考试看板桥接", "待配置", "请更新到桥接 0.3.0 或兼容版本，并授权登录自启动设置。", true),
                "Connected" when state.AutoStartChange?.State == "Sending" || state.Quit?.State is "Sending" or "AwaitingExit" =>
                    new("考试看板桥接", "处理中", "软件正在处理自启动或退出请求，请完成后重新检查。", true),
                "Connected" when state.AutoStartRegistered is null => Unknown("考试看板桥接", "已连接，但尚未取得自启动登记，请重新检查。"),
                "Connected" => new("考试看板桥接", "已就绪", $"已连接，支持修改自启动；当前登记为{(state.AutoStartRegistered == true ? "开启" : "关闭")}。"),
                "Disconnected" => new("考试看板桥接", "待连接", "尚未确认配对及权限。可在考试看板页面启动软件、完成配对后重新检查；继续切换时会尝试连接，未就绪不会修改自启动。"),
                "UnsupportedVersion" => new("考试看板桥接", "待处理", state.Message, true),
                _ => Unknown("考试看板桥接", state.Message)
            };
            return [program, bridge];
        }
        catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
        { return [Unknown("ExamAware2 程序", "后台未连接或配置读取失败，请重试。"), Unknown("考试看板桥接", "尚未取得连接状态。")]; }
    }

    private ClassroomSetupItem Program(string title, string? path) => string.IsNullOrWhiteSpace(path)
        ? new(title, "待配置", "尚未保存程序位置。", true)
        : fileExists(path) ? new(title, "已就绪", "已保存的位置可访问：" + path)
        : new(title, "待处理", "已保存的程序不存在或不可访问，请重新选择位置：" + path, true);
    private static ClassroomSetupItem Unknown(string title, string detail) => new(title, "待核实", detail, true);
}
