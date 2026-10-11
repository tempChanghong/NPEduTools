using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class RemoteExamHistoryView : UserControl
{
    public RemoteExamHistoryView() => InitializeComponent();

    public void ShowHistory(RemoteExamHistory[]? history)
    {
        var items = history?.Select(RemoteExamPresentation.History).ToArray() ?? [];
        HistoryItems.ItemsSource = items;
        EmptyHistory.Text = history is null ? "后台状态未读取，暂时无法确认历史记录。" : "暂无记录。";
        EmptyHistory.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
