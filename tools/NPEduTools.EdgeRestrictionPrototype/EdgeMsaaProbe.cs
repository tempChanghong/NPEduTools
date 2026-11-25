using System.Runtime.InteropServices;
using Accessibility;

namespace NPEduTools.EdgeRestrictionPrototype;

internal static class EdgeMsaaProbe
{
    private const int ClientObjectId = -4;
    private const int ChildSelf = 0;
    private const int DocumentRole = 0x0f;
    private const int InvisibleOrOffscreen = 0x18000;

    internal static string Read(nint renderer, Uri target)
    {
        try
        {
            Guid accessibleInterface = typeof(IAccessible).GUID;
            int result = AccessibleObjectFromWindow(renderer, ClientObjectId, ref accessibleInterface, out var client);
            if (result < 0 || client is null) return $"client-unavailable-{result:X8}";
            var remaining = new Queue<(IAccessible Node, object Child, int Depth)>();
            remaining.Enqueue((client, ChildSelf, 0));
            var documents = new List<string>(2);
            int remainingVisits = 64;
            while (remaining.TryDequeue(out var item) && remainingVisits-- > 0)
            {
                var (node, child, depth) = item;
                if (node.get_accRole(child) is int role && role == DocumentRole)
                {
                    if (node.get_accState(child) is not int state || (state & InvisibleOrOffscreen) == 0)
                        documents.Add(Classify(node.get_accValue(child), target));
                    if (documents.Count > 1) return "multiple-documents";
                    continue;
                }
                if (depth == 6 || !Equals(child, ChildSelf)) continue;
                int count = Math.Min(node.accChildCount, 16);
                if (count <= 0) continue;
                var children = new object[count];
                if (AccessibleChildren(node, 0, count, children, out int read) < 0) continue;
                for (int i = 0; i < read; i++)
                {
                    if (children[i] is IAccessible accessible) remaining.Enqueue((accessible, ChildSelf, depth + 1));
                    else if (children[i] is int childId && childId != ChildSelf) remaining.Enqueue((node, childId, depth + 1));
                }
            }
            return documents.Count == 1 ? documents[0] : "no-document";
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException or ArgumentException)
        {
            return $"read-unavailable-{error.HResult:X8}";
        }
    }

    private static string Classify(string? url, Uri target)
    {
        if (string.IsNullOrWhiteSpace(url)) return "document-value-unavailable";
        if (url == "chrome-error://chromewebdata/") return "internal-error-document";
        if (url == "edge://network-error/-22") return "diagnostic-document-url";
        if (EdgeForegroundUia.Matches(url, target)) return "external-document-at-target";
        return "other-document-value";
    }

    [DllImport("oleacc.dll", ExactSpelling = true)]
    private static extern int AccessibleObjectFromWindow(nint window, int objectId, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);

    [DllImport("oleacc.dll", ExactSpelling = true)]
    private static extern int AccessibleChildren(IAccessible parent, int childStart, int childCount,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);
}
