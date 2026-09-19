#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClassIsland.Shared.IPC;
using dotnetCampus.Ipc.CompilerServices.GeneratedProxies;
using NPEduTools.ClassIsland.Bridge.Contracts;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Integrations.ClassIsland;

public static class ClassIslandCalendarProbe
{
    public static async Task<StatusResult> ReadAsync(string pipe, string dateText)
    {
        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return new("Rejected", "InvalidDate", "日期无效。");
        var client = new IpcClient(); using var provider = client.Provider;
        try
        {
            provider.StartServer(); client.JsonIpcProvider.StartServer();
            var peer = await provider.GetAndConnectToPeerAsync(pipe).WaitAsync(TimeSpan.FromSeconds(4));
            var clock = provider.CreateIpcProxy<IRecordingBridgeP0>(peer);
            var hello = Decode<BridgeHello>(await clock.GetHelloAsync());
            ClassIslandSchoolClock.ValidateHello(hello);
            if (!hello.Capabilities.Contains("calendar-31-days")) return new("Unavailable", "CalendarUnsupported", "请将 ClassIsland 时间桥接插件更新至 0.2.0.0 或兼容版本。");
            var service = provider.CreateIpcProxy<IRecordingBridgeCalendar>(peer);
            var reply = Decode<BridgeCalendarReply>(await service.GetDayAsync(dateText));
            if (reply.ProtocolVersion != BridgeProtocol.Version || reply.BridgeInstanceId != hello.BridgeInstanceId || reply.RequestedDate != dateText || !reply.Forecast)
                throw new InvalidDataException("Calendar identity mismatch");
            if (reply.Status != "Succeeded" || reply.Day is null || reply.SchoolNow is null)
                return new("Unavailable", "Calendar" + reply.Status, reply.Status == "OutOfRange" ? "只可查询学校今天起 31 天内的课表。" : "预计课表暂不可用，请稍后刷新。");
            var now = ClassIslandSchoolClock.ParseSchoolTime(reply.SchoolNow);
            var today = DateOnly.FromDateTime(now.Date);
            if (date.DayNumber - today.DayNumber is < 0 or > 30) throw new InvalidDataException("Invalid date range");
            var day = ClassIslandSchoolClock.ConvertDay(reply.Day, date, now, false, true);
            return new("Succeeded", null, "预计课表；到当天将重新核对生效安排。", Forecast: new(hello.BridgeInstanceId, today, date, day));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Calendar probe: " + error.GetBaseException().GetType().Name);
            return new("Unavailable", "CalendarUnavailable", "预计课表查询失败，请检查桥接插件与连接状态。");
        }
    }
    private static T Decode<T>(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > BridgeProtocol.MaxBytes) throw new InvalidDataException("Oversize");
        return JsonSerializer.Deserialize<T>(json, BridgeProtocol.Json) ?? throw new InvalidDataException("Empty");
    }
}
