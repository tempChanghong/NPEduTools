using System.Diagnostics;
using System.Text.Json.Nodes;
namespace NPEduTools.Integrations.Npep;
public sealed partial class NpepRuntime
{
    private Task _studyPhotosLoop = Task.CompletedTask;
    private StudyRetry? _photoLeaseRetry, _photoUploadRetry;
    private void InitializeStudyPhotos(INpepStudySettings study)
    {
        if (study is not INpepStudyPhotos { SupportsPhotos: true } photos || _device is null) return;
        _studyPhotosLoop = Task.Run(async () => {
            long? unsupported = null;
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    try
                    {
                        var state = Snapshot();
                        if (state is { State: "UNPAIRED" or "SUSPENDED" })
                        {
                            lock (_sync)
                                if (_state.Revision == state.Revision) photos.PhotoFailed("PHOTO_NOT_ELIGIBLE");
                        }
                        else if (state is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } &&
                            (unsupported is null || Stopwatch.GetElapsedTime(unsupported.Value).TotalSeconds >= 60))
                        {
                            var code = await StudyPhotoCycleAsync(photos, _lifetime.Token);
                            if (code == "STUDY_PHOTOS_UNSUPPORTED") unsupported = Stopwatch.GetTimestamp();
                            else if (code is null) unsupported = null;
                        }
                    }
                    catch (Exception e) when (!_lifetime.IsCancellationRequested && (Handled(e) || e is InvalidDataException)) { }
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }
    internal async Task<string?> StudyPhotoCycleAsync(INpepStudyPhotos photos, CancellationToken token)
    {
        long revision = Snapshot().Revision;
        Guid? eventId = null;
        string? scope = null;
        void RequireOriginalConnection()
        {
            token.ThrowIfCancellationRequested();
            if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } state ||
                state.Revision != revision) throw new NpepException("SESSION_SUPERSEDED");
        }
        void Apply(Action update)
        {
            lock (_sync)
            {
                RequireOriginalConnection();
                update();
            }
        }
        try
        {
            RequireOriginalConnection();
            var context = await StudyContextAsync(token);
            RequireOriginalConnection();
            scope = context["identity"]!.ToJsonString();
            string? renewalError = null;
            if (!StudyRetryPending(_photoLeaseRetry, scope) && photos.PhotoLeaseRequest() is { } request)
            {
                request["requestId"] = NpepProtocol.Id(); request["context"] = context.Copy();
                var at = Stopwatch.GetTimestamp();
                try
                {
                    var lease = await _device!.StudyPhotoAsync(context, request, null, RequireOriginalConnection, token);
                    Apply(() => photos.ConfirmPhotoLease(lease, Stopwatch.GetElapsedTime(at)));
                    _photoLeaseRetry = null;
                }
                catch (Exception e) when (!token.IsCancellationRequested && (TransientStudyExchangeFailure(e) ||
                    e is NpepException { Code: "PHOTO_EXECUTION_NOT_FRESH" or "PHOTO_MANUAL_NOT_FRESH" or "PHOTO_TEMPORARILY_UNAVAILABLE" }))
                {
                    // A failed renewal grants no new capture time. It must not block a
                    // previously permitted photo, whose original lease is checked by KV.
                    RequireOriginalConnection();
                    if (e is NpepException problem) DeferPhotoExchange(scope, problem, isUpload: false);
                    renewalError = e is NpepException n ? n.Code : "PHOTO_OFFLINE";
                }
            }
            // An ended window may still upload an already permitted, encrypted photo.
            RequireOriginalConnection();
            if (!StudyRetryPending(_photoUploadRetry, scope) && photos.PendingPhoto() is { } packet)
            {
                eventId = Guid.Parse(packet.Metadata.Text("eventId"));
                if (!JsonNode.DeepEquals(JsonNode.Parse(packet.Scope), context["identity"])) throw new NpepException("BINDING_CHANGED");
                var reply = await _device!.StudyPhotoAsync(context, null, packet, RequireOriginalConnection, token);
                if (reply.Text("eventId") != packet.Metadata.Text("eventId")) throw new NpepException("INVALID_RESPONSE");
                Apply(() => photos.PhotoStored(eventId.Value));
                _photoUploadRetry = null;
            }
            if (renewalError is not null) Apply(() => photos.PhotoFailed(renewalError));
            return renewalError;
        }
        catch (Exception e) when (Handled(e) || e is InvalidDataException or FormatException)
        {
            var code = e is NpepException n ? n.Code : "PHOTO_OFFLINE";
            if (scope is not null && e is NpepException problem) DeferPhotoExchange(scope, problem, eventId is not null);
            lock (_sync)
                if (!_lifetime.IsCancellationRequested && _state.Revision == revision && !_state.ReportingPaused && !_state.Busy)
                    photos.PhotoFailed(code, eventId);
            if (e is NpepException { Status: 401 } refusal && Snapshot().Revision == revision)
                Publish("STOPPED", "设备授权已失效，自习照片同步已停止。", refusal.Code, requireSuspended: true);
            return code;
        }
    }

    private void DeferPhotoExchange(string scope, NpepException error, bool isUpload)
    {
        // KV applies the same rate-limit bucket to lease and upload.
        if (error is { Status: 429, Code: "RATE_LIMITED", RetryAfterSeconds: not null })
            _photoLeaseRetry = _photoUploadRetry = NewStudyRetry(scope, error);
        else if (error.RetryAfterSeconds is not null || isUpload && error.Status is >= 500)
        {
            if (isUpload) _photoUploadRetry = NewStudyRetry(scope, error);
            else _photoLeaseRetry = NewStudyRetry(scope, error);
        }
    }
}
