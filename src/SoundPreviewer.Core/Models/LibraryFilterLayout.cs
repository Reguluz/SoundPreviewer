namespace SoundPreviewer.Core.Models;

public static class LibraryFilterLayout
{
    public static IReadOnlyList<string> DefaultOrder { get; } = ["status", "category", "tag", "synth"];
    public static IReadOnlyList<string> KnownKeys { get; } = [.. DefaultOrder, "source", "package", "favorite"];

    // The saved list contains only visible groups, in display order.
    public static List<string> Normalize(IEnumerable<string>? order)
    {
        var result = order?.Where(KnownKeys.Contains).Distinct(StringComparer.Ordinal).ToList() ?? [];
        return result.Count > 0 ? result : [.. DefaultOrder];
    }
}
