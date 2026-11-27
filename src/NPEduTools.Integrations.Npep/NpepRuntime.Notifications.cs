using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private NpepInbox? _inbox;
    private Task _notificationLoop = Task.CompletedTask;
    private CancellationTokenSource? _notificationNetwork;
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
        try
        {
            lock (_sync)
            {
                if (_inbox is null) return new(Protocol.Version, request.RequestId, "Succeeded", null, "通知状态",
                    Inbox: new(null, "UNAVAILABLE", _inboxError ?? "通知服务尚未就绪。", 0, []));
                string? scope = CanExchangeNotifications() ? _device!.ActiveNotificationScope() : null;
                bool awaitingScope = scope is not null && !_inbox.MatchesScope(scope);
                bool allowed = scope is not null && !awaitingScope;
                if (command.Action is "displayed" or "dismissed" && (!allowed || !_inbox.Mark(command)))
                    return new(Protocol.Version, request.RequestId, "Rejected", "NoticeChanged", "通知或连接已变化，请刷新。");
                return new(Protocol.Version, request.RequestId, "Succeeded", null, "通知状态",
                    Inbox: awaitingScope ? new(null, "WAITING", "正在同步当前绑定的学校通知。", 0, []) : _inbox.Snapshot(allowed, command));
            }
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
            if (Stopwatch.GetElapsedTime(attemptedAt).TotalSeconds < delay) continue;
            CancellationTokenSource cancellation;
            lock (_sync)
            {
                if (!CanExchangeNotifications()) continue;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _notificationNetwork = cancellation;
            }
            using (cancellation)
            try
            {
                string scope;
                lock (_sync)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    scope = _device!.ActiveNotificationScope() ?? throw new NpepException("SESSION_SUPERSEDED");
                    _inbox!.Prepare(scope);
                }
                // No partial page is applied. A fresh complete snapshot is required after a restart.
                var snapshot = await _device.FetchNotificationsAsync(cancellation.Token, scope);
                JsonObject? pending;
                lock (_sync)
                {
                    RequireNotificationScope(scope, cancellation.Token);
                    _inbox.Apply(scope, snapshot);
                    pending = _inbox.Pending();
                }
                if (pending is not null)
                {
                    try
                    {
                        var reply = await _device.SendNotificationReceiptsAsync(pending, cancellation.Token, scope);
                        lock (_sync)
                        {
                            RequireNotificationScope(scope, cancellation.Token);
                            _inbox.Complete(pending, reply);
                        }
                    }
                    catch (Exception error) when (!cancellation.IsCancellationRequested && NotificationReceiptUnconfirmed(error))
                    {
                        // A lost receipt must not suppress a notification already durably received.
                        lock (_sync)
                        {
                            RequireNotificationScope(scope, cancellation.Token);
                            _inbox.PendingReceiptMessage();
                        }
                        delay = Math.Max(snapshot.PollAfterSeconds, (error as NpepException)?.RetryAfterSeconds ?? 0);
                        continue;
                    }
                }
                delay = snapshot.PollAfterSeconds; failures = 0;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) when (Handled(error) || error is InvalidOperationException or FormatException)
            {
                bool auth;
                string code = IsTlsFailure(error) ? "TLS_VALIDATION_FAILED" : error is NpepException n ? n.Code : error is HttpRequestException or OperationCanceledException ? "NETWORK_UNAVAILABLE" : "NOTIFICATION_STORE_UNAVAILABLE";
                lock (_sync)
                {
                    if (_disposed || cancellation.IsCancellationRequested || _state.Busy) continue;
                    auth = _device!.View().Text("state") == "SUSPENDED";
                    _inbox!.Status(auth ? "DISABLED" : code == "NOTIFICATIONS_UNSUPPORTED" ? "UNSUPPORTED" : "OFFLINE", code switch
                    {
                        "NOTIFICATIONS_UNSUPPORTED" => "学校服务尚未提供通知接口；设备状态继续上报。",
                        "TLS_VALIDATION_FAILED" => "HTTPS 证书验证失败，通知将持续自动重试；每次重试仍严格验证证书。",
                        "SNAPSHOT_INVALIDATED" or "SNAPSHOT_EXPIRED" => "通知列表在同步期间发生变化，将重新获取完整列表。",
                        _ => "通知同步未完成，将重试；错误：" + code
                    });
                }
                if (auth) Publish("STOPPED", "设备授权已失效，通知与状态上报均已停止。", code, requireSuspended: true);
                delay = code == "NOTIFICATIONS_UNSUPPORTED" ? 300 : Math.Max(Math.Min(60, 5 * (1 << Math.Min(failures++, 4))), (error as NpepException)?.RetryAfterSeconds ?? 0) * (1 + Random.Shared.NextDouble() * .2);
            }
            finally
            {
                lock (_sync) _notificationNetwork = null;
                attemptedAt = Stopwatch.GetTimestamp();
            }
        }
    }

    private bool CanExchangeNotifications() => _state is { State: "ACTIVE", Busy: false, ReportingPaused: false } &&
        _state.Connection != "STOPPED" && !_disposed && !_blocked;

    private static bool NotificationReceiptUnconfirmed(Exception error) => error is HttpRequestException or
        System.Security.Authentication.AuthenticationException or OperationCanceledException or NpepException { Status: 408 or 429 or >= 500 } or
        NpepException { Code: "INVALID_RESPONSE" or "REDIRECT_REJECTED" };

    // Commit while holding _sync so an accepted pause or rebind invalidates a late page or ACK.
    private void RequireNotificationScope(string scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanExchangeNotifications() || _device!.ActiveNotificationScope() != scope)
            throw new NpepException("SESSION_SUPERSEDED");
    }
}
