using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Services;

/// <summary>User edits override automatic metadata without changing render/cache identities.</summary>
public static class PresetMetadataService
{
    public static IReadOnlyList<string> EffectiveTags(PresetRecord record) => record.ManualTags ?? record.Tags ?? [];
    public static IReadOnlyList<string> EffectiveCategories(PresetRecord record) => record.ManualCategories ?? record.CategoryTags ?? [];

    public static void SetTags(PresetRecord record, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(values);
        record.AutomaticTags ??= [.. (record.Tags ?? [])];
        record.ManualTags = normalized;
        record.Tags = [.. normalized];
        record.TagIds = null;
    }

    public static void SetCategories(PresetRecord record, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(values);
        record.AutomaticCategories ??= [.. (record.CategoryTags ?? [])];
        record.ManualCategories = normalized;
        record.CategoryTags = [.. normalized];
        record.CategoryIds = null;
    }

    public static void ResetTags(PresetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.ManualTags = null;
        record.Tags = [.. (record.AutomaticTags ?? record.Tags ?? [])];
        record.TagIds = null;
    }

    public static void ResetCategories(PresetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.ManualCategories = null;
        record.CategoryTags = [.. (record.AutomaticCategories ?? record.CategoryTags ?? [])];
        record.CategoryIds = null;
    }

    internal static void UpdateAutomaticTags(PresetRecord record, IEnumerable<string> values)
    {
        var automatic = values.ToList();
        if (record.ManualTags is null && !record.Tags.SequenceEqual(automatic)) record.TagIds = null;
        if (record.ManualTags is not null || record.AutomaticTags is not null) record.AutomaticTags = automatic;
        record.Tags = [.. (record.ManualTags ?? automatic)];
    }

    internal static void UpdateAutomaticCategories(PresetRecord record, IEnumerable<string> values)
    {
        var automatic = values.ToList();
        if (record.ManualCategories is null && !record.CategoryTags.SequenceEqual(automatic)) record.CategoryIds = null;
        if (record.ManualCategories is not null || record.AutomaticCategories is not null) record.AutomaticCategories = automatic;
        record.CategoryTags = [.. (record.ManualCategories ?? automatic)];
    }

    internal static void PreserveEdits(PresetRecord previous, PresetRecord candidate)
    {
        candidate.ManualTags = previous.ManualTags is null ? null : [.. previous.ManualTags];
        candidate.TagIds = previous.TagIds is null ? null : [.. previous.TagIds];
        candidate.CategoryIds = previous.CategoryIds is null ? null : [.. previous.CategoryIds];
        candidate.CategoryCatalogId = previous.CategoryCatalogId;
        candidate.TagCatalogId = previous.TagCatalogId;
        candidate.ManualCategories = previous.ManualCategories is null ? null : [.. previous.ManualCategories];
        candidate.AutomaticTags = previous.AutomaticTags is null ? null : [.. previous.AutomaticTags];
        candidate.AutomaticCategories = previous.AutomaticCategories is null ? null : [.. previous.AutomaticCategories];
        candidate.CategoryTags = [.. (candidate.ManualCategories ?? previous.CategoryTags ?? [])];
        candidate.AdditionalProperties = previous.AdditionalProperties is null ? null : new(previous.AdditionalProperties);
    }

    private static List<string> Normalize(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.Select(value => value?.Trim() ?? "").Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (result.Count > 64 || result.Any(value => value.Length > 64 || value.Any(char.IsControl)))
            throw new ArgumentException("标签或分类最多 64 项，每项最多 64 个字符且不能包含控制字符。", nameof(values));
        return result;
    }
}
