using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NPEduTools.App;

public sealed class SettingsRow : HeaderedContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(SettingsRow));
    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    private static readonly DependencyPropertyKey IsStackedPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsStacked), typeof(bool), typeof(SettingsRow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsStackedProperty = IsStackedPropertyKey.DependencyProperty;
    public bool IsStacked => (bool)GetValue(IsStackedProperty);

    protected override Size MeasureOverride(Size constraint)
    {
        // The sidebar, scrollbar and desktop scale all affect the space left for a row.
        bool stacked = constraint.Width < 640;
        if (IsStacked != stacked) SetValue(IsStackedPropertyKey, stacked);
        return base.MeasureOverride(constraint);
    }
}
