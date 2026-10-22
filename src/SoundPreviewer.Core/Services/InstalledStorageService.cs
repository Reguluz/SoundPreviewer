using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Core.Services;

public sealed record InstalledStorageResult(string Directory, string SourceDirectory, int CopiedFiles,
    IReadOnlyList<string> Warnings, string? RetainedDirectory = null);

/// <summary>Imports historical libraries into Preview beside the application without changing their originals.</summary>
public static class InstalledStorageService
{
    public static string GetSourceDirectory(string applicationDirectory, string userDataDirectory, string? sourceDirectory = null,
        ICollection<string>? warnings = null)
    {
        string application = Path.GetFullPath(applicationDirectory);
        string target = Path.Combine(application, "Preview");
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            string requested = Path.GetFullPath(sourceDirectory, application);
            if (Path.GetFileName(Path.TrimEndingDirectorySeparator(requested)).StartsWith("Preview-before-import-", StringComparison.OrdinalIgnoreCase))
                throw new IOException("此目录是原 Preview 的完整回退备份，不能作为普通旧库导入。请先退出程序，另行备份当前 Preview，再将此备份恢复为原 Preview 目录名后重启，以免旧绝对路径绑定到其他音频：" + requested);
            if (!ApplicationPaths.SamePath(requested, target) && !File.Exists(Path.Combine(requested, "library.db")))
                throw new DirectoryNotFoundException("所选旧音色库不存在 library.db，未创建空库替代：" + requested);
            return requested;
        }
        // Once imported, an old locator must never redirect output back outside this installation.
        if (File.Exists(Path.Combine(target, "library.db"))) return target;
        try { return ApplicationPaths.ResolveDefaultDataDirectory(application, userDataDirectory); }
        catch (DirectoryNotFoundException error)
        {
            warnings?.Add("上次记录的音色库不可用，将使用程序旁 Preview；可重新配置目录并生成预览。旧路径中的文件未删除。详情：" + error.Message);
            return target;
        }
    }

    public static InstalledStorageResult Prepare(string applicationDirectory, string userDataDirectory,
        string? sourceDirectory = null, IProgress<string>? progress = null, bool replaceTarget = false)
    {
        string application = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        string target = Path.Combine(application, "Preview");
        var warnings = new List<string>();
        string source = Path.TrimEndingDirectorySeparator(GetSourceDirectory(application, userDataDirectory, sourceDirectory, warnings));
        bool inPlace = ApplicationPaths.SamePath(source, target);
        EnsureNoLinks(application);
        EnsureNoLinks(target);
        EnsureNoLinks(source);
        if (File.Exists(target)) throw new IOException("程序旁的 Preview 路径被文件占用，未改用其他保存位置：" + target);
        if (!inPlace)
        {
            if (!replaceTarget) ValidateEmptyTarget(target);
            if (ApplicationPaths.IsWithin(target, source) || ApplicationPaths.IsWithin(source, target))
                throw new IOException("原音色库与程序旁 Preview 不能相互包含，原文件未更改。");
        }
        Directory.CreateDirectory(application);
        string stage = Path.Combine(application, ".sp-install-" + Guid.NewGuid().ToString("N")[..16]);
        Directory.CreateDirectory(stage); // Also verifies write permission before opening any source database.
        bool published = false;
        try
        {
            string sourceDatabase = Path.Combine(source, "library.db");
            if (!File.Exists(sourceDatabase))
            {
                if (!inPlace) throw new DirectoryNotFoundException("原音色库数据库不存在：" + sourceDatabase);
                ValidateEmptyTarget(target);
                var fresh = new LibraryStore(Path.Combine(stage, "library.db"));
                fresh.SaveSettings(new AppSettings { PreviewDirectory = target });
                Publish(stage, target);
                published = true;
                return new(target, source, 0, warnings);
            }
            progress?.Report("正在将旧音色库安全复制到程序旁 Preview；原库和原音频保留。");
            EnsureNoLinks(sourceDatabase);
            string backupRelative = Path.Combine("Backups", "library-before-install-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8] + ".db");
            string backup = Path.Combine(stage, backupRelative);
            LibraryStore.SnapshotExistingDatabase(sourceDatabase, backup);
            string stagedDatabase = Path.Combine(stage, "library.db");
            File.Copy(backup, stagedDatabase);
            int oldVersion;
            using (var snapshot = new NativeSqlite(backup))
                oldVersion = int.Parse(snapshot.QueryStrings("PRAGMA user_version;")[0], CultureInfo.InvariantCulture);
            var destination = new LibraryStore(stagedDatabase);
            destination.Initialize(); // Only the private copy is upgraded; the old library remains usable by its old version.
            var plan = new ImportPlan(source, target, stage, inPlace);
            plan.Warnings.AddRange(warnings);
            ReadAndPreparePaths(stagedDatabase, plan);
            if (!inPlace) CopyConfig(Path.Combine(source, "Config"), Path.Combine(stage, "Config"), plan);
            bool changed = SavePathChanges(stagedDatabase, plan);
            // A syntactically valid JSON document can still contain invalid known model fields.
            // Check the exact stored copy before it can replace a usable installed library.
            destination.LoadPresets();
            destination.LoadSettings();
            if (inPlace && !changed && oldVersion == LibraryStore.CurrentSchemaVersion)
                return new(target, source, 0, plan.Warnings);
            string? retained = null;
            if (!inPlace)
            {
                if (replaceTarget) retained = PublishRetainingTarget(stage, target);
                else Publish(stage, target);
                published = true;
            }
            else CommitInPlace(sourceDatabase, stagedDatabase, backup, backupRelative, plan);
            plan.Warnings.Add("原音色库、原预览及个人配置均已保留；迁移前的一致数据库快照位于：" + Path.Combine(target, backupRelative));
            if (retained is not null)
                plan.Warnings.Add("导入前的 Preview 完整保留于：" + retained + "；若需回退，先退出程序并恢复为原 Preview 目录名。");
            return new(target, source, plan.CopiedFiles, plan.Warnings, retained);
        }
        finally
        {
            if (!published) DeleteStage(stage);
        }
    }

    private static void ReadAndPreparePaths(string databasePath, ImportPlan plan)
    {
        using var database = new NativeSqlite(databasePath);
        var ids = database.QueryStrings("SELECT id FROM presets ORDER BY id;");
        var json = database.QueryStrings("SELECT json FROM presets ORDER BY id;");
        for (int i = 0; i < ids.Count; i++)
        {
            var node = JsonNode.Parse(json[i]) as JsonObject ?? throw new InvalidDataException("音色库包含无效记录，原库未更改。");
            plan.Records.Add(new(ids[i], json[i], node));
        }
        string? settingsJson = database.QueryStrings("SELECT json FROM settings WHERE key='application';").SingleOrDefault();
        plan.SettingsOriginal = settingsJson;
        plan.Settings = settingsJson is null
            ? JsonSerializer.SerializeToNode(new AppSettings(), LibraryJson.Options)!.AsObject()
            : JsonNode.Parse(settingsJson) as JsonObject ?? throw new InvalidDataException("音色库包含无效设置，原库未更改。");
        foreach (var record in plan.Records)
        {
            string? path = Normalize(Text(record.Node, "PreviewPath"), plan.Source);
            if (path is null) continue;
            if (CopyAudio(path, plan) is string next) Set(record.Node, "PreviewPath", next);
        }
        if (Node(plan.Settings, "Plugins") is JsonArray plugins)
            foreach (var plugin in plugins.OfType<JsonObject>())
            {
                string? path = Normalize(Text(plugin, "InitBaselinePath"), plan.Source);
                if (path is null) continue;
                if (CopyBaseline(path, plan) is string next) Set(plugin, "InitBaselinePath", next);
            }
        Set(plan.Settings, "PreviewDirectory", plan.Target);
    }

    private static string? CopyAudio(string source, ImportPlan plan, bool baseline = false)
    {
        if (baseline && !Path.GetExtension(source).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            plan.Warnings.Add("基准引用不是 WAV 文件，已保留原记录及文件：" + source);
            return null;
        }
        if (plan.Mappings.TryGetValue(source, out string? known)) return known;
        if (!File.Exists(source))
        {
            plan.Warnings.Add("原预览或基准音频缺失，已保留原缓存记录，未清空或重新生成：" + source);
            return null;
        }
        EnsureNoLinks(source);
        if (!PreviewAudioFile.IsSupportedExtension(source))
        {
            plan.Warnings.Add("预览引用指向不支持的音频文件，已保留原记录及文件：" + source);
            return null;
        }
        if (plan.InPlace && ApplicationPaths.IsWithin(source, plan.Target)) return plan.Mappings[source] = source;
        string relative = string.Equals(Path.GetFileName(Path.GetDirectoryName(source)), "InitializationBaselines", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine("InitializationBaselines", Path.GetFileName(source)) : Path.GetFileName(source);
        string result = StageFile(source, relative, plan);
        plan.Mappings[source] = result;
        return result;
    }

    private static string? CopyBaseline(string source, ImportPlan plan)
    {
        if (plan.Mappings.TryGetValue(source, out string? known)) return known;
        if (!File.Exists(source))
        {
            plan.Warnings.Add("原基准配置缺失，已保留原引用，未清空：" + source);
            return null;
        }
        EnsureNoLinks(source);
        if (new FileInfo(source).Length > 4 * 1024 * 1024)
            throw new IOException("基准配置过大，未更改原库：" + source);
        string original = File.ReadAllText(source);
        JsonObject? metadata;
        try { metadata = JsonNode.Parse(original) as JsonObject; }
        catch (JsonException error) { throw new InvalidDataException("无法读取原基准配置，未清空原引用：" + source, error); }
        if (metadata is null || Text(metadata, "Kind") != InitBaselineService.BaselineKind)
            throw new InvalidDataException("无法识别原基准配置，未清空原引用：" + source);
        RewriteBaseline(metadata, plan);
        bool rewritten = !JsonNode.DeepEquals(JsonNode.Parse(original), metadata);
        if (!rewritten && plan.InPlace && ApplicationPaths.IsWithin(source, plan.Target))
            return plan.Mappings[source] = source;
        string relative = Path.Combine("InitializationBaselines", Path.GetFileName(source));
        string result;
        if (rewritten)
        {
            string temporary = Path.Combine(plan.Stage, ".baseline-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(temporary, metadata.ToJsonString(LibraryJson.Options));
            try { result = StageFile(temporary, relative, plan, source); }
            finally { File.Delete(temporary); }
        }
        else result = StageFile(source, relative, plan);
        plan.Mappings[source] = result;
        return result;
    }

    private static void RewriteBaseline(JsonNode? node, ImportPlan plan)
    {
        if (node is JsonObject obj)
            foreach (string key in obj.Select(item => item.Key).ToList())
            {
                if ((key.Equals("WavPath", StringComparison.OrdinalIgnoreCase) || key.Equals("OutputPath", StringComparison.OrdinalIgnoreCase)) &&
                    obj[key] is JsonValue value && value.TryGetValue<string>(out string? text) && Normalize(text, plan.Source) is string source)
                {
                    if (CopyAudio(source, plan, baseline: true) is string next) obj[key] = next;
                }
                else RewriteBaseline(obj[key], plan);
            }
        else if (node is JsonArray array) foreach (var item in array) RewriteBaseline(item, plan);
    }

    private static string StageFile(string source, string preferredRelative, ImportPlan plan, string? identity = null)
    {
        EnsureNoLinks(source);
        string hash = Fingerprints.HashFile(source);
        string relative = preferredRelative;
        if (Conflicts(relative, hash, plan))
        {
            relative = Path.Combine("MigratedAudio", Fingerprints.HashText(identity ?? source)[..20], Path.GetFileName(preferredRelative));
            if (Conflicts(relative, hash, plan))
                relative = Path.Combine("MigratedAudio", Fingerprints.HashText((identity ?? source) + "|" + hash)[..20], Path.GetFileName(preferredRelative));
            if (Conflicts(relative, hash, plan)) throw new IOException("迁移目标存在冲突文件，未覆盖：" + Path.Combine(plan.Target, relative));
        }
        string destination = Path.Combine(plan.Stage, relative);
        string final = Path.Combine(plan.Target, relative);
        if (plan.StagedFiles.ContainsKey(relative) || File.Exists(destination) || plan.InPlace && File.Exists(final)) return final;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
        if (hash != Fingerprints.HashFile(destination) || hash != Fingerprints.HashFile(source))
            throw new IOException("迁移文件复制校验失败，原文件未更改：" + source);
        plan.StagedFiles.Add(relative, hash);
        plan.CopiedFiles++;
        return final;
    }

    private static bool Conflicts(string relative, string hash, ImportPlan plan)
    {
        if (plan.StagedFiles.TryGetValue(relative, out string? stagedHash)) return stagedHash != hash;
        // APFS may ignore filename case even though PathComparer deliberately preserves it.
        // Ask the filesystem as well, so differently cased basenames cannot overwrite each other.
        string staged = Path.Combine(plan.Stage, relative);
        EnsureNoLinks(staged);
        if (Directory.Exists(staged)) return true;
        if (File.Exists(staged)) return Fingerprints.HashFile(staged) != hash;
        string final = Path.Combine(plan.Target, relative);
        EnsureNoLinks(final);
        if (Directory.Exists(final)) return true;
        return plan.InPlace && File.Exists(final) && Fingerprints.HashFile(final) != hash;
    }

    private static bool SavePathChanges(string databasePath, ImportPlan plan)
    {
        var changedRecords = plan.Records.Where(record => !JsonNode.DeepEquals(JsonNode.Parse(record.Original), record.Node)).ToList();
        bool changedSettings = plan.SettingsOriginal is null || !JsonNode.DeepEquals(JsonNode.Parse(plan.SettingsOriginal), plan.Settings);
        if (changedRecords.Count == 0 && !changedSettings) return false;
        using var database = new NativeSqlite(databasePath);
        database.Execute("BEGIN IMMEDIATE;");
        try
        {
            foreach (var record in changedRecords)
                database.Execute("UPDATE presets SET json=? WHERE id=?;", record.Node.ToJsonString(LibraryJson.Options), record.Id);
            if (changedSettings)
            {
                if (plan.SettingsOriginal is null)
                    database.Execute("INSERT INTO settings (key,json) VALUES ('application',?);", plan.Settings.ToJsonString(LibraryJson.Options));
                else database.Execute("UPDATE settings SET json=? WHERE key='application';", plan.Settings.ToJsonString(LibraryJson.Options));
            }
            database.Execute("COMMIT;");
        }
        catch { database.Execute("ROLLBACK;"); throw; }
        if (database.QueryStrings("PRAGMA quick_check;").Any(result => result != "ok")) throw new InvalidDataException("迁移副本完整性校验失败，原库未更改。");
        foreach (var record in changedRecords)
            if (!JsonNode.DeepEquals(JsonNode.Parse(database.QueryStrings("SELECT json FROM presets WHERE id=?;", record.Id).Single()), record.Node))
                throw new IOException("迁移副本记录校验失败，原库未更改。");
        if (changedSettings && !JsonNode.DeepEquals(JsonNode.Parse(database.QueryStrings("SELECT json FROM settings WHERE key='application';").Single()), plan.Settings))
            throw new IOException("迁移副本设置校验失败，原库未更改。");
        return true;
    }

    private static void CommitInPlace(string sourceDatabase, string stagedDatabase, string backup, string backupRelative, ImportPlan plan)
    {
        string comparison = Path.Combine(plan.Stage, ".before-commit.db");
        LibraryStore.SnapshotExistingDatabase(sourceDatabase, comparison);
        if (Fingerprints.HashFile(comparison) != Fingerprints.HashFile(backup))
            throw new IOException("迁移期间音色库发生变化，请关闭其他版本后重试；未修改原库。");
        File.Delete(comparison);
        var created = new List<(string Path, string Hash)>();
        try
        {
            foreach (var (relative, hash) in plan.StagedFiles)
            {
                string final = Path.Combine(plan.Target, relative);
                EnsureNoLinks(final);
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                if (File.Exists(final))
                {
                    if (Fingerprints.HashFile(final) != hash) throw new IOException("提交前目标文件发生变化，未覆盖：" + final);
                    continue;
                }
                File.Move(Path.Combine(plan.Stage, relative), final);
                created.Add((final, hash));
            }
            string finalBackup = Path.Combine(plan.Target, backupRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(finalBackup)!);
            string backupHash = Fingerprints.HashFile(backup);
            File.Move(backup, finalBackup);
            if (backupHash != Fingerprints.HashFile(finalBackup)) throw new IOException("迁移前快照备份校验失败，原库未更改。");
            // SQLite commits this complete, already-validated database copy as one write transaction,
            // including when the installed library has a live WAL. Never replace a .db beneath its WAL.
            using var snapshot = new NativeSqlite(stagedDatabase);
            snapshot.BackupTo(sourceDatabase);
        }
        catch
        {
            foreach (var (path, hash) in created.AsEnumerable().Reverse())
                if (File.Exists(path) && Fingerprints.HashFile(path) == hash) File.Delete(path);
            throw;
        }
    }

    private static void CopyConfig(string source, string destination, ImportPlan plan)
    {
        if (!Directory.Exists(source)) return;
        EnsureNoLinks(source);
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            EnsureNoLinks(file);
            string output = Path.Combine(destination, Path.GetFileName(file));
            string hash = Fingerprints.HashFile(file);
            File.Copy(file, output);
            if (Fingerprints.HashFile(output) != hash || Fingerprints.HashFile(file) != hash) throw new IOException("个人配置复制校验失败，原库未更改：" + file);
            plan.CopiedFiles++;
        }
        foreach (string directory in Directory.EnumerateDirectories(source)) CopyConfig(directory, Path.Combine(destination, Path.GetFileName(directory)), plan);
    }

    private static string? Normalize(string? path, string root) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path, root);
    private static string Key(JsonObject obj, string key) => obj.Select(item => item.Key).FirstOrDefault(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
    private static JsonNode? Node(JsonObject obj, string key) => obj[Key(obj, key)];
    private static string? Text(JsonObject obj, string key) => Node(obj, key) is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;
    private static void Set(JsonObject obj, string key, string value) => obj[Key(obj, key)] = value;

    private static void ValidateEmptyTarget(string target)
    {
        EnsureNoLinks(target);
        if (File.Exists(target) || Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("程序旁 Preview 已有音色库或其他文件，未覆盖或合并。请先备份现有 Preview 后再导入：" + target);
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不能迁移链接或重解析点路径：" + current);
    }

    private static void Publish(string stage, string target)
    {
        ValidateEmptyTarget(target);
        if (Directory.Exists(target)) Directory.Delete(target); // Only the just-checked empty directory.
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { Directory.Move(stage, target); return; }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 5 or 32 or 33 &&
                timeout.Elapsed < TimeSpan.FromSeconds(1) && Directory.Exists(stage) && !Directory.Exists(target) && !File.Exists(target))
            { Thread.Sleep(40); }
        }
    }

    private static string? PublishRetainingTarget(string stage, string target)
    {
        EnsureNoLinks(target);
        if (!Directory.Exists(target)) { Publish(stage, target); return null; }
        string retained = Path.Combine(Path.GetDirectoryName(target)!, "Preview-before-import-" + Guid.NewGuid().ToString("N"));
        Directory.Move(target, retained);
        try { Publish(stage, target); }
        catch (Exception publishError)
        {
            if (Directory.Exists(target) || File.Exists(target))
                throw new IOException("新库发布失败，目标位置已存在其他内容，未覆盖；原 Preview 完整保留于：" + retained + "。请退出程序后恢复原目录名。", publishError);
            try { Directory.Move(retained, target); }
            catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
            {
                throw new AggregateException("新库发布失败且原目录未能自动恢复；原 Preview 完整保留于：" + retained + "。请退出程序后恢复原目录名。", publishError, restoreError);
            }
            throw;
        }
        return retained;
    }

    private static void DeleteStage(string stage)
    {
        if (!Directory.Exists(stage)) return;
        if (!Path.GetFileName(stage).StartsWith(".sp-install-", StringComparison.Ordinal)) throw new IOException("拒绝清理非本次迁移的暂存目录。");
        EnsureNoLinks(stage);
        foreach (string directory in Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories)) EnsureNoLinks(directory);
        foreach (string file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)) { EnsureNoLinks(file); File.Delete(file); }
        foreach (string directory in Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length)) Directory.Delete(directory);
        Directory.Delete(stage);
    }

    private sealed record JsonRecord(string Id, string Original, JsonObject Node);
    private sealed class ImportPlan(string source, string target, string stage, bool inPlace)
    {
        public string Source { get; } = source;
        public string Target { get; } = target;
        public string Stage { get; } = stage;
        public bool InPlace { get; } = inPlace;
        public List<JsonRecord> Records { get; } = [];
        public JsonObject Settings { get; set; } = new();
        public string? SettingsOriginal { get; set; }
        public Dictionary<string, string> Mappings { get; } = new(ApplicationPaths.PathComparer);
        public Dictionary<string, string> StagedFiles { get; } = new(ApplicationPaths.PathComparer);
        public List<string> Warnings { get; } = [];
        public int CopiedFiles { get; set; }
    }
}
