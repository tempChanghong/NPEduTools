using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace NPEduTools.App;

internal static class ClassroomWarningSurface
{
    internal static Brush WindowBackground { get; } = new SolidColorBrush(Color.FromRgb(5, 12, 17));

    internal static FrameworkElement Create(string translations, FrameworkElement explanation, int emphasizedLine = -1)
    {
        var panel = new Grid { Width = 1920, Height = 1080 };
        panel.ColumnDefinitions.Add(new() { Width = new(emphasizedLine < 0 ? 2.35 : 1.35, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var left = new TextBlock
        {
            FontSize = 49, LineHeight = 91, Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center, Margin = new(64, 40, 40, 40),
            TextAlignment = TextAlignment.Left
        };
        if (emphasizedLine < 0)
        {
            left.FontFamily = new("Segoe UI");
            left.Text = translations;
        }
        else
        {
            left.FontWeight = FontWeights.Bold;
            var lines = translations.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) left.Inlines.Add(new LineBreak());
                var row = new Run(lines[i]);
                if (i == emphasizedLine)
                {
                    row.FontSize = 64;
                    row.FontWeight = FontWeights.Bold;
                }
                left.Inlines.Add(row);
            }
        }
        Grid.SetColumn(explanation, 1);
        panel.Children.Add(left);
        panel.Children.Add(explanation);
        var full = new Grid { Background = new SolidColorBrush(Color.FromArgb(238, 158, 23, 32)) };
        Viewbox view = emphasizedLine < 0 ? new Viewbox() : new WebsiteWarningViewbox(panel, explanation);
        view.Child = panel;
        view.Stretch = Stretch.Uniform;
        full.Children.Add(view);
        return full;
    }

    private sealed class WebsiteWarningViewbox(Grid panel, FrameworkElement explanation) : Viewbox
    {
        private bool _stacked;
        private readonly Thickness _sideMargin = explanation.Margin;

        protected override Size MeasureOverride(Size constraint)
        {
            bool stack = double.IsFinite(constraint.Height) && constraint.Height > constraint.Width;
            if (stack != _stacked)
            {
                _stacked = stack;
                panel.Width = stack ? 1080 : 1920;
                panel.Height = stack ? double.NaN : 1080;
                panel.RowDefinitions.Clear();
                if (stack)
                {
                    panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
                }
                Grid.SetColumnSpan(panel.Children[0], stack ? 2 : 1);
                Grid.SetColumn(explanation, stack ? 0 : 1);
                Grid.SetColumnSpan(explanation, stack ? 2 : 1);
                Grid.SetRow(explanation, stack ? 1 : 0);
                explanation.Margin = stack ? new Thickness(64, 0, 64, 52) : _sideMargin;
            }
            return base.MeasureOverride(constraint);
        }
    }
}
