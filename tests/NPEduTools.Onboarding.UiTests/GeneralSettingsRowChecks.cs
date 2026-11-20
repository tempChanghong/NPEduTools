using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NPEduTools.App;

internal static partial class Program
{
    private static void RunGeneralSettingsRowChecks()
    {
        string pipe = "NPEduTools.Test.settings-rows." + Guid.NewGuid().ToString("N");
        var main = PrivateWorkspace(pipe);
        QuickAccessWindow Rail() => new(pipe, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { });
        var rail = Rail();
        string placementPath = (string)typeof(QuickAccessWindow).GetField("_settingsPath", WorkspaceFlags)!.GetValue(rail)!;
        string densityPath = StartupPreferencesStore.PathFor(pipe) + ".density";
        try
        {
            typeof(MainWindow).GetMethod("InitializeOnboarding", WorkspaceFlags)!.Invoke(main, null);
            typeof(MainWindow).GetField("_densityReady", WorkspaceFlags)!.SetValue(main, true);
            typeof(MainWindow).GetField("_quick", WorkspaceFlags)!.SetValue(main, rail);
            Exercise(main, owner =>
            {
                Click(owner, "SettingsNav");
                var density = (ComboBox)owner.FindName("DensityChoiceBox");
                var left = (RadioButton)owner.FindName("DockLeft");
                var right = (RadioButton)owner.FindName("DockRight");
                var group = (FrameworkElement)owner.FindName("DisplayOperationSettings");
                right.IsChecked = !rail.LeftSide;
                ((FrameworkElement)owner.Content).LayoutTransform = Transform.Identity;
                owner.Width = 1040; owner.Height = 760; owner.UpdateLayout();
                var wizard = Button(owner, "OpenOnboarding", true);
                var scroll = (ScrollViewer)owner.FindName("SettingsScroll");
                void InspectLayout(bool stacked, string file)
                {
                    owner.UpdateLayout(); scroll.ScrollToEnd(); owner.UpdateLayout();
                    var rows = Descendants<SettingsRow>(group).ToArray();
                    Assert(rows.Length == 3 && rows.All(row => row.IsStacked == stacked), "settings rows did not adapt to the available width");
                    foreach (var row in rows)
                    {
                        var description = (FrameworkElement)row.Header;
                        var action = (FrameworkElement)row.Content;
                        var labelBox = description.TransformToAncestor(row).TransformBounds(new Rect(description.RenderSize));
                        var actionBox = action.TransformToAncestor(row).TransformBounds(new Rect(action.RenderSize));
                        Assert(stacked ? actionBox.Top >= labelBox.Bottom && Math.Abs(actionBox.Left - labelBox.Left) <= 1
                                       : actionBox.Left >= labelBox.Right && Math.Abs(actionBox.Right - row.ActualWidth + 16) <= 1,
                            "settings control overlaps its text or lost its alignment");
                        Assert(actionBox.Right <= row.ActualWidth && actionBox.Bottom <= row.ActualHeight, "settings control clipped");
                    }
                    foreach (var control in new Control[] { density, left, right, wizard })
                        Assert(control.ActualWidth >= 44 && control.ActualHeight >= 44, "settings hit target under 44 WPF units");
                    Snapshot(owner, file);
                }
                InspectLayout(false, "general-settings-wide.png");
                CheckSettingsFontFallback(owner, group);
                Assert(density.Focus(), "density selector could not take keyboard focus");
                owner.Width = 840; owner.UpdateLayout();
                // Keep the navigation open to exercise the minimum supported window width.
                if (((ColumnDefinition)owner.FindName("WorkspaceNavColumn")).ActualWidth < 100) Click(owner, "FoldWorkspaceSidebar", true);
                InspectLayout(true, "general-settings-narrow.png");
                Assert(density.IsKeyboardFocusWithin, "reflow took focus from the active selector");
                owner.Height = 600; owner.UpdateLayout();
                Reachable(owner, wizard); Reachable(owner, density);
                var comboPeer = new ComboBoxAutomationPeer(density);
                Assert(comboPeer.GetName() == "界面大小", "density selector lost its accessible label");
                var expand = (IExpandCollapseProvider)comboPeer.GetPattern(PatternInterface.ExpandCollapse)!;
                expand.Expand(); owner.UpdateLayout();
                Assert(density.Items.Count == 4 && ((Popup)density.Template.FindName("PART_Popup", density)).IsOpen, "density popup lost its choices");
                expand.Collapse();
                CheckSettingsHitTargets(owner, density, left, right, wizard);
                density.SelectedValue = .8d;
                Assert(File.ReadAllText(densityPath) == "0.8" && ((TextBlock)owner.FindName("DensityMessage")).Text.Contains("已保存"), "density choice did not persist");
                CheckSettingsHitTargets(owner, density, left, right, wizard);
                using (File.Open(densityPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    density.SelectedValue = .9d;
                    Assert(Equals(density.SelectedValue, .8d) && ((TextBlock)owner.FindName("DensityMessage")).Text.Contains("未能保存"), "failed save left a misleading density selection");
                }

                density.Focus(); density.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Assert(right.IsKeyboardFocused, "Tab did not enter the segment at the active edge");
                InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(right), Environment.TickCount, Key.Left)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert(left.IsKeyboardFocused && left.IsChecked == true && right.IsChecked == false && rail.LeftSide, "left arrow did not select the left edge");
                left.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Assert(wizard.IsKeyboardFocused, "Tab did not leave the single-selection group for onboarding");
                var reopenedRail = Rail();
                try { Assert(reopenedRail.LeftSide, "new rail did not read the selected side"); }
                finally { reopenedRail.Shutdown(); }

                var contrast = typeof(MainWindow).GetMethod("SetSettingsContrast", WorkspaceFlags)!;
                contrast.Invoke(main, [true]); owner.UpdateLayout();
                Assert(((SolidColorBrush)group.FindResource("SettingsText")).Color == SystemColors.WindowTextColor &&
                       density.IsEnabled && left.IsChecked == true && left.ActualHeight >= 44, "system contrast lost the text color, selection or hit area");
                contrast.Invoke(main, [SystemParameters.HighContrast]);
                Click(owner, "OpenOnboarding", true);
                var opened = Application.Current.Windows.OfType<OnboardingWindow>().Single();
                Assert(opened.IsVisible, "configuration action did not open onboarding");
                Click(opened, "NextButton");
                Click(owner, "TeachingSettingsTab");
                Click(opened, "OobePreferences", true);
                owner.UpdateLayout();
                Assert(((FrameworkElement)owner.FindName("GeneralSettingsPanel")).IsVisible,
                    "wizard preferences reopened the previously selected settings category");
                opened.Close();
                owner.Close();
            });
            Checks.Add("General settings: wide/narrow geometry, 44-unit controls, pointer hit tests at normal/compact scale, missing-font fallback, keyboard traversal, native popup, saved/locked density, reloaded dock side, system contrast and onboarding entry");
        }
        finally
        {
            foreach (string path in new[] { placementPath, densityPath, OnboardingStore.PathFor(pipe) }) File.Delete(path);
        }
    }

