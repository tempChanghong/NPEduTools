using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Host;

/// <summary>A bounded display inventory must not turn its omitted current device into absence evidence.</summary>
internal static class DeviceInventory
{
    internal static T[] Limit<T>(IEnumerable<T> source, string? selected, Func<T, string> id) where T : class
    {
        var candidates = source.ToArray();
        var result = Prefix(candidates);
        var current = selected is null ? null : candidates.FirstOrDefault(d => id(d) == selected);
        // Ordinary inventories keep their original order. Promote only a genuinely
        // enumerated saved device which would otherwise fall outside the wire budget.
        return current is not null && !result.Any(d => id(d) == selected)
            ? Prefix(new[] { current }.Concat(candidates.Where(d => id(d) != selected))) : result;
    }

    private static T[] Prefix<T>(IEnumerable<T> source)
    {
        int bytes = 2;
        return source.Take(128).TakeWhile(d =>
            (bytes += JsonSerializer.SerializeToUtf8Bytes(d, Protocol.Json).Length + 1) < 24000).ToArray();
    }
}
