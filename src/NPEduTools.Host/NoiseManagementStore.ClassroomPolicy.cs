namespace NPEduTools.Host;

public sealed partial class NoiseManagementStore
{
    // Legacy consumers retain their credential format. For managed classroom actions,
    // a newer negative observation takes precedence over an otherwise valid old cache.
    private string ClassroomPasswordMarker => _path + ".classroom-password-unavailable";
    private bool _classroomPasswordBlocked;
    private bool _classroomMarkerPending;
    internal bool ClassroomPasswordReady => _schoolScope is not null && Configured && !_classroomPasswordBlocked;
    internal long ClassroomPasswordEpoch { get; private set; }

    private void ReadClassroomPasswordAvailability()
    {
        try
        {
            File.GetAttributes(ClassroomPasswordMarker);
            _classroomPasswordBlocked = true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _classroomPasswordBlocked = _classroomMarkerPending = true;
        }
    }

    internal void SetClassroomPasswordReady(bool ready)
    {
        bool shouldBlock = !ready;
        // No previous school password exists on a new binding; absence is already durable.
        if (shouldBlock && _credential is null && !_classroomPasswordBlocked) return;
        if (!_classroomMarkerPending && _classroomPasswordBlocked == shouldBlock) return;
        if (!_classroomPasswordBlocked)
        {
            ClassroomPasswordEpoch++;
            foreach (var id in _tickets.Where(t => t.Value.RequiresClassroomPassword).Select(t => t.Key).ToArray())
                _tickets.Remove(id);
        }
        _classroomPasswordBlocked = true;
        try
        {
            if (ready) File.Delete(ClassroomPasswordMarker);
            else
            {
                // The existence of even a partial marker is sufficient to block on restart.
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using var marker = new FileStream(ClassroomPasswordMarker, FileMode.Create, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough);
                marker.WriteByte(1);
                marker.Flush(true);
            }
            _classroomPasswordBlocked = shouldBlock;
            _classroomMarkerPending = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _classroomMarkerPending = true;
        }
    }
}
