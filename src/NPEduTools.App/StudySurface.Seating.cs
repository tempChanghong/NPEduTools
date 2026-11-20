using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
namespace NPEduTools.App;
public partial class StudySurface
{
    public event Action? SeatingUploadRequested;
    private BitmapSource? _seatingImage, _pendingSeating;
    private string? _seatingScope, _seatingHash, _pendingSeatingHash;
    private int _seatingRevision, _pendingSeatingRevision;
    private bool _seatingZoom, _seatingAutomatic;
    private IInputElement? _seatingReturnFocus;
    private bool SeatingOpen => SeatingOverlay.Visibility == Visibility.Visible;
    private readonly NPEduTools.Core.StudySeatingAutoDisplay _autoSeating = new();
    public string? SeatingHash => _seatingHash;
    public int SeatingRevision => _seatingRevision;
    public bool SeatingVisible => SeatingOpen && StageNotice.Visibility != Visibility.Visible && FreshRenderAgeMs is not null;
    public bool HasSeating(string hash, int revision) => _seatingHash == hash && _seatingRevision == revision || _pendingSeatingHash == hash && _pendingSeatingRevision == revision;
    public void SeatingScope(string scope) { if (_seatingScope == scope) return; ClearSeating(); _seatingScope = scope; }
    public void ClearSeating()
    {
        _seatingImage = _pendingSeating = null; _seatingHash = _pendingSeatingHash = _seatingScope = null; _seatingRevision = _pendingSeatingRevision = 0;
        SeatingUpdate.Text = "";
        HideSeatingOverlay();
    }
    public void ApplySeating(string hash, int revision, BitmapSource image)
    {
        if (HasSeating(hash, revision)) return;
        if (SeatingOpen && _seatingImage is not null) {
            _pendingSeating = image; _pendingSeatingHash = hash; _pendingSeatingRevision = revision;
            SeatingUpdate.Text = "新图就绪 · 关闭后切换";
        } else {
            _seatingImage = image; _seatingHash = hash; _seatingRevision = revision;
            if (SeatingOpen) SeatingImage.Source = image;
        }
        RenderAutomaticSeating(); RenderSeating(); // A downloaded image opens only inside the initial scheduled seat window.
    }
    private void RenderSeating()
    {
        bool allowed = !_paused && _timeline.Fresh && _timeline.State is { Manual: null, PolicyState: "APPLIED", Phase: "Preparing" or "Active" } && StageNotice.Visibility != Visibility.Visible;
        SeatingButton.IsEnabled = UploadSeating.IsEnabled = allowed;
        SeatingZoom.IsEnabled = allowed && _seatingImage is not null;
        SeatingEmpty.Visibility = _seatingImage is null ? Visibility.Visible : Visibility.Collapsed;
        SeatingStatus.Text = _seatingImage is null ? "自习座位表暂不可用" : _pendingSeating is not null ? "新图就绪 · 关闭后切换" : $"自习座位表 · 版本 {_seatingRevision}";
    }
    private void OpenSeatingClicked(object sender, RoutedEventArgs e)
    {
        if (SeatingOpen || !SeatingButton.IsEnabled) return;
        _autoSeating.ManualChoice(); _seatingAutomatic = false;
        ShowSeatingView();
    }
    private void ShowSeatingView()
    {
        if (!ReaderOpen && StudyOverview.IsKeyboardFocusWithin) _seatingReturnFocus = Keyboard.FocusedElement;
        CancelHold();
        SeatingImage.Source = _seatingImage; _seatingZoom = false; SeatingImage.Width = 1840; SeatingImage.Height = 900;
        SeatingZoom.Content = "放大"; SeatingScroll.ScrollToHome(); SeatingOverlay.Visibility = Visibility.Visible;
        UpdateOverviewInput();
        FocusOpenSeating(entering: true);
    }
    private void FocusOpenSeating(bool entering = false)
    {
        if (!_paused && SeatingOverlay.IsVisible && StageNotice.Visibility != Visibility.Visible &&
            (entering || !SeatingOverlay.IsKeyboardFocusWithin))
            CloseSeating.Focus();
    }
    private void RenderAutomaticSeating()
    {
        if (_timeline.State is not { } state) return;
        bool wanted = !_paused && _timeline.Fresh && _autoSeating.WantsImage(state);
        if (state is not { Manual: null, PolicyState: "APPLIED", Phase: "Preparing" or "Active" })
        {
            if (SeatingOpen) CloseSeatingView();
            return;
        }
        if (_seatingAutomatic && !wanted) { _seatingAutomatic = false; CloseSeatingView(); }
        else if (wanted && _seatingImage is not null && !SeatingOpen) {
            _seatingAutomatic = true; ShowSeatingView();
        }
    }
    private void CloseSeatingView()
    {
        _seatingAutomatic = false;
        if (_pendingSeating is { } next) { _seatingImage = next; _seatingHash = _pendingSeatingHash; _seatingRevision = _pendingSeatingRevision; }
        _pendingSeating = null; _pendingSeatingHash = null; _pendingSeatingRevision = 0; SeatingUpdate.Text = "";
        HideSeatingOverlay();
    }
    private void HideSeatingOverlay()
    {
        bool wasOpen = SeatingOpen;
        var previous = _seatingReturnFocus;
        _seatingReturnFocus = null;
        SeatingOverlay.Visibility = Visibility.Collapsed;
        SeatingImage.Source = null;
        UpdateOverviewInput();
        RenderSeating();
        if (wasOpen && !_paused && IsVisible && !ReaderOpen && StageNotice.Visibility != Visibility.Visible &&
            (previous is null || !ReferenceEquals(Keyboard.Focus(previous), previous)))
            SeatingButton.Focus();
    }
    private void CloseSeatingClicked(object sender, RoutedEventArgs e) { _autoSeating.ManualChoice(); CloseSeatingView(); }
    private void UploadSeatingClicked(object sender, RoutedEventArgs e) { if (UploadSeating.IsEnabled) SeatingUploadRequested?.Invoke(); }
    private void ZoomSeatingClicked(object sender, RoutedEventArgs e)
    {
        _seatingZoom = !_seatingZoom; SeatingImage.Width = _seatingZoom ? 3680 : 1840; SeatingImage.Height = _seatingZoom ? 1800 : 900;
        SeatingZoom.Content = _seatingZoom ? "适合屏幕" : "放大";
    }
}
