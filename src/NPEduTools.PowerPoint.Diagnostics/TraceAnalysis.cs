using System.Text;
using System.Text.Json;

namespace NPEduTools.PowerPoint.Diagnostics;

public sealed class TraceAnalysis
{
    public int Frames { get; private set; }
    public int InvalidLines { get; private set; }
    public bool SequenceGap { get; private set; }
    public bool HasEnvironment { get; private set; }
    public bool HasSummary { get; private set; }
    public bool DataLoss { get; private set; }
    public bool LimitReached { get; private set; }
    public int InputEvents { get; private set; }
    public int TouchDowns { get; private set; }
    public int Candidates { get; private set; }
    public int SlideChanges { get; private set; }
    public int AnimationChanges { get; private set; }
    public int ContextBreaks { get; private set; }
    public int FeatureFrames { get; private set; }
    public bool ObservedActions { get; private set; }
    public bool ObservedLinks { get; private set; }
    public bool ObservedTriggers { get; private set; }
    public bool ObservedMedia { get; private set; }
    public bool ObservedGroups { get; private set; }
    public bool IncompleteFeatureInventory { get; private set; }
    public SortedDictionary<string, int> Sources { get; } = [];
    public SortedDictionary<string, int> Outcomes { get; } = [];
    public SortedDictionary<string, int> Statuses { get; } = [];
    public SortedSet<string> OfficeBuilds { get; } = [];
    public SortedSet<uint> Dpis { get; } = [];
    public bool TargetTouchValidationPassed => false;
    public bool StructurallyComplete => HasEnvironment && HasSummary && InvalidLines == 0 && !SequenceGap && !DataLoss && !LimitReached;

    private long _lastSequence, _lastTime = -1, _lastShowTime;
    private ShowTarget? _lastShow;
    private bool _summarySeen;

