using NPEduTools.Contracts;
namespace NPEduTools.Host;
public sealed class StudyModeClock(Func<string> mode, Func<SchoolClockFrame> school, TimeProvider? time = null)
{
    private readonly Guid _connection = Guid.NewGuid();
    private long _sequence;
    public string Mode => mode() == "Exam" ? "Exam" : "Daily";
    public SchoolClockFrame Snapshot()
    {
        if (Mode != "Exam") return school();
        var utc = (time ?? TimeProvider.System).GetUtcNow();
        var wall = new DateTimeOffset(utc.ToOffset(TimeSpan.FromHours(8)).DateTime, TimeSpan.Zero);
        return new(_connection, _connection, Interlocked.Increment(ref _sequence), 1, wall, 0, "Advancing", "Windows time (Asia/Shanghai)");
    }
}
