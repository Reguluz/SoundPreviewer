using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Scanning;

/// <summary>A non-mutating view of global duplicate groups within individual source folders.</summary>
public sealed class PresetDuplicateView
{
    private readonly Dictionary<string, PresetRecord> canonicalById;
    private readonly Dictionary<string, HashSet<string>> representativesBySource;
    private readonly Dictionary<string, PresetSourceCounts> countsBySource;

    public PresetDuplicateView(IEnumerable<PresetRecord> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var records = presets.ToList();
        var byId = records.ToDictionary(record => record.Id, StringComparer.Ordinal);
        canonicalById = records.ToDictionary(record => record.Id, Resolve, StringComparer.Ordinal);
        representativesBySource = new(StringComparer.Ordinal);
        countsBySource = new(StringComparer.Ordinal);
        var crossSourceGroups = records.GroupBy(record => canonicalById[record.Id].Id, StringComparer.Ordinal)
            .Where(group => group.Select(record => record.SourceId).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var source in records.GroupBy(record => record.SourceId, StringComparer.Ordinal))
        {
            var representatives = source.GroupBy(record => canonicalById[record.Id].Id, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(record => record.DuplicateOfId.Length == 0)
                    .ThenBy(record => record.Status == PresetStatus.Missing)
                    .ThenBy(record => record.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(record => record.Id, StringComparer.Ordinal).First().Id)
                .ToHashSet(StringComparer.Ordinal);
            representativesBySource[source.Key] = representatives;
            int total = source.Count();
            countsBySource[source.Key] = new(total, representatives.Count, total - representatives.Count,
                source.Count(record => crossSourceGroups.Contains(canonicalById[record.Id].Id)));
        }
        TotalCounts = new(records.Count, records.Select(record => canonicalById[record.Id].Id).Distinct(StringComparer.Ordinal).Count(),
            records.Count(record => record.DuplicateOfId.Length > 0),
            records.Count(record => crossSourceGroups.Contains(canonicalById[record.Id].Id)));

        PresetRecord Resolve(PresetRecord record)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { record.Id };
            var current = record;
            while (current.DuplicateOfId.Length > 0 && byId.TryGetValue(current.DuplicateOfId, out var next))
            {
                // Broken or cyclic legacy links must never redirect generation to an unrelated record.
                if (!visited.Add(next.Id)) return record;
                current = next;
            }
            return current.DuplicateOfId.Length == 0 ? current : record;
        }
    }

    public PresetSourceCounts TotalCounts { get; }

    public PresetSourceCounts GetSourceCounts(string sourceId) =>
        countsBySource.GetValueOrDefault(sourceId) ?? new(0, 0, 0, 0);

    /// <param name="sourceId">Null for all sources; an empty string selects records without a source.</param>
    public bool IsRepresentative(PresetRecord record, string? sourceId = null) => sourceId is null
        ? record.DuplicateOfId.Length == 0
        : record.SourceId == sourceId && representativesBySource.TryGetValue(sourceId, out var representatives) && representatives.Contains(record.Id);

    public PresetRecord ResolveCanonical(PresetRecord record) => canonicalById.GetValueOrDefault(record.Id) ?? record;

    public IReadOnlyList<PresetRecord> ResolveCanonicalSelection(IEnumerable<PresetRecord> selected) =>
        selected.Select(ResolveCanonical).DistinctBy(record => record.Id, StringComparer.Ordinal).ToList();
}

/// <param name="DuplicateCopies">Additional copies within this source; for all sources, the global duplicate count.</param>
/// <param name="CrossSourceMatches">Records whose duplicate group also exists in another source, including the canonical record.</param>
public sealed record PresetSourceCounts(int Total, int Unique, int DuplicateCopies, int CrossSourceMatches);
