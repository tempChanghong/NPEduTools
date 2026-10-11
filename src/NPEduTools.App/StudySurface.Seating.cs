using System.Windows;
using System.Windows.Media.Imaging;
namespace NPEduTools.App;
public partial class StudySurface
{
    private BitmapSource? _seatingImage, _pendingSeating;
    private string? _seatingScope, _seatingHash, _pendingSeatingHash;
    private int _seatingRevision, _pendingSeatingRevision;
    private bool _seatingZoom;
    public string? SeatingHash => _seatingHash;
    public int SeatingRevision => _seatingRevision;
    public bool SeatingVisible => SeatingOverlay.Visibility == Visibility.Visible && StageNotice.Visibility != Visibility.Visible && FreshRenderAgeMs is not null;
    public bool HasSeating(string hash, int revision) => _seatingHash == hash && _seatingRevision == revision || _pendingSeatingHash == hash && _pendingSeatingRevision == revision;
    public void SeatingScope(string scope) { if (_seatingScope == scope) return; ClearSeating(); _seatingScope = scope; }
    public void ClearSeating()
    {
        _seatingImage = _pendingSeating = null; _seatingHash = _pendingSeatingHash = _seatingScope = null; _seatingRevision = _pendingSeatingRevision = 0;
        SeatingImage.Source = null; SeatingOverlay.Visibility = Visibility.Collapsed; SeatingButton.IsEnabled = false;
        SeatingStatus.Text = "自习座位表暂不可用"; SeatingUpdate.Text = "";
    }
    public void ApplySeating(string hash, int revision, BitmapSource image)
    {
        if (HasSeating(hash, revision)) return;
        if (SeatingOverlay.Visibility == Visibility.Visible && _seatingImage is not null) {
            _pendingSeating = image; _pendingSeatingHash = hash; _pendingSeatingRevision = revision;
            SeatingUpdate.Text = "新自习座位表已就绪 · 关闭后切换";
        } else { _seatingImage = image; _seatingHash = hash; _seatingRevision = revision; }
        RenderSeating(); // Never opens the overlay, including initial download and replacement.
    }
    private void RenderSeating()
    {
        SeatingButton.IsEnabled = !_paused && _timeline.Fresh && _seatingImage is not null && StageNotice.Visibility != Visibility.Visible;
        SeatingStatus.Text = _seatingImage is null ? "自习座位表暂不可用" : _pendingSeating is not null ? "新图就绪 · 关闭后切换" : $"自习座位表 · 版本 {_seatingRevision}";
    }
    private void OpenSeatingClicked(object sender, RoutedEventArgs e)
    {
        if (!SeatingButton.IsEnabled || _seatingImage is null) return;
        SeatingImage.Source = _seatingImage; _seatingZoom = false; SeatingImage.Width = 1840; SeatingImage.Height = 900;
        SeatingZoom.Content = "放大"; SeatingScroll.ScrollToHome(); SeatingOverlay.Visibility = Visibility.Visible;
    }
    private void CloseSeatingView()
    {
        SeatingOverlay.Visibility = Visibility.Collapsed; SeatingImage.Source = null;
        if (_pendingSeating is { } next) { _seatingImage = next; _seatingHash = _pendingSeatingHash; _seatingRevision = _pendingSeatingRevision; }
        _pendingSeating = null; _pendingSeatingHash = null; _pendingSeatingRevision = 0; SeatingUpdate.Text = "";
        RenderSeating();
    }
    private void CloseSeatingClicked(object sender, RoutedEventArgs e) => CloseSeatingView();
    private void ZoomSeatingClicked(object sender, RoutedEventArgs e)
    {
        _seatingZoom = !_seatingZoom; SeatingImage.Width = _seatingZoom ? 3680 : 1840; SeatingImage.Height = _seatingZoom ? 1800 : 900;
        SeatingZoom.Content = _seatingZoom ? "适合屏幕" : "放大";
    }
}
