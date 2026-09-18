#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.IPC;
using ClassIsland.Shared.IPC.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using dotnetCampus.Ipc.CompilerServices.GeneratedProxies;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Integrations.ClassIsland;

public static class ClassIslandScheduleProbe
{
    public static async Task<StatusResult> ReadAsync(string pipeName)
    {
        var client = new IpcClient();
        using var provider = client.Provider;
        int disconnected = 0;
        try
        {
            provider.StartServer(); client.JsonIpcProvider.StartServer();
            var peer = await provider.GetAndConnectToPeerAsync(pipeName);
            peer.PeerConnectionBroken += (_, _) => Interlocked.Exchange(ref disconnected, 1);
            var lessons = provider.CreateIpcProxy<IPublicLessonsService, StrictLessonsShape>(peer);
            var profiles = provider.CreateIpcProxy<IPublicProfileService, StrictProfileShape>(peer);
            // Generated property calls are blocking. Never run them on the IPC receive loop.
            return await Task.Run(() =>
            {
                bool enabled = lessons.IsClassPlanEnabled && lessons.IsClassPlanLoaded && lessons.IsTimerRunning;
                var plan = lessons.CurrentClassPlan;
                var profile = profiles.Profile ?? throw new InvalidDataException("档案不可用。");
                string profileHash = Fingerprint(profile), planHash = Fingerprint(plan);
                var before = DateTimeOffset.Now;
                var state = lessons.CurrentState;
                var time = state == TimeState.OnClass ? lessons.NextBreakingTimeLayoutItem : lessons.NextClassTimeLayoutItem;
                var remaining = state == TimeState.OnClass ? lessons.OnBreakingTimeLeftTime : lessons.OnClassLeftTime;
                var after = DateTimeOffset.Now;
                var clockAnchor = new DateTimeOffset(after.Date + time.StartTime, after.Offset) - remaining;
                bool clockVerified = time.EndTime > time.StartTime && remaining > TimeSpan.Zero &&
                    clockAnchor >= before.AddSeconds(-2) && clockAnchor <= after.AddSeconds(2) && before.Date == after.Date;
                // Catch torn reads during temporary timetable switches or profile edits.
                var planAfter = lessons.CurrentClassPlan; var profileAfter = profiles.Profile;
                if (planHash != Fingerprint(planAfter) || profileHash != Fingerprint(profileAfter) ||
                    enabled != (lessons.IsClassPlanEnabled && lessons.IsClassPlanLoaded && lessons.IsTimerRunning))
                    return new StatusResult("Unavailable", "ScheduleChangedDuringRead", "课表正在变化，请稍后重新读取。");
                if (Volatile.Read(ref disconnected) != 0)
                    return new StatusResult("Unavailable", "ClassIslandDisconnected", "读取日程时 ClassIsland 已断开。");
                string clockMessage = clockVerified ? "已用 ClassIsland 倒计时核对本机时间" : "时间基准未确认：可能存在校时偏移、课表间隙或缺少下一课间，暂停新预演任务";
                if (plan is null)
                    return new StatusResult("Succeeded", null, "当前没有生效课表。", Schedule: new(profile.Id, Guid.Empty, Guid.Empty,
                        "无生效课表", DateOnly.FromDateTime(after.Date), profileHash, after, false, false, clockMessage, []));
                var matches = profile.ClassPlans.Where(p => Fingerprint(p.Value) == planHash).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("无法唯一确定生效课表，请检查重复课表。");
                if (!profile.TimeLayouts.TryGetValue(plan.TimeLayoutId, out var layout)) throw new InvalidDataException("课表对应的时间表不存在。");
                var slots = layout.Layouts.Where(t => t.TimeType == 0).ToArray();
                if (slots.Length > 64 || slots.Length != plan.Classes.Count) throw new InvalidDataException("课程与时间点数量不匹配或超过 64 节。");
                var result = new List<LessonSlot>();
                for (int i = 0; i < slots.Length; i++)
                {
                    var slot = slots[i]; var lesson = plan.Classes[i];
                    if (slot.StartTime < TimeSpan.Zero || slot.EndTime >= TimeSpan.FromDays(1) || slot.EndTime <= slot.StartTime)
                        throw new InvalidDataException("课程时间无效或跨越午夜，暂不支持生成计划。");
                    Guid subjectId = lesson.SubjectId != Guid.Empty ? lesson.SubjectId : slot.DefaultClassId;
                    bool defined = profile.Subjects.TryGetValue(subjectId, out var subject);
                    string name = defined ? subject!.Name : "未定义科目";
                    result.Add(new(i + 1, subjectId, name[..Math.Min(name.Length, 60)], new(after.Date + slot.StartTime, after.Offset),
                        new(after.Date + slot.EndTime, after.Offset), lesson.IsEnabled && defined && subjectId != Guid.Empty));
                }
                return new StatusResult("Succeeded", null, "已读取当前生效日程。", Schedule: new(profile.Id, matches[0].Key,
                    plan.TimeLayoutId, plan.Name[..Math.Min(plan.Name.Length, 80)], DateOnly.FromDateTime(after.Date),
                    profileHash, after, enabled, clockVerified, clockMessage, result.ToArray()));
            });
        }
        catch (Exception error)
        {
            string type = error.GetBaseException().GetType().Name;
            Console.Error.WriteLine("Schedule probe error: " + type);
            return new("Failed", "ScheduleUnavailable", error is InvalidDataException ? error.Message :
                "无法读取完整日程，请核对 ClassIsland 接口；现有课程状态读取仍可使用。");
        }
    }

    private static object? PlanData(ClassPlan? plan) => plan is null ? null : new
    {
        plan.Name, plan.TimeLayoutId, plan.IsEnabled, plan.IsOverlay, plan.OverlaySourceId, plan.AssociatedGroup,
        Rule = new { plan.TimeRule.WeekDay, plan.TimeRule.WeekCountDiv, plan.TimeRule.WeekCountDivTotal },
        Classes = plan.Classes.Select(c => new { c.SubjectId, c.IsEnabled }).ToArray()
    };

    private static string Fingerprint<T>(T value)
    {
        // Deserialization creates runtime timestamps and caches. Hash only schedule
        // inputs, not incidental ObservableRecipient or constructor-generated state.
        object? data = value switch
        {
            ClassPlan plan => PlanData(plan),
            Profile profile => new
            {
                profile.Id,
                Plans = profile.ClassPlans.OrderBy(p => p.Key).Select(p => new { p.Key, Plan = PlanData(p.Value) }).ToArray(),
                Layouts = profile.TimeLayouts.OrderBy(p => p.Key).Select(p => new { p.Key,
                    Slots = p.Value.Layouts.Select(t => new { t.StartTime, t.EndTime, t.TimeType, t.DefaultClassId }).ToArray() }).ToArray(),
                // The IPC serializer may populate EditingSubjects and create extra
                // random-key copies. Only IDs referenced by timetable inputs matter.
                Subjects = profile.Subjects.Where(p => profile.ClassPlans.Values.Any(c => c.Classes.Any(s => s.SubjectId == p.Key)) ||
                    profile.TimeLayouts.Values.Any(l => l.Layouts.Any(t => t.DefaultClassId == p.Key)))
                    .OrderBy(p => p.Key).Select(p => new { p.Key, p.Value.Name }).ToArray()
            },
            _ => value
        };
        string json = JsonSerializer.Serialize(data);
        if (json.Length > 2 * 1024 * 1024) throw new InvalidDataException("档案过大，暂不支持读取完整日程。");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
