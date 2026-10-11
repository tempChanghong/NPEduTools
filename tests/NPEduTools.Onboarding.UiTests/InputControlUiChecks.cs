using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

internal static partial class Program
{
    private static void RunInputControlChecks()
    {
        // Real application resources and native WPF controls; no App startup,
        // device enumeration, production configuration or sensor acquisition.
        var date = new DatePicker { Width = 290, Margin = new Thickness(0, 8, 0, 8) };
        var until = new DatePicker { Width = 290, Margin = new Thickness(0, 0, 0, 20) };
        var password = new PasswordBox { Width = 290, MaxLength = 128 };
        var body = new StackPanel { Margin = new Thickness(30), HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(new TextBlock { Text = "生效日期 / 结束日期（留空不限）", Foreground = Brushes.Teal });
        body.Children.Add(date); body.Children.Add(until);
        body.Children.Add(new TextBlock { Text = "本机管理口令", Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(password);
        var window = new Window { Title = "输入控件隔离验证", Width = 430, Height = 340, Content = body,
            FontFamily = new FontFamily("Microsoft YaHei UI"), Background = Brushes.White };
        Exercise(window, owner =>
        {
            date.ApplyTemplate(); password.ApplyTemplate(); owner.UpdateLayout();
            var editor = (DatePickerTextBox)date.Template.FindName("PART_TextBox", date);
            editor.ApplyTemplate();
            var watermark = (ContentControl)editor.Template.FindName("PART_Watermark", editor);
            Assert(date.ActualHeight <= 46 && editor.MinHeight == 0, "date editor inherited oversized TextBox chrome");
            Assert(watermark.Visibility == Visibility.Visible, "empty date lost its watermark");
            Assert(((Border)date.Template.FindName("InputSurface", date)).CornerRadius.TopLeft == 8 &&
                   ((Border)password.Template.FindName("InputSurface", password)).CornerRadius.TopLeft == 8, "input chrome not applied");
            int changes = 0; date.SelectedDateChanged += (_, _) => changes++;
            editor.Focus(); editor.Text = new DateTime(2026, 10, 12).ToString("d");
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), 0, Key.Return)
                { RoutedEvent = Keyboard.KeyDownEvent });
            owner.UpdateLayout();
            Assert(date.SelectedDate == new DateTime(2026, 10, 12) && changes > 0, "typed date/Enter no longer commits");
            Assert(watermark.Visibility == Visibility.Collapsed, "watermark covers a selected date");
            ((Button)date.Template.FindName("PART_Button", date)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => date.IsDropDownOpen, "calendar button no longer opens the popup");
            var popup = (Popup)date.Template.FindName("PART_Popup", date);
            Assert(popup.IsOpen && popup.Child is not null, "native Calendar is missing");
            Snapshot(owner, "input-controls-calendar.png");
            var calendar = popup.Child as Calendar ?? Descendants<Calendar>(popup.Child!).Single();
            calendar.SelectedDate = new DateTime(2026, 10, 14);
            owner.UpdateLayout();
            Assert(date.SelectedDate == new DateTime(2026, 10, 14), "calendar selection no longer updates date");
            date.IsDropDownOpen = false;
            editor.Text = "";
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), 0, Key.Return)
                { RoutedEvent = Keyboard.KeyDownEvent });
            owner.UpdateLayout();
            Assert(date.SelectedDate is null, "cleared date/Enter no longer commits null");
            Assert(date.Text.Length == 0 && watermark.Visibility == Visibility.Visible, "clearing the date lost the unlimited/empty state");
            password.Password = "fixture-only"; password.Focus(); owner.UpdateLayout();
            Assert(password.Password == "fixture-only" && password.PasswordChar == '●', "password value or native masking changed");
            Snapshot(owner, "input-controls-empty-password.png");
            date.SelectedDate = new DateTime(2026, 10, 12); until.SelectedDate = new DateTime(2026, 10, 30);
            password.Clear(); owner.UpdateLayout();
            Assert(password.Password.Length == 0, "password clear no longer works");
            Snapshot(owner, "input-controls-dates.png");
            Checks.Add("Real WPF styles: compact dates, empty watermark, typed Enter commit, native calendar selection, clear to null; rounded password keeps masked content/focus/clear; no devices");
            owner.Close();
        });
    }
}
