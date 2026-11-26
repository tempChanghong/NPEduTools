using System.IO;
using System.Text.Json;
using System.Windows;
using NPEduTools.Contracts;

namespace NPEduTools.App;

internal enum SidebarDrawState { Succeeded, Pending, Failed, SetupRequired, Reconciled }

internal sealed record SidebarDrawReceipt(SidebarDrawState State, string Detail, Guid? RequestId = null);

public partial class SecRandomWindow
{
    private Guid? _sidebarPendingDraw;
    private SecRandomOperation? _sidebarObservedDraw;
    private SecRandomOperation? _sidebarVerifiedDraw;
    private bool _sidebarReceiptAbsent;

    internal event Action<SidebarDrawReceipt>? SidebarDrawVerified;

    internal async Task<SidebarDrawReceipt> QuickDrawFromSidebarAsync()
    {
        if (_sidebarPendingDraw is { } pending) return PendingSidebarDraw(pending);
        if (_closing || _busy) return new(SidebarDrawState.Failed, "课堂点名正在处理其他操作，请稍候。");
        _generation++;
        _busy = true;
        _sidebarVerifiedDraw = null;
        Message.Text = "";
        Render(_state);
        Guid? submittedId = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var preflight = await RequestAsync("secrandom.status", cancellationToken: deadline.Token);
            Render(preflight.SecRandom);
            if (preflight.Outcome != "Succeeded" || _state is not { Error: null })
                return new(SidebarDrawState.Failed, "暂时无法核对课堂点名后台。");
            if (string.IsNullOrWhiteSpace(_state.ExecutablePath))
                return new(SidebarDrawState.SetupRequired, "请先在课堂点名设置 SecRandom 程序位置。");
            if (_state.Operation is { State: "Unknown", Action: "quick-draw" } unresolved)
                return PendingSidebarDraw(unresolved.RequestId, unresolved);
            if (_state.Operation?.State == "Unknown")
                return new(SidebarDrawState.Failed, "请先在课堂点名核实上次操作。");
            if (_state.Operation?.State == "Running")
                return new(SidebarDrawState.Failed, "SecRandom 正在执行另一项操作，请稍候。");

            Guid id = Guid.NewGuid();
            submittedId = id;
            var response = await RequestAsync("secrandom.command", command: new("quick-draw"), requestId: id, cancellationToken: deadline.Token);
            Render(response.SecRandom);
            if (response.Outcome == "Rejected")
            {
                if (response.SecRandom?.Operation is { Action: "quick-draw", State: "Unknown" } original)
                    return PendingSidebarDraw(original.RequestId, original);
                return new(response.ErrorCode == "PathMissing" ? SidebarDrawState.SetupRequired : SidebarDrawState.Failed,
                    "后台未接受本次闪抽，请在课堂点名核对。");
            }
            if (response.Outcome is not ("Accepted" or "Succeeded")) return PendingSidebarDraw(id);
            var operation = MatchingDraw(response, id);
            while (operation is null or { State: "Running" })
            {
                await Task.Delay(250, deadline.Token);
                var lookup = await ReadSidebarDrawAsync(id, deadline.Token);
                operation = lookup.Operation;
                if (operation is null) return PendingSidebarDraw(id);
            }
            if (operation.State is not ("Succeeded" or "Failed" or "Acknowledged")) return PendingSidebarDraw(id, operation);
            // The workspace can still show this exact result even if the general status
            // snapshot has already moved on to a different operation.
            _sidebarVerifiedDraw = operation;
            return ConfirmedSidebarDraw(operation);
        }
        catch (Exception error) when (IsSidebarConnectionError(error))
        {
            Render(null);
            if (submittedId is { } id) return PendingSidebarDraw(id);
            Message.Text = "未收到可靠回执，未提交新的闪抽。";
            return new(SidebarDrawState.Failed, "后台连接待确认，未提交新的闪抽。");
        }
        finally { _busy = false; Render(_state); }
    }

    internal async Task VerifySidebarDrawAsync(bool confirmMissing = false)
    {
        if (_closing || _busy || _sidebarPendingDraw is not { } id) return;
        _generation++;
        _busy = true;
        Render(_state);
        try
        {
            var (operation, absent) = await ReadSidebarDrawAsync(id, _lifetime.Token);
            _sidebarReceiptAbsent = absent;
            _sidebarObservedDraw = operation;
            if (operation is { State: "Succeeded" or "Failed" or "Acknowledged" })
            {
                _sidebarVerifiedDraw = operation;
                CompleteSidebarVerification(ConfirmedSidebarDraw(operation));
            }
            else if (confirmMissing && absent) MarkSidebarDrawReconciled(id);
        }
        catch (Exception error) when (IsSidebarConnectionError(error))
        {
            _sidebarReceiptAbsent = false;
            _sidebarObservedDraw = null;
            Render(null);
        }
        finally { _busy = false; Render(_state); }
    }

    private void CompleteSidebarVerification(SidebarDrawReceipt receipt)
    {
        if (_pendingReconciliation?.RequestId == _sidebarPendingDraw) _pendingReconciliation = null;
        _sidebarPendingDraw = null;
        _sidebarObservedDraw = null;
        _sidebarReceiptAbsent = false;
        Message.Text = receipt.Detail;
        SidebarDrawVerified?.Invoke(receipt);
    }

    private void MarkSidebarDrawReconciled(Guid id)
    {
        const string detail = "已核实这次闪抽。";
        _sidebarVerifiedDraw = new(id, "quick-draw", "Acknowledged", detail, DateTimeOffset.UtcNow);
        CompleteSidebarVerification(new(SidebarDrawState.Reconciled, detail, id));
    }

    private SidebarDrawReceipt PendingSidebarDraw(Guid id, SecRandomOperation? operation = null)
    {
        _sidebarPendingDraw = id;
        _sidebarObservedDraw = operation;
        _sidebarReceiptAbsent = false;
        return new(SidebarDrawState.Pending, "回执未确认，请到课堂点名核实。", id);
    }

    private static SidebarDrawReceipt ConfirmedSidebarDraw(SecRandomOperation operation) => operation.State switch
    {
        "Succeeded" => new(SidebarDrawState.Succeeded, "本次闪抽已完成。", operation.RequestId),
        "Acknowledged" => new(SidebarDrawState.Reconciled, "已核实这次闪抽。", operation.RequestId),
        _ => new(SidebarDrawState.Failed, "本次闪抽未完成，请在课堂点名查看。", operation.RequestId)
    };

    private async Task<(SecRandomOperation? Operation, bool Absent)> ReadSidebarDrawAsync(Guid id, CancellationToken token)
    {
        var response = await RequestAsync("secrandom.receipt", operationId: id, cancellationToken: token);
        if (response.ErrorCode == "UnknownCapability")
            response = await RequestAsync("secrandom.status", cancellationToken: token);
        return (response.Outcome == "Succeeded" ? MatchingDraw(response, id) : null,
            response is { Outcome: "Rejected", ErrorCode: "SecRandomReceiptNotFound" });
    }

    private static SecRandomOperation? MatchingDraw(HostResponse response, Guid id) =>
        response.SecRandom?.Operation is { Action: "quick-draw" } operation && operation.RequestId == id
            ? operation : null;

    private static bool IsSidebarConnectionError(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or JsonException;

    private async void CheckSidebarDrawClicked(object sender, RoutedEventArgs e) => await VerifySidebarDrawAsync();
}
