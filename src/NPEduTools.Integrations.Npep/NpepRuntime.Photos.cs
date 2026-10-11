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
                    Guid? eventId = null;
                    try
                    {
                        var state = Snapshot();
                        if (state is { State: "UNPAIRED" or "SUSPENDED" } or { ReportingPaused: true } or { Busy: true })
                            photos.PhotoFailed("PHOTO_NOT_ELIGIBLE");
                        else if (state is { State: "ACTIVE", Connection: "ONLINE" } && (unsupported is null || Stopwatch.GetElapsedTime(unsupported.Value).TotalSeconds >= 60))
                        {
                            var context = await StudyContextAsync(_lifetime.Token);
                            if (photos.PhotoLeaseRequest() is { } request)
                            {
                                request["requestId"] = NpepProtocol.Id(); request["context"] = context.Copy();
                                var at = Stopwatch.GetTimestamp();
                                var lease = await _device.StudyPhotoAsync(request, null, _lifetime.Token);
                                await RequireStudyContextAsync(context, _lifetime.Token);
                                photos.ConfirmPhotoLease(lease, Stopwatch.GetElapsedTime(at));
                            }
                            // An ended window may still upload an already permitted, encrypted photo.
                            if (photos.PendingPhoto() is { } packet)
                            {
                                eventId = Guid.Parse(packet.Metadata.Text("eventId"));
                                if (!JsonNode.DeepEquals(JsonNode.Parse(packet.Scope), context["identity"])) throw new NpepException("BINDING_CHANGED");
                                var reply = await _device.StudyPhotoAsync(null, packet, _lifetime.Token);
                                await RequireStudyContextAsync(context, _lifetime.Token);
                                if (reply.Text("eventId") != packet.Metadata.Text("eventId")) throw new NpepException("INVALID_RESPONSE");
                                photos.PhotoStored(Guid.Parse(reply.Text("eventId")));
                            }
                            unsupported = null;
                        }
                    }
                    catch (Exception e) when (Handled(e) || e is InvalidDataException)
                    {
                        var code = e is NpepException n ? n.Code : "PHOTO_OFFLINE";
                        if (code == "STUDY_PHOTOS_UNSUPPORTED") unsupported = Stopwatch.GetTimestamp();
                        photos.PhotoFailed(code, eventId);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }
}
