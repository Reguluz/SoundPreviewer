using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Core.Services;

public enum PreviewDirectoryChangeMode { Migrate, Clear }
public sealed record PreviewStorageResult(int DeletedFiles, long FreedBytes, int UpdatedRecords, IReadOnlyList<string> Warnings);
public sealed record PreviewRelocationResult(string Directory, int CopiedFiles, int DeletedFiles, long FreedBytes, IReadOnlyList<string> Warnings);

public static class PreviewStorageService
{
    public static PreviewStorageResult ClearPreviews(LibraryStore store, AppSettings settings)
    {
        var plan = CreatePlan(store, settings);
        int changed = plan.Records.Count(ClearMapping);
        foreach (var plugin in plan.Settings.Plugins) plugin.InitBaselinePath = "";
        var staged = StageForDeletion(plan.DeletableFiles);
        try { store.SaveState(plan.Records, plan.Settings); }
        catch { Restore(staged); throw; }
        var deleted = DeleteStaged(staged, plan.Warnings);
        foreach (var plugin in settings.Plugins) plugin.InitBaselinePath = "";
        return new(deleted.Count, deleted.Bytes, changed, plan.Warnings);
    }

    public static PreviewRelocationResult RelocateLibrary(LibraryStore store, AppSettings settings, string newDirectory,
        PreviewDirectoryChangeMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var plan = CreatePlan(store, settings);
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newDirectory));
        ValidateDestination(target, plan);
        string stage = Path.Combine(Path.GetDirectoryName(target)!, ".sp-move-" + Guid.NewGuid().ToString("N")[..16]);
        Directory.CreateDirectory(stage);
        bool published = false;
        List<StagedFile> staged = [];
        int copied = 0;
        try
        {
            string backup = Path.Combine(stage, "Backups", "library-before-relocation-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ".db");
            store.CreateBackup(backup);
            File.Copy(backup, Path.Combine(stage, "library.db"));
            var destinationStore = new LibraryStore(Path.Combine(stage, "library.db"));
            // Check the snapshot before changing any path or removing any source audio.
            if (JsonSerializer.Serialize(destinationStore.LoadPresets(), LibraryJson.Options) != JsonSerializer.Serialize(plan.Records, LibraryJson.Options))
                throw new IOException("复制期间音色库记录发生变化，请停止任务后重试；原音色库未更改。");
            CopyConfig(Path.Combine(plan.LibraryRoot, "Config"), Path.Combine(stage, "Config"));
            var mappings = new Dictionary<string, string>(ApplicationPaths.PathComparer);
            if (mode == PreviewDirectoryChangeMode.Migrate)
            {
                var destinations = new HashSet<string>(ApplicationPaths.PathComparer);
                foreach (string source in plan.AudioFiles.OrderBy(path => path, ApplicationPaths.PathComparer))
                {
                    string relative = Path.GetDirectoryName(source)!.EndsWith("InitializationBaselines", ApplicationPaths.PathComparison)
                        ? Path.Combine("InitializationBaselines", Path.GetFileName(source)) : Path.GetFileName(source);
                    if (!destinations.Add(relative))
                    {
                        relative = Path.Combine("MigratedAudio", Fingerprints.HashText(source)[..20], Path.GetFileName(source));
                        destinations.Add(relative);
                    }
                    string output = Path.Combine(stage, relative);
                    EnsureNoLinks(source);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.Copy(source, output);
                    if (Fingerprints.HashFile(source) != Fingerprints.HashFile(output)) throw new IOException("预览复制校验失败：" + source);
                    mappings[source] = Path.Combine(target, relative);
                    copied++;
                }
                foreach (var (source, metadata) in plan.Baselines)
                {
                    string relative = Path.Combine("InitializationBaselines", Path.GetFileName(source));
                    if (!destinations.Add(relative)) relative = Path.Combine("InitializationBaselines", Fingerprints.HashText(source)[..20] + ".json");
                    RewriteBaselinePaths(metadata, mappings, plan.LibraryRoot);
                    string output = Path.Combine(stage, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.WriteAllText(output, metadata.ToJsonString(LibraryJson.Options));
                    mappings[source] = Path.Combine(target, relative);
                    copied++;
                }
            }
            foreach (var record in plan.Records)
            {
                string? previous = Normalize(record.PreviewPath, plan.LibraryRoot);
                if (mode == PreviewDirectoryChangeMode.Migrate && previous is not null && mappings.TryGetValue(previous, out string? next)) record.PreviewPath = next;
                else ClearMapping(record);
            }
            foreach (var plugin in plan.Settings.Plugins)
            {
                string? previous = Normalize(plugin.InitBaselinePath, plan.LibraryRoot);
                plugin.InitBaselinePath = mode == PreviewDirectoryChangeMode.Migrate && previous is not null && mappings.TryGetValue(previous, out string? next) ? next : "";
            }
            plan.Settings.PreviewDirectory = target;
            destinationStore.SaveState(plan.Records, plan.Settings);
            ValidateDestination(target, plan);
            staged = StageForDeletion(plan.DeletableFiles);
            if (Directory.Exists(target)) Directory.Delete(target); // Empty directories only; never remove another user's contents.
            PublishDirectory(stage, target);
            published = true;
        }
        catch
        {
            Restore(staged);
            throw;
        }
        finally
        {
            if (!published) DeletePrivateStage(stage);
        }
        var deleted = DeleteStaged(staged, plan.Warnings);
        plan.Warnings.Add("原数据库及 Config 作为额外回退备份保留在：" + plan.LibraryRoot + "；迁移前的一致数据库快照另存于新目录 Backups。");
        return new(target, copied, deleted.Count, deleted.Bytes, plan.Warnings);
    }

    private static Plan CreatePlan(LibraryStore store, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        string libraryRoot = Path.GetDirectoryName(store.DatabasePath)!;
        EnsureNoLinks(libraryRoot);
        var plan = new Plan(libraryRoot, store.LoadPresets(), Clone(settings));
        plan.Roots.Add(libraryRoot);
        if (Normalize(settings.PreviewDirectory, libraryRoot) is string previewRoot) plan.Roots.Add(previewRoot);
        foreach (string root in plan.Roots)
        {
            EnsureNoLinks(root);
            if (File.Exists(Path.Combine(root, "library.db")) && !ApplicationPaths.SamePath(Path.Combine(root, "library.db"), store.DatabasePath))
            {
                plan.SharedRoots.Add(root);
                plan.Warnings.Add("缓存目录含有另一个音色库，原文件受保护：" + root);
            }
        }
        foreach (var record in plan.Records)
        {
            if (Normalize(record.SourcePath, libraryRoot) is string source) plan.Sources.Add(source);
        }
        foreach (var source in settings.Sources)
            if (Normalize(source.Path, libraryRoot) is string path) plan.SourceRoots.Add(path);
        foreach (var record in plan.Records)
        {
            if (Normalize(record.PreviewPath, libraryRoot) is not string path || !File.Exists(path)) continue;
            if (!PreviewAudioFile.IsSupportedExtension(path) || Protected(path, plan))
            { plan.Warnings.Add("已保留受保护的来源或非预览文件：" + path); continue; }
            bool owned = IsOwnedLocation(path, plan);
            if (owned || IsHash(record.CacheKey)) plan.AudioFiles.Add(path);
            if (owned) plan.DeletableFiles.Add(path);
            else plan.Warnings.Add("外部或共享位置的原预览音频受保护，未删除：" + path);
        }
        foreach (var plugin in settings.Plugins)
            if (Normalize(plugin.InitBaselinePath, libraryRoot) is string path && File.Exists(path)) AddBaseline(path, plan);
        ReportUnreferencedFiles(plan);
        return plan;
    }

    private static void ReportUnreferencedFiles(Plan plan)
    {
        // Another database can use this directory as its external cache. Neither a hash
        // filename nor a database beside a WAV proves that this library owns that file.
        var retained = new HashSet<string>(ApplicationPaths.PathComparer);
        foreach (string root in plan.Roots.Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
                if (PreviewAudioFile.IsSupportedExtension(file) && !plan.AudioFiles.Contains(file) && !Protected(file, plan)) retained.Add(file);
            string baselines = Path.Combine(root, "InitializationBaselines");
            if (!Directory.Exists(baselines)) continue;
            if ((File.GetAttributes(baselines) & FileAttributes.ReparsePoint) != 0)
            { plan.Warnings.Add("未检查的基准链接目录已保留：" + baselines); continue; }
            foreach (string file in Directory.EnumerateFiles(baselines, "*", SearchOption.TopDirectoryOnly))
                if ((Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase)) &&
                    !plan.AudioFiles.Contains(file) && !plan.DeletableFiles.Contains(file) && !Protected(file, plan)) retained.Add(file);
        }
        if (retained.Count > 0) plan.Warnings.Add($"有 {retained.Count} 个未被当前库记录引用、无法确认归属的音频或基准文件，已保留；例如：{retained.First()}");
    }

    private static void AddBaseline(string path, Plan plan)
    {
        path = Path.GetFullPath(path);
        if (plan.Baselines.ContainsKey(path) || Protected(path, plan)) return;
        EnsureNoLinks(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) { plan.Warnings.Add("基准元数据过大，已保留：" + path); return; }
        JsonObject? metadata;
        try { metadata = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { plan.Warnings.Add("无法识别的基准配置已保留：" + path); return; }
        if (metadata?["Kind"] is not JsonValue kind || !kind.TryGetValue<string>(out string? kindText) || kindText != InitBaselineService.BaselineKind) return;
        var references = BaselinePaths(metadata).Select(value => Normalize(value, plan.LibraryRoot)).Where(value => value is not null).Cast<string>().Distinct(ApplicationPaths.PathComparer).ToList();
        bool complete = references.Count > 0;
        foreach (string wav in references)
        {
            if (!File.Exists(wav) || !Path.GetExtension(wav).Equals(".wav", StringComparison.OrdinalIgnoreCase) || Protected(wav, plan))
            { complete = false; continue; }
            EnsureNoLinks(wav);
            plan.AudioFiles.Add(wav);
            if (IsOwnedLocation(wav, plan)) plan.DeletableFiles.Add(wav);
            else plan.Warnings.Add("外部或共享位置的原基准音频受保护，未删除：" + wav);
        }
        if (IsOwnedLocation(path, plan)) plan.DeletableFiles.Add(path);
        else plan.Warnings.Add("外部基准配置保留在原位置：" + path);
        if (complete) plan.Baselines[path] = metadata;
        else plan.Warnings.Add("缺少完整音频的基准引用将清除：" + path);
    }

    private static IEnumerable<string> BaselinePaths(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var (key, value) in obj)
            {
                if (key is "WavPath" or "OutputPath" && value is JsonValue text && text.TryGetValue<string>(out string? path) && path.Length > 0) yield return path;
                else foreach (string nestedPath in BaselinePaths(value)) yield return nestedPath;
            }
        else if (node is JsonArray array)
            foreach (var value in array) foreach (string path in BaselinePaths(value)) yield return path;
    }

    private static void RewriteBaselinePaths(JsonNode? node, Dictionary<string, string> mappings, string root)
    {
        if (node is JsonObject obj)
            foreach (string key in obj.Select(entry => entry.Key).ToList())
            {
                if (key is "WavPath" or "OutputPath" && obj[key] is JsonValue text && text.TryGetValue<string>(out string? path) &&
                    Normalize(path, root) is string previous && mappings.TryGetValue(previous, out string? next)) obj[key] = next;
                else RewriteBaselinePaths(obj[key], mappings, root);
            }
        else if (node is JsonArray array) foreach (var value in array) RewriteBaselinePaths(value, mappings, root);
    }

    private static bool ClearMapping(PresetRecord record)
    {
        if (record.PreviewPath.Length == 0 && record.CacheKey.Length == 0 && record.Notes.Count == 0 && record.Metrics is null) return false;
        record.PreviewPath = ""; record.CacheKey = ""; record.AttemptedCacheKey = ""; record.RenderedUtc = null;
        record.Metrics = null; record.Notes = []; record.AdapterVersion = ""; record.PluginVersion = ""; record.TemplateJson = ""; record.Attempts = 0;
        if (record.Status is PresetStatus.Ready or PresetStatus.NeedsReview or PresetStatus.Rendering or PresetStatus.Failed) record.Status = PresetStatus.Pending;
        record.Message = "预览音频已清除，可重新生成。";
        return true;
    }

    private static void ValidateDestination(string target, Plan plan)
    {
        EnsureNoLinks(target);
        foreach (string root in plan.Roots)
            if (ApplicationPaths.IsWithin(target, root) || ApplicationPaths.IsWithin(root, target)) throw new ArgumentException("新目录不能等于、包含或位于旧音色库／缓存目录内。");
        foreach (string source in plan.SourceRoots)
            if (ApplicationPaths.IsWithin(target, source) || ApplicationPaths.IsWithin(source, target)) throw new ArgumentException("新目录不能与音色来源目录重叠。");
        foreach (string source in plan.Sources)
            if (ApplicationPaths.IsWithin(source, target)) throw new ArgumentException("新目录不能包含原始音色文件。");
        for (string? parent = target; parent is not null; parent = Path.GetDirectoryName(parent))
            if (File.Exists(Path.Combine(parent, "library.db"))) throw new IOException("新目录不能位于另一个音色库目录内。");
        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())) throw new IOException("请选择空目录；不会覆盖已有数据库或其他文件。");
    }

    private static bool Protected(string path, Plan plan) => plan.Sources.Contains(path) || plan.SourceRoots.Any(root => ApplicationPaths.IsWithin(path, root));
    private static bool IsOwnedLocation(string path, Plan plan)
    {
        string? root = ApplicationPaths.IsWithin(path, plan.LibraryRoot) ? plan.LibraryRoot : null;
        if (root is null || plan.SharedRoots.Any(directory => ApplicationPaths.IsWithin(path, directory))) return false;
        EnsureNoLinks(path);
        for (string? parent = Path.GetDirectoryName(path); parent is not null && !ApplicationPaths.SamePath(parent, root); parent = Path.GetDirectoryName(parent))
            if (File.Exists(Path.Combine(parent, "library.db"))) return false;
        return true;
    }

    private static bool IsHash(string text) => text.Length == 64 && text.All(char.IsAsciiHexDigit);
    private static string? Normalize(string? path, string root) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path, root);
    private static AppSettings Clone(AppSettings settings) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, LibraryJson.Options), LibraryJson.Options)!;

    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不能操作链接或重解析点路径：" + current);
    }

    private static void CopyConfig(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        EnsureNoLinks(source);
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source)) { EnsureNoLinks(file); File.Copy(file, Path.Combine(destination, Path.GetFileName(file))); }
        foreach (string directory in Directory.EnumerateDirectories(source)) CopyConfig(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static List<StagedFile> StageForDeletion(IEnumerable<string> files)
    {
        var moved = new List<StagedFile>();
        string operation = ".soundpreviewer-cleanup-" + Guid.NewGuid().ToString("N");
        try
        {
            foreach (string file in files.OrderBy(path => path, ApplicationPaths.PathComparer))
            {
                EnsureNoLinks(file);
                if (!File.Exists(file)) continue;
                string directory = Path.Combine(Path.GetDirectoryName(file)!, operation);
                Directory.CreateDirectory(directory);
                string temporary = Path.Combine(directory, Path.GetFileName(file) + ".pending");
                long bytes = new FileInfo(file).Length;
                File.Move(file, temporary);
                moved.Add(new(file, temporary, bytes));
            }
            return moved;
        }
        catch { Restore(moved); throw; }
    }

    private static void Restore(List<StagedFile> files)
    {
        var failures = new List<Exception>();
        foreach (var file in files.AsEnumerable().Reverse())
        {
            try { if (File.Exists(file.Temporary)) File.Move(file.Temporary, file.Original); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures.Add(new IOException("原音频可从此路径恢复：" + file.Temporary, error)); }
        }
        RemoveEmptyQuarantines(files, null);
        if (failures.Count > 0) throw new AggregateException("操作未提交，部分原音频需要从临时保留位置恢复。", failures);
    }

    private static (int Count, long Bytes) DeleteStaged(List<StagedFile> files, List<string> warnings)
    {
        int count = 0; long bytes = 0;
        foreach (var file in files)
        {
            try { File.Delete(file.Temporary); count++; bytes += file.Bytes; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { warnings.Add("新库已安全保存；旧缓存清理失败，保留在：" + file.Temporary + "（" + error.Message + "）"); }
        }
        RemoveEmptyQuarantines(files, warnings);
        return (count, bytes);
    }

    private static void RemoveEmptyQuarantines(IEnumerable<StagedFile> files, List<string>? warnings)
    {
        foreach (string directory in files.Select(file => Path.GetDirectoryName(file.Temporary)!).Distinct(ApplicationPaths.PathComparer))
        {
            try { if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { warnings?.Add("空的缓存清理目录未能移除：" + directory); }
        }
    }

    private static void DeletePrivateStage(string directory)
    {
        if (!Directory.Exists(directory)) return;
        if (!Path.GetFileName(directory).StartsWith(".sp-move-", StringComparison.Ordinal)) throw new IOException("拒绝清理非本次操作的临时目录。");
        EnsureNoLinks(directory);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) { EnsureNoLinks(file); File.Delete(file); }
        foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length)) { EnsureNoLinks(child); Directory.Delete(child); }
        Directory.Delete(directory);
    }

    private static void PublishDirectory(string stage, string target)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { Directory.Move(stage, target); return; }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 5 or 32 or 33 &&
                elapsed.Elapsed < TimeSpan.FromSeconds(1) && Directory.Exists(stage) && !Directory.Exists(target) && !File.Exists(target))
            {
                // Windows scanners can briefly retain handles to newly written SQLite or audio files.
                Thread.Sleep(40);
            }
        }
    }

    private sealed record StagedFile(string Original, string Temporary, long Bytes);
    private sealed class Plan(string libraryRoot, List<PresetRecord> records, AppSettings settings)
    {
        public string LibraryRoot { get; } = libraryRoot;
        public List<PresetRecord> Records { get; } = records;
        public AppSettings Settings { get; } = settings;
        public HashSet<string> Roots { get; } = new(ApplicationPaths.PathComparer);
        public HashSet<string> SharedRoots { get; } = new(ApplicationPaths.PathComparer);
        public HashSet<string> Sources { get; } = new(ApplicationPaths.PathComparer);
        public HashSet<string> SourceRoots { get; } = new(ApplicationPaths.PathComparer);
        public HashSet<string> AudioFiles { get; } = new(ApplicationPaths.PathComparer);
        public HashSet<string> DeletableFiles { get; } = new(ApplicationPaths.PathComparer);
        public Dictionary<string, JsonObject> Baselines { get; } = new(ApplicationPaths.PathComparer);
        public List<string> Warnings { get; } = [];
    }
}
