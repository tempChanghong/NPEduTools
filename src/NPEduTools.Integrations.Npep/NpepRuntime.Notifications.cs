using System.Diagnostics;
using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private NpepInbox? _inbox;
    private Task _notificationLoop = Task.CompletedTask;
    private string? _inboxError;
    private int _notificationWake;
    private void InitializeNotifications()
    {
        if (_device is null) return;
        try { _inbox = new(_device.DataDirectory); }
        catch (Exception error) when (Handled(error)) { _inboxError = "通知缓存无法读取，原文件已保留；状态上报不受影响。"; }
        if (_inbox is not null) _notificationLoop = Task.Run(NotificationLoopAsync);
    }
    private HostResponse HandleNotifications(HostRequest request)
    {
        var command = request.Notification;
        if (command is null || !NpepNotificationContract.Valid(command))
            return new(Protocol.Version, request.RequestId, "Rejected", "InvalidNotificationCommand", "通知请求无效。");
        var state = Snapshot();
        bool allowed = !_disposed && !state.Busy && state.State == "ACTIVE" && !state.ReportingPaused && state.Connection != "STOPPED";
        try
        {
            if (_inbox is null) return new(Protocol.Version, request.RequestId, "Succeeded", null, "通知状态",
                Inbox: new(null, "UNAVAILABLE", _inboxError ?? "通知服务尚未就绪。", 0, []));
            if (command.Action is "displayed" or "dismissed" && (!allowed || !_inbox.Mark(command)))
                return new(Protocol.Version, request.RequestId, "Rejected", "NoticeChanged", "通知或连接已变化，请刷新。");
            return new(Protocol.Version, request.RequestId, "Succeeded", null, "通知状态", Inbox: _inbox.Snapshot(allowed, command));
        }
        catch (Exception e) when (Handled(e)) { return new(Protocol.Version, request.RequestId, "Rejected", "NotificationStoreUnavailable", "通知状态未能保存，请重试；不会伪报成功。"); }
    }

    private async Task NotificationLoopAsync()
    {
        long attemptedAt = 0; double delay = 0; int failures = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try { await Task.Delay(250, _lifetime.Token); } catch (OperationCanceledException) { break; }
            if (Interlocked.Exchange(ref _notificationWake, 0) == 1) delay = 0;
            if (Stopwatch.GetElapsedTime(attemptedAt).TotalSeconds < delay || !await _work.WaitAsync(0)) continue;
            bool attempted = false;
            try
            {
                lock (_sync)
                {
                    if (_disposed || _state.Busy || _blocked) continue;
                    _network = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                }
                var view = _device!.View();
                if (view.Text("state") != "ACTIVE" || view["reportingPaused"]?.GetValue<bool>() == true) continue;
                attempted = true;
                string scope = _device.NotificationScope(); _inbox!.Prepare(scope);
                // No partial page is applied. A fresh complete snapshot is required after a restart.
                var snapshot = await _device.FetchNotificationsAsync(_network.Token);
                _inbox.Apply(scope, snapshot);
                if (_inbox.Pending() is { } pending)
                {
                    try { _inbox.Complete(pending, await _device.SendNotificationReceiptsAsync(pending, _network.Token)); }
                    catch (Exception error) when (error is HttpRequestException or System.Security.Authentication.AuthenticationException || error is OperationCanceledException && !_network.IsCancellationRequested ||
                        error is NpepException { Status: 429 or >= 500 })
                    {
                        // A lost receipt must not suppress a notification already durably received.
                        _inbox.PendingReceiptMessage();
                        delay = Math.Max(snapshot.PollAfterSeconds, (error as NpepException)?.RetryAfterSeconds ?? 0);
                        continue;
                    }
                }
                delay = snapshot.PollAfterSeconds; failures = 0;
            }
            catch (OperationCanceledException) when (_network?.IsCancellationRequested == true) { }
            catch (Exception error) when (Handled(error) || error is InvalidOperationException or FormatException)
            {
                bool auth = _device!.View().Text("state") == "SUSPENDED";
                string code = IsTlsFailure(error) ? "TLS_VALIDATION_FAILED" : error is NpepException n ? n.Code : error is HttpRequestException or OperationCanceledException ? "NETWORK_UNAVAILABLE" : "NOTIFICATION_STORE_UNAVAILABLE";
                _inbox!.Status(auth ? "DISABLED" : code == "NOTIFICATIONS_UNSUPPORTED" ? "UNSUPPORTED" : "OFFLINE", code switch
                {
                    "NOTIFICATIONS_UNSUPPORTED" => "学校服务尚未提供通知接口；设备状态继续上报。",
                    "TLS_VALIDATION_FAILED" => "HTTPS 证书验证失败，通知将持续自动重试；每次重试仍严格验证证书。",
                    "SNAPSHOT_INVALIDATED" or "SNAPSHOT_EXPIRED" => "通知列表在同步期间发生变化，将重新获取完整列表。",
                    _ => "通知同步未完成，将重试；错误：" + code
                });
                if (auth) Publish("STOPPED", "设备授权已失效，通知与状态上报均已停止。", code);
                delay = code == "NOTIFICATIONS_UNSUPPORTED" ? 300 : Math.Max(Math.Min(60, 5 * (1 << Math.Min(failures++, 4))), (error as NpepException)?.RetryAfterSeconds ?? 0) * (1 + Random.Shared.NextDouble() * .2);
            }
            finally
            {
                lock (_sync) { _network?.Dispose(); _network = null; }
                if (attempted) attemptedAt = Stopwatch.GetTimestamp();
                _work.Release();
            }
        }
    }
}