    public static TraceAnalysis Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > 40 * 1024 * 1024) throw new InvalidDataException("Trace exceeds 40 MiB.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return Read(reader);
    }

    public static TraceAnalysis Read(TextReader reader)
    {
        var result = new TraceAnalysis();
        long characters = 0;
        // Limit before allocating an arbitrary-length JSON line, including files still being written.
        var line = new StringBuilder();
        int c;
        while ((c = reader.Read()) != -1)
        {
            if (++characters > 40 * 1024 * 1024) throw new InvalidDataException("Trace exceeds character limit.");
            if (c == '\n') { result.Accept(line.ToString()); line.Clear(); }
            else
            {
                if (line.Length >= 65536) throw new InvalidDataException("Trace line exceeds 64 KiB.");
                line.Append((char)c);
            }
        }
        if (line.Length != 0) result.Accept(line.ToString());
        return result;
    }

    private void Accept(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var document = JsonDocument.Parse(line, new() { MaxDepth = 32 });
            var frame = document.RootElement;
            if (frame.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException();
            long sequence = frame.GetProperty("sequence").GetInt64();
            long time = frame.GetProperty("monotonicMs").GetInt64();
            string type = frame.GetProperty("type").GetString() ?? throw new InvalidDataException();
            var data = frame.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            Frames++;
            if (sequence != _lastSequence + 1 || time < _lastTime || _summarySeen)
            { SequenceGap = true; _lastShow = null; }
            _lastSequence = sequence; _lastTime = time;
            switch (type)
            {
                case "environment":
                    if (HasEnvironment || Frames != 1 || !data.GetProperty("readOnly").GetBoolean()) SequenceGap = true;
                    HasEnvironment = true;
                    break;
                case "show": ReadShow(data, time); break;
                case "input":
                    var sample = data.GetProperty("sample").Deserialize<InputSample>() ?? throw new InvalidDataException();
                    if (sample.Kind == "Cancel") break; // Outside-target cancellation is not a mouse event.
                    if (sample.Kind is not ("Down" or "Up" or "Move" or "RightDown")) throw new InvalidDataException();
                    InputEvents++;
                    string source = InputSource.Classify(sample.ExtraInfo, sample.Flags);
                    Increment(Sources, source);
                    if (source == "TouchMarked" && sample.Kind == "Down") TouchDowns++;
                    break;
                case "gesture":
                    var gesture = data.Deserialize<TapResult>() ?? throw new InvalidDataException();
                    string outcome = gesture.Outcome;
                    if (outcome is not ("TouchTapCandidate" or "TargetLost" or "TargetChanged" or "SourceChanged" or
                        "Moved" or "NotTouchMarked" or "LongOrInvalidDuration" or "OtherButton" or "OverlappingDown" or
                        "InputGap" or "SnapshotUnavailable" or "SessionEnded")) outcome = "Other";
                    Increment(Outcomes, outcome);
                    if (outcome == "TouchTapCandidate") Candidates++;
                    break;
                case "inputGap": DataLoss = true; _lastShow = null; break;
                case "summary":
                    HasSummary = true; _summarySeen = true;
                    DataLoss |= data.GetProperty("droppedInputs").GetInt64() != 0 ||
                        data.GetProperty("callbackErrors").GetInt64() != 0 || data.GetProperty("droppedSnapshots").GetInt64() != 0;
                    LimitReached |= data.GetProperty("limitReached").GetBoolean();
                    break;
                default: throw new InvalidDataException();
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or
            InvalidDataException or FormatException or OverflowException or ArgumentException)
        { InvalidLines++; _lastShow = null; }
    }

    private void ReadShow(JsonElement data, long time)
    {
        var snapshot = data.Deserialize<ShowSnapshot>() ?? throw new InvalidDataException();
        string status = snapshot.Status;
        if (status is not ("NotRunning" or "NoSlideShow" or "Showing" or "MultipleInstancesUnsupported" or
            "MultipleShowsUnsupported" or "WindowMappingUnavailable" or "WindowIdentityMismatch" or "WindowUnavailable" or
            "RunningButNotRegistered" or "ComUnavailable" or "ProbeFailed" or "ProbeTimeout" or "WorkerUnavailable")) status = "Other";
        Increment(Statuses, status);
        if (snapshot.OfficeBuild is { Length: > 0 and <= 80 } build && OfficeBuilds.Count < 32) OfficeBuilds.Add(build);
        if (status != "Showing" || snapshot.Windows.Length != 1) { _lastShow = null; ContextBreaks++; return; }
        var target = snapshot.Windows[0];
        if (target is null || string.IsNullOrEmpty(target.Id)) throw new InvalidDataException();
        if (Dpis.Count < 32) Dpis.Add(target.Dpi);
        if (_lastShow is { } previous && time - _lastShowTime <= 750 &&
            previous.Id == target.Id && previous.Hwnd == target.Hwnd && previous.ProcessId == target.ProcessId &&
            previous.ProcessStartedUtcTicks == target.ProcessStartedUtcTicks)
        {
            if (previous.SlideId != target.SlideId) SlideChanges++;
            else if (previous.ClickIndex != target.ClickIndex) AnimationChanges++;
        }
        else ContextBreaks++;
        _lastShow = target; _lastShowTime = time;
        if (target.Features is { } features)
        {
            FeatureFrames++;
            ObservedActions |= features.ActionShapes > 0;
            ObservedLinks |= features.Hyperlinks > 0;
            ObservedTriggers |= features.InteractiveSequences > 0;
            ObservedMedia |= features.MediaShapes > 0;
            ObservedGroups |= features.GroupShapes > 0;
            IncompleteFeatureInventory |= features.Coverage != "TopLevelOnly";
        }
    }

    private static void Increment(SortedDictionary<string, int> counts, string key)
        => counts[key] = counts.GetValueOrDefault(key) + 1;

    public string Markdown()
    {
        var text = new StringBuilder("# PowerPoint 诊断分析\n\n");
        text.AppendLine(StructurallyComplete ? "日志结构完整；这不代表触摸兼容性或辅助翻页已验收。" :
            "日志不完整或存在丢失/格式问题，不能据此作出触摸兼容性结论。");
        text.AppendLine($"\n可读帧：{Frames}；异常行：{InvalidLines}；缺少开始记录：{Flag(!HasEnvironment)}；缺少结束记录：{Flag(!HasSummary)}；序号/时间异常：{Flag(SequenceGap)}；数据丢失：{Flag(DataLoss)}；达到容量限制：{Flag(LimitReached)}。");
        text.AppendLine($"\n输入事件：{InputEvents}；触摸标记按下：{TouchDowns}；轻点候选：{Candidates}。");
        text.AppendLine(TouchDowns == 0 ? "\n未观察到触摸标记按下。可能没有进行触摸测试，或驱动未提供兼容鼠标标记；不能认定设备没有触摸屏。" :
            "\n观察到了触摸兼容鼠标标记。仍需核对原生触摸是否同时被 PowerPoint 处理，以及是否出现重复推进。");
        text.AppendLine($"\n相邻有效放映快照中：幻灯片变化 {SlideChanges} 次，同页动画点击序号变化 {AnimationChanges} 次。连接、目标或采样间隔中断 {ContextBreaks} 次。变化可能来自键盘、鼠标、自动动画或其他控制，不能归因于某次触摸，也不代表完整变化次数。");
        if (OfficeBuilds.Count > 0) text.AppendLine($"\n观察到的 Office 构建：{string.Join("、", OfficeBuilds.Select(Escape))}。");
        if (Dpis.Count > 0) text.AppendLine($"\n窗口 DPI：{string.Join("、", Dpis)}。");
        Table(text, "输入来源（事件数）", Sources);
        Table(text, "手势结果", Outcomes);
        Table(text, "放映状态（采样次数）", Statuses);
        text.AppendLine("\n## 交互对象与后续检查\n");
        if (FeatureFrames == 0) text.AppendLine("日志没有页面交互清单（可能来自旧版工具或没有放映）。");
        else text.AppendLine($"观察到形状动作：{Flag(ObservedActions)}；链接：{Flag(ObservedLinks)}；触发器序列：{Flag(ObservedTriggers)}；媒体形状：{Flag(ObservedMedia)}；组合形状：{Flag(ObservedGroups)}；存在截断或读取失败的清单：{Flag(IncompleteFeatureInventory)}。");
        text.AppendLine("\n页面清单只检查当前幻灯片的有限顶层形状，不是点击命中测试；不覆盖母版、组合内部所有对象、菜单和全部媒体控件。零计数不能证明页面可以安全补发翻页。");
        text.AppendLine("\n仍需在目标 Office 2024 设备核对：轻点与逐步动画、鼠标不重复推进、拖动/长按/书写、菜单/链接/触发器/媒体、多显示器。会话末尾未消费的输入可能省略。本报告不会启用辅助功能，也不会更改 PowerPoint。");
        return text.ToString();
    }

    private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\r", " ").Replace("\n", " ").Replace("[", "&#91;").Replace("]", "&#93;").Replace("|", "&#124;").Replace("`", "&#96;");

    private static string Flag(bool value) => value ? "是" : "否";

    private static void Table(StringBuilder text, string title, SortedDictionary<string, int> counts)
    {
        text.AppendLine($"\n## {title}\n\n| 项目 | 数量 |\n| --- | ---: |");
        foreach (var entry in counts) text.AppendLine($"| {entry.Key} | {entry.Value} |");
        if (counts.Count == 0) text.AppendLine("| 未记录 | 0 |");
    }

    public static string WriteReport(string inputPath)
    {
        var analysis = Read(inputPath);
        string output = Path.GetFullPath(inputPath) + $".analysis-{Guid.NewGuid():N}.md";
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(analysis.Markdown());
        return output;
    }
}
