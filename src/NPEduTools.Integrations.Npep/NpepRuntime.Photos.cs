using System.Diagnostics;
using System.Text.Json.Nodes;
namespace NPEduTools.Integrations.Npep;
public sealed partial class NpepRuntime
{
    private Task _studyPhotosLoop = Task.CompletedTask;
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
                        if (state is { State: "UNPAIRED" or "SUSPENDED" } or { ReportingPaused: true } || state.Busy && !ContinuingStudyResume)
                            photos.PhotoFailed("PHOTO_NOT_ELIGIBLE");
                        else if (state is { State: "ACTIVE", Connection: "ONLINE", Busy: false } && (unsupported is null || Stopwatch.GetElapsedTime(unsupported.Value).TotalSeconds >= 60))
                        {
                            var code = await StudyPhotoCycleAsync(photos, _lifetime.Token);
                            if (code == "STUDY_PHOTOS_UNSUPPORTED") unsupported = Stopwatch.GetTimestamp();
                            else if (code is null) unsupported = null;
                        }
                    }
                    catch (Exception e) when (Handled(e) || e is InvalidDataException)
                    {
                        var code = e is NpepException n ? n.Code : "PHOTO_OFFLINE";
                        if (code == "STUDY_PHOTOS_UNSUPPORTED") unsupported = Stopwatch.GetTimestamp();
                        photos.PhotoFailed(code);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }
    internal async Task<string?> StudyPhotoCycleAsync(INpepStudyPhotos photos, CancellationToken token)
    {
        Guid? eventId = null;
        try
        {
            var context = await StudyContextAsync(token);
            string? renewalError = null;
            if (photos.PhotoLeaseRequest() is { } request)
            {
                request["requestId"] = NpepProtocol.Id(); request["context"] = context.Copy();
                var at = Stopwatch.GetTimestamp();
                try
                {
                    var lease = await _device!.StudyPhotoAsync(request, null, token);
                    await RequireStudyContextAsync(context, token);
                    photos.ConfirmPhotoLease(lease, Stopwatch.GetElapsedTime(at));
                }
                catch (Exception e) when (!token.IsCancellationRequested && TransientStudyExchangeFailure(e))
                {
                    // A failed renewal grants no new capture time. It must not block a
                    // previously permitted photo, whose original lease is checked by KV.
                    await RequireStudyContextAsync(context, token);
                    renewalError = e is NpepException n ? n.Code : "PHOTO_OFFLINE";
                }
            }
            // An ended window may still upload an already permitted, encrypted photo.
            await RequireStudyContextAsync(context, token);
            if (photos.PendingPhoto() is { } packet)
            {
                eventId = Guid.Parse(packet.Metadata.Text("eventId"));
                if (!JsonNode.DeepEquals(JsonNode.Parse(packet.Scope), context["identity"])) throw new NpepException("BINDING_CHANGED");
                var reply = await _device!.StudyPhotoAsync(null, packet, token);
                await RequireStudyContextAsync(context, token);
                if (reply.Text("eventId") != packet.Metadata.Text("eventId")) throw new NpepException("INVALID_RESPONSE");
                photos.PhotoStored(Guid.Parse(reply.Text("eventId")));
            }
            if (renewalError is not null) photos.PhotoFailed(renewalError);
            return renewalError;
        }
        catch (Exception e) when (Handled(e) || e is InvalidDataException)
        {
            var code = e is NpepException n ? n.Code : "PHOTO_OFFLINE";
            photos.PhotoFailed(code, eventId);
            return code;
        }
    }
}
