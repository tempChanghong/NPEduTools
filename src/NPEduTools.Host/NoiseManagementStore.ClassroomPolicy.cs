namespace NPEduTools.Host;

public sealed partial class NoiseManagementStore
{
    // An explicit withdrawal also fences callers of the original maintenance API.
    // A malformed response still only blocks the newer classroom password actions.
    private string ClassroomPasswordMarker => _schoolPathPrefix + ".classroom-password-unavailable";
    private bool _classroomPasswordBlocked;
    private bool _classroomMarkerPending;
    private bool _schoolPasswordWithdrawn;
    internal bool ClassroomPasswordReady => _schoolScope is not null && Configured && !_classroomPasswordBlocked;
    internal long ClassroomPasswordEpoch { get; private set; }

    private void ReadClassroomPasswordAvailability()
    {
        try
        {
            using var marker = File.OpenRead(ClassroomPasswordMarker);
            _classroomPasswordBlocked = true;
            _schoolPasswordWithdrawn = marker.ReadByte() == 2;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _classroomPasswordBlocked = _classroomMarkerPending = true;
        }
    }

    internal void SetClassroomPasswordReady(bool ready, bool withdrawn = false)
    {
        bool shouldBlock = !ready;
        bool shouldWithdraw = shouldBlock && (withdrawn || _schoolPasswordWithdrawn);
        // No previous school password exists on a new binding; absence is already durable.
        if (shouldBlock && _credential is null && !_classroomPasswordBlocked) return;
        if (!_classroomMarkerPending && _classroomPasswordBlocked == shouldBlock && _schoolPasswordWithdrawn == shouldWithdraw) return;
        if (!_classroomPasswordBlocked || shouldWithdraw && !_schoolPasswordWithdrawn)
        {
            ClassroomPasswordEpoch++;
            foreach (var id in _tickets.Where(t => t.Value.RequiresClassroomPassword ||
                         shouldWithdraw && t.Value.ClassroomPolicyVersion is null).Select(t => t.Key).ToArray())
                _tickets.Remove(id);
        }
        _classroomPasswordBlocked = true;
        _schoolPasswordWithdrawn |= shouldWithdraw;
        try
        {
            if (ready) File.Delete(ClassroomPasswordMarker);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ClassroomPasswordMarker)!);
                string temporary = ClassroomPasswordMarker + ".tmp";
                using (var marker = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    marker.WriteByte(shouldWithdraw ? (byte)2 : (byte)1);
                    marker.Flush(true);
                }
                File.Move(temporary, ClassroomPasswordMarker, true);
            }
            _classroomPasswordBlocked = shouldBlock;
            _schoolPasswordWithdrawn = shouldWithdraw;
            _classroomMarkerPending = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _classroomMarkerPending = true;
        }
    }
}
