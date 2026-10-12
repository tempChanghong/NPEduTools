using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Host;

public sealed partial class NoiseService
{
    internal StudySessionReport? StudyReportSnapshot()
    {
        lock (_sync)
        {
            if (_session is not {} s || !s.Scheduled && !s.ManualStudy) return null;
            return new(s.Id, _state is "Stopped" or "Faulted", s.FinishedSeconds ?? _time.GetElapsedTime(s.Started).TotalSeconds,
                s.Clipped, s.Frames, s.DisciplineReminders, s.ReportRaw.Snapshot(),
                s.EstimateProfile is null ? null : s.ReportEstimated.Snapshot(), s.EstimateReason,s.EstimateReferenceNote);
        }
    }
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
