using System.Windows.Controls;

namespace NPEduTools.App;

internal static partial class HomeRecordingPresentation
{
    public static void ShowMark(TextBlock marker, string mark, string brush)
    {
        marker.Text = mark;
        marker.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }
}
