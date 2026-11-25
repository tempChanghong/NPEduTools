using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Microsoft.Win32.SafeHandles;

namespace NPEduTools.EdgeRestrictionPrototype;

internal static class EdgeForegroundUia
{
    private static readonly string[] StablePaths = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
    }.Where(directory => !string.IsNullOrEmpty(directory))
      .Select(directory => Path.Combine(directory, @"Microsoft\Edge\Application\msedge.exe")).ToArray();

    internal static (nint Window, string Result, BrowserObservation? Proof) Read(Uri target, int? testPid)
    {
        nint hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || (testPid is int allowedPid && allowedPid != pid))
            return (0, "no-foreground-test-window", null);
        try
        {
            using var process = OpenProcess(0x1000, false, pid);
            var path = new StringBuilder(1024);
            int capacity = path.Capacity;
            if (process.IsInvalid || !QueryFullProcessImageName(process, 0, path, ref capacity))
                return (0, "foreground-process-unavailable", null);
            if (!StablePaths.Contains(path.ToString(), StringComparer.OrdinalIgnoreCase))
                return (0, "no-foreground-stable-edge", null);

            var root = AutomationElement.FromHandle(hwnd);
            if (root is null || root.Current.ClassName != "Chrome_WidgetWin_1")
                return (0, "no-browser-window", null);

            var documentRoot = root;
            var documents = ReadDocuments(documentRoot);
            int rendererWindowCount = 0;
            if (documents.Length == 0)
            {
                var renderers = FindRendererWindows(hwnd);
                rendererWindowCount = renderers.Count;
                if (renderers.Count == 1 && AutomationElement.FromHandle(renderers[0]) is { } renderer)
                {
                    documentRoot = renderer;
                    documents = ReadDocuments(documentRoot);
                }
            }
            if (documents.Length != 1)
                return (hwnd, testPid is null ? "no-unique-active-document" :
                    $"no-unique-active-document-{documents.Length}-documents-{rendererWindowCount}-renderer-windows", null);
            var activeDocument = documents[0];
            string? documentUrl = ReadValue(activeDocument);
            if (documentUrl != "chrome-error://chromewebdata/")
            {
                if (testPid is not null && documentUrl == "edge://network-error/-22")
                    return (hwnd, "diagnostic-document-url", null);
                return (hwnd, documentUrl is null ? "document-url-not-exposed" :
                    Matches(documentUrl, target) ? "external-document-at-target" : "no-internal-error-document", null);
            }

            // Read only the confirmed browser error document, never ordinary webpage content.
            var details = activeDocument.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Where(node => !node.Current.IsOffscreen).Select(node => node.Current.Name).ToArray();
            if (!HasAdminMarker(details))
                return (hwnd, "no-admin-error-code", null);

            var address = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "address-edit"),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "OmniboxViewViews"),
                    new PropertyCondition(AutomationElement.NameProperty, "Address and search bar"),
                    new PropertyCondition(AutomationElement.NameProperty, "地址和搜索栏"))));
            if (address is null || !IsTopLevelVisible(address, root) || address.Current.HasKeyboardFocus ||
                ReadAddress(ReadValue(address), target) is not { } attempted ||
                !ForegroundPolicyGate.MatchesWatchTarget(attempted, target))
                return (hwnd, "destination-not-confirmed-by-address-bar", null);

            bool confirmedPath = details.Any(detail => Matches(detail, target));
            // Edge's administrator error template may print no URL. Its trusted
            // document or selected tab title may corroborate the committed host,
            // but does not independently attest arbitrary address-bar paths.
            bool canUseHostProof = ForegroundPolicyGate.IsSiteScope(target);
            if (!confirmedPath && !(canUseHostProof && ConfirmsHost(root, activeDocument, target)))
                return (hwnd, "error-destination-not-exposed", null);
            var current = ReadDocuments(documentRoot);
            if (GetForegroundWindow() != hwnd || current.Length != 1 || !activeDocument.Equals(current[0]) ||
                ReadValue(activeDocument) != documentUrl || address.Current.HasKeyboardFocus ||
                ReadAddress(ReadValue(address), target) is not { } addressNow ||
                addressNow != attempted || addressNow.Fragment != attempted.Fragment)
                return (0, "foreground-changed", null);

            return (hwnd, "confirmed", new BrowserObservation(hwnd.ToString(), string.Join(".", activeDocument.GetRuntimeId()),
                attempted.AbsoluteUri, documentUrl, "net::ERR_BLOCKED_BY_ADMINISTRATOR", MainDocument: true, ActiveTab: true));
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or
            UnauthorizedAccessException or ArgumentException or ExternalException or TimeoutException)
        {
            return (0, testPid is null ? "accessibility-read-unavailable" :
                $"accessibility-read-unavailable-{error.GetType().Name}-{error.HResult:X8}", null);
        }
    }

    private static AutomationElement[] ReadDocuments(AutomationElement root)
    {
        if (root.Current.ControlType == ControlType.Document && !root.Current.IsOffscreen) return [root];
        return root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document))
            .Cast<AutomationElement>().Where(document => IsTopLevelVisible(document, root)).Take(2).ToArray();
    }

    internal static bool HasAdminMarker(IReadOnlyCollection<string> documentText) =>
        documentText.Contains("ERR_BLOCKED_BY_ADMINISTRATOR", StringComparer.Ordinal) ||
        (documentText.Contains("已阻止此页面", StringComparer.Ordinal) &&
            documentText.Contains("你的组织不允许你查看此网站", StringComparer.Ordinal)) ||
        (documentText.Contains("This page has been blocked", StringComparer.Ordinal) &&
            documentText.Contains("Your organization doesn't allow you to view this site", StringComparer.Ordinal));

    private static bool ConfirmsHost(AutomationElement browser, AutomationElement document, Uri target)
    {
        if (string.Equals(document.Current.Name, target.IdnHost, StringComparison.OrdinalIgnoreCase)) return true;
        string windowTitle = browser.Current.Name.Replace("\u200b", "", StringComparison.Ordinal);
        if (windowTitle.StartsWith(target.IdnHost + " - ", StringComparison.OrdinalIgnoreCase) &&
            windowTitle.EndsWith(" - Microsoft Edge", StringComparison.Ordinal))
            return true;
        return browser.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
            .Cast<AutomationElement>().Any(tab => IsTopLevelVisible(tab, browser) &&
                string.Equals(tab.Current.Name, target.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) &&
                ((SelectionItemPattern)pattern).Current.IsSelected);
    }

    internal static string ReadMsaaProbe(nint hwnd, Uri target)
    {
        if (hwnd == 0 || GetForegroundWindow() != hwnd) return "no-foreground-test-window";
        var renderers = FindRendererWindows(hwnd);
        if (renderers.Count != 1) return "no-unique-renderer";
        return EdgeMsaaProbe.Read(renderers[0], target);
    }

    private static List<nint> FindRendererWindows(nint hwnd)
    {
        var renderers = new List<nint>();
        EnumChildWindows(hwnd, (child, _) =>
        {
            if (IsWindowVisible(child))
            {
                var name = new StringBuilder(64);
                if (GetClassName(child, name, name.Capacity) > 0 && name.ToString() == "Chrome_RenderWidgetHostHWND")
                    renderers.Add(child);
            }
            return renderers.Count < 2;
        }, 0);
        return renderers;
    }

    private static bool IsTopLevelVisible(AutomationElement element, AutomationElement root)
    {
        if (element.Current.IsOffscreen) return false;
        var walker = TreeWalker.RawViewWalker;
        for (var parent = walker.GetParent(element); parent is not null; parent = walker.GetParent(parent))
        {
            if (parent.Equals(root)) return true;
            if (parent.Current.ControlType == ControlType.Document) return false;
        }
        return false;
    }

    private static string? ReadValue(AutomationElement element) =>
        element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ? ((ValuePattern)value).Current.Value : null;

    internal static bool Matches(string? value, Uri target) =>
        ReadAddress(value, target) is { } address && address == target && address.Fragment == target.Fragment;

    private static Uri? ReadAddress(string? value, Uri target)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            if (!Uri.TryCreate(target.Scheme + "://" + value, UriKind.Absolute, out uri))
                return null;
        }
        return string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
    }

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ReadChildWindow(nint window, nint unused);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint hwnd, ReadChildWindow callback, nint unused);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
}
