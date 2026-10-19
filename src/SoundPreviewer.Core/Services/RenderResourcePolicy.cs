namespace SoundPreviewer.Core.Services;

public sealed record RenderHardwareInfo(int LogicalProcessorCount, int? PhysicalCoreCount)
{
    public string DetectionMethod { get; init; } = "";
}

public sealed record RenderProcessUsage(int ProcessId, int ParentProcessId, long ResidentBytes, double? CpuSeconds);

public sealed record RenderMemorySnapshot(long ResidentBytes, bool IsComplete, int ProcessCount, string? Error = null)
{
    public long RootResidentBytes { get; init; }
    public IReadOnlyList<RenderProcessUsage> Processes { get; init; } = [];

    public static RenderMemorySnapshot FromProcesses(int rootProcessId, IReadOnlyList<RenderProcessUsage> processTable,
        bool enumerationComplete = true, string? error = null)
    {
        var family = new Dictionary<int, RenderProcessUsage>();
        var root = processTable.FirstOrDefault(process => process.ProcessId == rootProcessId);
        if (root is null) return new(0, false, 0, error ?? "无法读取主程序进程的内存占用。");
        var children = processTable.ToLookup(process => process.ParentProcessId);
        var waiting = new Queue<RenderProcessUsage>();
        waiting.Enqueue(root);
        long resident = 0;
        while (waiting.TryDequeue(out var process))
        {
            if (!family.TryAdd(process.ProcessId, process)) continue;
            if (process.ResidentBytes < 0) { enumerationComplete = false; error ??= "进程内存计数无效。"; }
            else
            {
                try { resident = checked(resident + process.ResidentBytes); }
                catch (OverflowException) { resident = long.MaxValue; enumerationComplete = false; error ??= "进程内存计数超出范围。"; }
            }
            foreach (var child in children[process.ProcessId]) waiting.Enqueue(child);
        }
        return new(resident, enumerationComplete, family.Count, error)
        {
            RootResidentBytes = Math.Max(0, root.ResidentBytes), Processes = family.Values.ToArray()
        };
    }
}

public interface IRenderResourceProbe
{
    RenderHardwareInfo Hardware { get; }
    RenderMemorySnapshot Capture();
}

public static class RenderResourcePolicy
{
    public const long MemoryLimitBytes = 2L * 1024 * 1024 * 1024;
    public const long SoftLimitBytes = 1792L * 1024 * 1024;
    private static readonly int[] levels = [1, 2, 4, 8, 16];

    public static int ResolveMaxConcurrency(int requested, RenderHardwareInfo hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        if (requested != 0 && !levels.Contains(requested)) throw new ArgumentException("同时生成数量须为自动、1、2、4、8 或 16。", nameof(requested));
        if (requested != 0) return requested;
        int logical = Math.Max(1, hardware.LogicalProcessorCount);
        int cores = hardware.PhysicalCoreCount is > 0 ? Math.Min(hardware.PhysicalCoreCount.Value, logical) : Math.Max(1, logical / 2);
        return levels.Last(level => level <= Math.Min(16, cores));
    }

    public static bool MustStop(RenderMemorySnapshot snapshot) => snapshot.ResidentBytes > MemoryLimitBytes;

    public static long WorkerTreeResidentBytes(RenderMemorySnapshot snapshot, IEnumerable<int> workerProcessIds)
    {
        var processes = snapshot.Processes.ToDictionary(process => process.ProcessId);
        var children = snapshot.Processes.ToLookup(process => process.ParentProcessId);
        var waiting = new Queue<int>(workerProcessIds);
        var visited = new HashSet<int>();
        long resident = 0;
        while (waiting.TryDequeue(out int id))
        {
            if (!visited.Add(id)) continue;
            if (processes.TryGetValue(id, out var process))
            {
                long bytes = Math.Max(0, process.ResidentBytes);
                resident = bytes > long.MaxValue - resident ? long.MaxValue : resident + bytes;
            }
            // A worker can exit between enumeration and counter reads while its host remains.
            foreach (var child in children[id]) waiting.Enqueue(child.ProcessId);
        }
        return resident;
    }

    public static bool CanStart(RenderMemorySnapshot snapshot, long reservedBytes, long newReservationBytes)
    {
        if (!snapshot.IsComplete || snapshot.ResidentBytes < 0 || snapshot.ResidentBytes >= SoftLimitBytes ||
            reservedBytes < 0 || newReservationBytes < 0) return false;
        // Subtraction keeps an untrusted or overflowing reservation from becoming free space.
        long available = SoftLimitBytes - snapshot.ResidentBytes;
        return reservedBytes <= available && newReservationBytes <= available - reservedBytes;
    }
}
