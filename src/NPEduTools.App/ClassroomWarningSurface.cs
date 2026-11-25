using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace NPEduTools.App;

internal static class ClassroomWarningSurface
{
    internal static Brush WindowBackground { get; } = new SolidColorBrush(Color.FromRgb(5, 12, 17));

    internal static FrameworkElement Create(string translations, UIElement explanation, int emphasizedLine = -1)
    {
        var panel = new Grid { Width = 1920, Height = 1080 };
        panel.ColumnDefinitions.Add(new() { Width = new(2.35, GridUnitType.Star) });
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
        full.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = panel });
        return full;
    }
}
