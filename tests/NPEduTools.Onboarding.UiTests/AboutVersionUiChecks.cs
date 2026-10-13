using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;

internal static partial class Program
{
    private static void RunAboutVersionChecks()
    {
        var about = new AboutPage();
        var window = new Window { Content = about, Width = 980, Height = 660 };
        try
        {
            Exercise(window, owner =>
            {
                string version = typeof(App).Assembly.GetName().Version!.ToString(3);
                string info = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
                var badge = (TextBlock)about.FindName("VersionBadge");
                var status = (TextBlock)about.FindName("ReleaseStatus");
                Assert(badge.Text == info.Split('+', 2)[0] && badge.Text.Contains(version) && badge.Text.Contains("EVA-02（二号机）"), "about page lost source version or codename");
                Checks.Add("About badge uses compiled version and unchanged EVA-02 codename");
                Assert(!status.Text.Contains("正式版"), "a local build is labelled as a confirmed public release");
                Assert(status.Text.Contains("发布"), "about page omits the independent release boundary");
                Snapshot(owner, "about-current-source.png");
                owner.Close();
                Checks.Add("A semantic version does not claim public release status");
            });
        }
        finally { window.Close(); }
    }
}