    private static void CheckSettingsHitTargets(Window owner, params Control[] controls)
    {
        foreach (var control in controls)
        {
            control.BringIntoView(); owner.UpdateLayout();
            foreach (var point in new[] { new Point(5, 5), new Point(control.ActualWidth - 5, control.ActualHeight - 5) })
            {
                var hit = owner.InputHitTest(control.TranslatePoint(point, owner)) as DependencyObject;
                Assert(hit == control || hit is not null && control.IsAncestorOf(hit), "another element intercepts the settings hit area");
            }
        }
    }

    private static void CheckSettingsFontFallback(Window owner, FrameworkElement group)
    {
        string[] keys = ["SettingsBodyFont", "SettingsHeadingFont"];
        foreach (string key in keys)
        {
            var family = (FontFamily)group.FindResource(key);
            group.Resources[key] = new FontFamily(string.Join(",", family.Source.Split(',').Select(
                name => name.Trim().StartsWith("MiSans", StringComparison.Ordinal) ? "NPEduTools-Absent-Font" : name)));
        }
        try
        {
            owner.UpdateLayout();
            foreach (var name in new[] { "DensityLabel", "DensityMessage" })
            {
                var label = (TextBlock)owner.FindName(name);
                var actual = new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch);
                var fallback = new Typeface(new FontFamily("Microsoft YaHei UI"), label.FontStyle, label.FontWeight, label.FontStretch);
                Assert(actual.TryGetGlyphTypeface(out var face) && fallback.TryGetGlyphTypeface(out var expected) &&
                       face.FontUri == expected.FontUri && face.CharacterToGlyphMap.ContainsKey('课'), "settings font fallback lost Chinese glyphs");
            }
        }
        finally { foreach (string key in keys) group.Resources.Remove(key); }
    }
}
