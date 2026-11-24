using System.Windows;

namespace NPEduTools.App;

public partial class StudyActionConfirmation : Window
{
    private StudyActionConfirmation(string action, string effect)
    {
        InitializeComponent();
        Title = action;
        EffectText.Text = effect;
        ConfirmAction.Content = action;
        DesktopDialogSizing.Attach(this);
        Loaded += (_, _) => CancelAction.Focus();
    }

    internal static bool Ask(Window owner, string action, string effect)
    {
        owner = NoiseManagementDialog.PrepareOwner(owner);
        bool ownerOnScreen = owner.IsVisible && owner.WindowState != WindowState.Minimized;
        var dialog = new StudyActionConfirmation(action, effect) { Owner = owner, Topmost = !ownerOnScreen || owner.Topmost };
        if (ownerOnScreen) dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        return dialog.ShowDialog() == true;
    }

    private void ConfirmClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
