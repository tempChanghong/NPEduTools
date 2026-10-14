using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

public sealed partial class NoiseService
{
    private readonly Dictionary<Guid, StudySessionReport> _finishedStudyReports = [];
    private readonly Queue<Guid> _finishedStudyOrder = new();
    internal StudySessionReport? StudyReportSnapshot()
    {
        lock (_sync)
        {
            if (_session is not {} s || !s.Scheduled && !s.ManualStudy) return null;
            return StudyReport(s, _state is "Stopped" or "Faulted");
        }
    }
    // Only the report collector's already witnessed session IDs may use this terminal cache.
    // Starting another capture must not erase a tail before the collector's next poll.
    internal StudySessionReport? FinishedStudyReport(Guid session)
    {
        lock (_sync) return _finishedStudyReports.GetValueOrDefault(session);
    }
    private void PreserveStudyReport(Session s)
    {
        if (!s.Scheduled && !s.ManualStudy) return;
        _finishedStudyReports[s.Id] = StudyReport(s, true);
        _finishedStudyOrder.Enqueue(s.Id);
        while (_finishedStudyOrder.Count > 16) _finishedStudyReports.Remove(_finishedStudyOrder.Dequeue());
    }
    private StudySessionReport StudyReport(Session s, bool complete) =>
        new(s.Id, complete, s.FinishedSeconds ?? _time.GetElapsedTime(s.Started).TotalSeconds,
                s.Clipped, s.Frames, s.DisciplineReminders, s.ReportRaw.Snapshot(),
                s.EstimateProfile is null ? null : s.ReportEstimated.Snapshot(), s.EstimateReason,s.EstimateReferenceNote);
    internal void RecordStudyReminder(Guid session)
    {
        lock (_sync) if (_session is {} s && (s.Scheduled || s.ManualStudy) && s.Id == session && _state == "Active") s.DisciplineReminders++;
    }
    private void ReportRaw(Session s, NoiseFrame frame)
    {
        if (!s.Scheduled && !s.ManualStudy) return;
        s.ReportRaw.Receive(s.FinishedSeconds ?? _time.GetElapsedTime(s.Started).TotalSeconds, frame.Seconds,
            frame.MeanSquare, NPEduTools.Core.NoiseMeter.Dbfs(frame.Peak), s.ThresholdPolicy);
    }
    private void ReportEstimated(Session s, ReferenceNoiseFrame frame, double energy, double maximum)
    {
        if (!s.Scheduled && !s.ManualStudy || s.EstimateProfile is not { } p) return;
        s.ReportEstimated.Receive(s.FinishedSeconds ?? _time.GetElapsedTime(s.Started).TotalSeconds, frame.Seconds,
            energy * Math.Pow(10, p.OffsetDb / 10), maximum, s.ThresholdPolicy);
    }
}
