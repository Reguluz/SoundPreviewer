using System.Globalization;
using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;

namespace SoundPreviewer.Core.Storage;

public sealed class LibraryStore
{
    public const int CurrentSchemaVersion = 2;
    private readonly string databasePath;
    private readonly string previewRoot;
    private readonly object gate = new();
    private bool initialized;
    public string? LastMigrationBackupPath { get; private set; }
    public string DatabasePath => databasePath;

    // Import old libraries without initializing (and therefore upgrading) the source.
    // SQLite's backup API reads the committed WAL as part of the same snapshot.
    public static void SnapshotExistingDatabase(string sourcePath, string destinationPath)
    {
        string source = Path.GetFullPath(sourcePath), destination = Path.GetFullPath(destinationPath);
        if (!File.Exists(source)) throw new FileNotFoundException("原音色库数据库不存在。", source);
        if (ApplicationPaths.SamePath(source, destination)) throw new ArgumentException("快照不能覆盖原数据库。", nameof(destinationPath));
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("快照目标已经存在，未覆盖：" + destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        bool created = false;
        try
        {
            using (new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            created = true;
            if (!TrySnapshotClosedWal(source, destination))
            {
                using var database = new NativeSqlite(source, readOnly: true);
                if (database.QueryStrings("PRAGMA quick_check;").Any(result => result != "ok"))
                    throw new InvalidDataException("原音色库未通过数据库完整性检查，原文件未更改：" + source);
                database.BackupTo(destination);
            }
            // A backup can inherit WAL mode without inheriting its sidecars. Normalize only
            // this private copy so it is a self-contained, portable, read-only-readable image.
            using (var snapshot = new NativeSqlite(destination))
            {
                // Force the pager to read the copied database header before asking it to
                // leave WAL mode; its unopened default can otherwise report DELETE early.
                if (snapshot.QueryStrings("PRAGMA quick_check;").Any(result => result != "ok"))
                    throw new InvalidDataException("一致数据库快照未通过完整性检查，原库未更改：" + destination);
                if (!snapshot.QueryStrings("PRAGMA main.journal_mode=DELETE;").Single().Equals("delete", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("一致数据库快照无法转为独立文件，原库未更改：" + destination);
                if (snapshot.QueryStrings("PRAGMA quick_check;").Any(result => result != "ok") ||
                    !snapshot.QueryStrings("PRAGMA main.journal_mode;").Single().Equals("delete", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("一致数据库快照规范化校验失败，原库未更改：" + destination);
            }
            using (var image = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Span<byte> header = stackalloc byte[20];
                image.ReadExactly(header);
                if (header[18] != 1 || header[19] != 1)
                    throw new InvalidDataException($"一致数据库快照仍非独立回滚日志格式（文件头 {header[18]}/{header[19]}），原库未更改：{destination}");
            }
            if (HasWalSidecars(destination))
            {
                string auxiliaries = string.Join("，", new[] { "-wal", "-shm" }.Where(suffix => File.Exists(destination + suffix))
                    .Select(suffix => suffix + "=" + new FileInfo(destination + suffix).Length + " bytes"));
                System.Diagnostics.Trace.TraceInformation("数据库快照已确认文件头 1/1，但仍保留辅助文件（{0}）：{1}", auxiliaries, destination);
            }
        }
        catch
        {
            if (created && File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }

    private static bool TrySnapshotClosedWal(string source, string destination)
    {
        if (HasWalSidecars(source)) return false;
        string input = Path.Combine(Path.GetDirectoryName(destination)!, ".spi-" + Guid.NewGuid().ToString("N")[..16] + ".db");
        bool created = false;
        try
        {
            // Installed startup already owns the source-library mutex. Also verify the file
            // before and after copying so this public API rejects unexpected external changes.
            using (var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (original.Length < 20) return false;
                Span<byte> header = stackalloc byte[20];
                original.ReadExactly(header);
                if (header[18] != 2 || header[19] != 2) return false;
                if (HasWalSidecars(source)) throw ChangedSource(source);
                string originalHash = Fingerprints.HashFile(source);
                using (var copy = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    original.Position = 0;
                    original.CopyTo(copy);
                    copy.Flush(flushToDisk: true);
                }
                if (HasWalSidecars(source) || Fingerprints.HashFile(source) != originalHash ||
                    Fingerprints.HashFile(input) != originalHash || HasWalSidecars(source)) throw ChangedSource(source);
            }
            // Some macOS SQLite versions cannot read a WAL-mode main file through a
            // read-only connection when its WAL/SHM are absent. Initialize only our copy.
            using var database = new NativeSqlite(input);
            if (database.QueryStrings("PRAGMA quick_check;").Any(result => result != "ok"))
                throw new InvalidDataException("原音色库的私有副本未通过完整性检查，原文件未更改：" + source);
            database.BackupTo(destination);
            return true;
        }
        finally
        {
            if (created)
                foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
                    if (File.Exists(input + suffix)) File.Delete(input + suffix);
        }
    }

    private static bool HasWalSidecars(string source) => File.Exists(source + "-wal") || File.Exists(source + "-shm") ||
        Directory.Exists(source + "-wal") || Directory.Exists(source + "-shm");

    private static IOException ChangedSource(string source) => new("生成快照期间原音色库或其 WAL 状态发生变化，请关闭其他程序后重试；未忽略日志或改写原库：" + source);

    public void CreateBackup(string destinationPath)
    {
        string destination = Path.GetFullPath(destinationPath);
        if (ApplicationPaths.SamePath(destination, databasePath)) throw new ArgumentException("备份不能覆盖当前数据库。", nameof(destinationPath));
        lock (gate)
        {
            Initialize();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".spb-" + Guid.NewGuid().ToString("N")[..16] + ".db");
            try
            {
                using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                using (var database = new NativeSqlite(databasePath)) database.BackupTo(temporary);
                File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public LibraryStore(string dbPath, string? previewRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        databasePath = Path.GetFullPath(dbPath);
        this.previewRoot = Path.GetFullPath(previewRoot ?? Path.GetDirectoryName(databasePath)!);
    }

    public void Initialize()
    {
        lock (gate)
        {
            if (initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            using var database = new NativeSqlite(databasePath);
            int version = int.Parse(database.QueryStrings("PRAGMA user_version;")[0], CultureInfo.InvariantCulture);
            if (version > CurrentSchemaVersion) throw new InvalidDataException($"Database schema {version} is newer than supported schema {CurrentSchemaVersion}. Please open this library with the newer application; the library has not been changed.");
            if (version < CurrentSchemaVersion && database.QueryStrings("SELECT name FROM sqlite_master WHERE type='table' AND name IN ('presets', 'settings');").Count > 0)
                LastMigrationBackupPath = BackupBeforeMigration(database, version);
            database.Execute("PRAGMA journal_mode=WAL;");
            database.Execute("PRAGMA synchronous=NORMAL;");
            database.Execute("BEGIN IMMEDIATE;");
            try
            {
                database.Execute("CREATE TABLE IF NOT EXISTS presets (id TEXT PRIMARY KEY NOT NULL, source_id TEXT NOT NULL, synth INTEGER NOT NULL, source_path TEXT NOT NULL, internal_path TEXT NOT NULL, content_hash TEXT NOT NULL, content_md5 TEXT NOT NULL DEFAULT '', duplicate_of_id TEXT NOT NULL DEFAULT '', status INTEGER NOT NULL, favorite INTEGER NOT NULL, feedback INTEGER NOT NULL, json TEXT NOT NULL, updated_utc TEXT NOT NULL);");
                var columns = database.QueryStrings("SELECT name FROM pragma_table_info('presets');").ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!columns.Contains("content_md5")) database.Execute("ALTER TABLE presets ADD COLUMN content_md5 TEXT NOT NULL DEFAULT '';");
                if (!columns.Contains("duplicate_of_id")) database.Execute("ALTER TABLE presets ADD COLUMN duplicate_of_id TEXT NOT NULL DEFAULT '';");
                database.Execute("CREATE INDEX IF NOT EXISTS ix_presets_source ON presets(source_id);");
                database.Execute("CREATE INDEX IF NOT EXISTS ix_presets_status ON presets(status);");
                database.Execute("CREATE INDEX IF NOT EXISTS ix_presets_content_md5 ON presets(content_md5 COLLATE NOCASE);");
                database.Execute("CREATE INDEX IF NOT EXISTS ix_presets_duplicate ON presets(duplicate_of_id);");
                database.Execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY NOT NULL, json TEXT NOT NULL);");
                if (version < CurrentSchemaVersion)
                {
                    foreach (string json in database.QueryStrings("SELECT json FROM presets;"))
                    {
                        var preset = JsonSerializer.Deserialize<PresetRecord>(json, LibraryJson.Options) ?? throw new InvalidDataException("A stored preset has no metadata.");
                        database.Execute("UPDATE presets SET content_md5=?, duplicate_of_id=? WHERE id=?;", preset.ContentMd5.ToLowerInvariant(), preset.DuplicateOfId, preset.Id);
                    }
                }
                database.Execute($"PRAGMA user_version={CurrentSchemaVersion};");
                database.Execute("COMMIT;");
                initialized = true;
            }
            catch
            {
                database.Execute("ROLLBACK;");
                throw;
            }
        }
    }

    private string BackupBeforeMigration(NativeSqlite database, int version)
    {
        string backupDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, "Backups");
        Directory.CreateDirectory(backupDirectory);
        string name = $"{Path.GetFileNameWithoutExtension(databasePath)}.schema-v{version}.{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.{Guid.NewGuid():N}.db";
        string backupPath = Path.Combine(backupDirectory, name);
        string temporary = backupPath + ".tmp";
        try
        {
            // The SQLite backup API includes committed WAL pages without copying an active database file.
            database.BackupTo(temporary);
            File.Move(temporary, backupPath);
            return backupPath;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public List<PresetRecord> LoadPresets()
    {
        lock (gate)
        {
            Initialize();
            using var database = new NativeSqlite(databasePath);
            return database.QueryStrings("SELECT json FROM presets ORDER BY synth, source_path, internal_path, id;")
                .Select(ReadPreset)
                .ToList();
        }
    }

    public void Upsert(PresetRecord preset) => UpsertMany([preset]);

    // Queue snapshots own only generated fields. Merge under the same transaction as the
    // read so a concurrent metadata edit cannot be replaced by an older job snapshot.
    internal PresetRecord MergeRenderState(PresetRecord rendered, PresetRecord identity) =>
        MergeRenderStates([(rendered, identity, false)])[0];

    internal List<PresetRecord> MergeRenderStates(IReadOnlyList<(PresetRecord Rendered, PresetRecord Identity, bool AudioOnly)> updates,
        IReadOnlyList<PresetRecord>? requiredPreviewIdentities = null, bool existingRecordsOnly = false)
    {
        lock (gate)
        {
            Initialize();
            using var database = new NativeSqlite(databasePath);
            database.Execute("BEGIN IMMEDIATE;");
            try
            {
                VerifyPreviewIdentities(database, requiredPreviewIdentities);
                var merged = new List<PresetRecord>();
                var persisted = new List<PresetRecord>();
                foreach (var (rendered, identity, audioOnly) in updates)
                {
                    string? json = database.QueryStrings("SELECT json FROM presets WHERE id=?;", identity.Id).SingleOrDefault();
                    // A preflight skip can explain a transient caller input, but must
                    // never turn it into a new library entry or revive a deleted entry.
                    if (json is null && existingRecordsOnly)
                    {
                        merged.Add(rendered);
                        continue;
                    }
                    var latest = ReadPreset(json ?? JsonSerializer.Serialize(identity, LibraryJson.Options));
                    if (requiredPreviewIdentities is not null && (json is null || latest.Status == PresetStatus.Rendering))
                        throw new InvalidOperationException("复用预览期间副本已删除或开始生成；未更改其预览。");
                    if (!SameRenderSource(latest, identity))
                        throw new InvalidOperationException("生成期间音色来源已变化；已保留新记录，未挂接旧任务的预览。");
                    if (!audioOnly) CopyRenderFields(latest, rendered);
                    else if (latest.PreviewPath == identity.PreviewPath && latest.CacheKey == identity.CacheKey)
                    {
                        CopyAudioFields(latest, rendered);
                        if (latest.Status == PresetStatus.Ready && rendered.Status == PresetStatus.NeedsReview)
                            latest.Status = PresetStatus.NeedsReview;
                    }
                    merged.Add(latest);
                    persisted.Add(latest);
                }
                WritePresets(database, persisted);
                VerifyPreviewIdentities(database, requiredPreviewIdentities);
                database.Execute("COMMIT;");
                return merged;
            }
            catch { database.Execute("ROLLBACK;"); throw; }
        }
    }

    private static bool SameRenderSource(PresetRecord first, PresetRecord second) =>
        first.ContentHash == second.ContentHash && first.Synth == second.Synth && first.SourceId == second.SourceId &&
        first.SourcePath == second.SourcePath && first.InternalPath == second.InternalPath && first.BankSlot == second.BankSlot &&
        first.DuplicateOfId == second.DuplicateOfId && first.FileSize == second.FileSize && first.LastWriteUtc == second.LastWriteUtc;

    internal static string AudioFieldsIdentity(PresetRecord record) => JsonSerializer.Serialize(new
    {
        record.PreviewPath, record.CacheKey, record.RenderedUtc, record.Metrics, record.Notes,
        record.AdapterVersion, record.PluginVersion, record.TemplateJson
    }, LibraryJson.Options);

    private void VerifyPreviewIdentities(NativeSqlite database, IReadOnlyList<PresetRecord>? required)
    {
        if (required is null) return;
        foreach (var identity in required)
        {
            string? json = database.QueryStrings("SELECT json FROM presets WHERE id=?;", identity.Id).SingleOrDefault();
            var latest = json is null ? null : ReadPreset(json);
            if (latest is null || latest.Status == PresetStatus.Rendering || !SameRenderSource(latest, identity) ||
                AudioFieldsIdentity(latest) != AudioFieldsIdentity(identity))
                throw new InvalidOperationException("复用预览期间保留项的来源或预览已变化；未更改副本记录。");
        }
    }

    internal static void CopyRenderFields(PresetRecord target, PresetRecord rendered)
    {
        target.Status = rendered.Status;
        target.Attempts = rendered.Attempts;
        target.Message = rendered.Message;
        target.LastRenderOutcome = rendered.LastRenderOutcome;
        target.LastRenderMessage = rendered.LastRenderMessage;
        target.AttemptedCacheKey = rendered.AttemptedCacheKey;
        CopyAudioFields(target, rendered);
    }

    internal static void CopyAudioFields(PresetRecord target, PresetRecord rendered)
    {
        // A committed replacement (including the same cache filename) supersedes a
        // missing legacy reference. It must not be restored if the new file is later lost.
        if (File.Exists(rendered.PreviewPath) && target.AdditionalProperties is { } properties)
            foreach (string key in properties.Keys.Where(key => key.Equals("HistoricalPreviewPath", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("HistoricalPreviewPathLocalPath", StringComparison.OrdinalIgnoreCase)).ToArray())
                properties.Remove(key);
        target.PreviewPath = rendered.PreviewPath;
        target.CacheKey = rendered.CacheKey;
        target.RenderedUtc = rendered.RenderedUtc;
        target.Metrics = rendered.Metrics;
        target.Notes = rendered.Notes;
        target.AdapterVersion = rendered.AdapterVersion;
        target.PluginVersion = rendered.PluginVersion;
        target.TemplateJson = rendered.TemplateJson;
    }

    internal void SaveInitializationBaseline(AppSettings batchSettings, SynthKind synth, string previousPath, string nextPath)
    {
        lock (gate)
        {
            Initialize();
            using var database = new NativeSqlite(databasePath);
            database.Execute("BEGIN IMMEDIATE;");
            try
            {
                string? json = database.QueryStrings("SELECT json FROM settings WHERE key=?;", "application").SingleOrDefault();
                var latest = ReadSettings(json ?? JsonSerializer.Serialize(batchSettings, LibraryJson.Options));
                var plugin = latest.Plugins.FirstOrDefault(candidate => candidate.Synth == synth);
                if (plugin is not null && (json is null || plugin.InitBaselinePath == previousPath))
                {
                    plugin.InitBaselinePath = nextPath;
                    database.Execute("INSERT OR REPLACE INTO settings (key, json) VALUES (?, ?);", "application", WriteSettings(latest));
                }
                database.Execute("COMMIT;");
            }
            catch { database.Execute("ROLLBACK;"); throw; }
        }
    }

    public List<PresetRecord> FindByMd5(string md5)
    {
        ArgumentNullException.ThrowIfNull(md5);
        if (!PresetIndex.IsMd5(md5)) throw new ArgumentException("MD5 须为 32 位十六进制字符串。", nameof(md5));
        lock (gate)
        {
            Initialize();
            using var database = new NativeSqlite(databasePath);
            return database.QueryStrings("SELECT json FROM presets WHERE content_md5=? COLLATE NOCASE ORDER BY synth, source_path, internal_path, id;", md5)
                .Select(ReadPreset).ToList();
        }
    }

    public void UpsertMany(IEnumerable<PresetRecord> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        lock (gate)
        {
            Initialize();
            using var database = new NativeSqlite(databasePath);
            database.Execute("BEGIN IMMEDIATE;");
            try
            {
                WritePresets(database, presets);
                database.Execute("COMMIT;");
            }
            catch
            {
                database.Execute("ROLLBACK;");
                throw;
            }
        }
    }

    public void SaveState(IEnumerable<PresetRecord> presets, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(presets);
        ArgumentNullException.ThrowIfNull(settings);
        lock (gate)
        {
            Initialize();
            using var database = new NativeSqlite(databasePath);
            database.Execute("BEGIN IMMEDIATE;");
            try
            {
                WritePresets(database, presets);
                database.Execute("INSERT OR REPLACE INTO settings (key, json) VALUES (?, ?);", "application", WriteSettings(settings));
                database.Execute("COMMIT;");
            }
            catch { database.Execute("ROLLBACK;"); throw; }
        }
    }

    private void WritePresets(NativeSqlite database, IEnumerable<PresetRecord> presets)
    {
        foreach (var preset in presets)
        {
            ArgumentNullException.ThrowIfNull(preset);
            ArgumentException.ThrowIfNullOrWhiteSpace(preset.Id);
            database.Execute("INSERT OR REPLACE INTO presets (id, source_id, synth, source_path, internal_path, content_hash, content_md5, duplicate_of_id, status, favorite, feedback, json, updated_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                preset.Id, preset.SourceId, (int)preset.Synth, preset.SourcePath, preset.InternalPath, preset.ContentHash, preset.ContentMd5.ToLowerInvariant(), preset.DuplicateOfId, (int)preset.Status, preset.Favorite, preset.Feedback, WritePreset(preset), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
    }

    public AppSettings LoadSettings()
    {
        lock (gate)
        {
            Initialize();
            using var database = new NativeSqlite(databasePath);
            string? json = database.QueryStrings("SELECT json FROM settings WHERE key=?;", "application").SingleOrDefault();
            return json is null ? new AppSettings() : ReadSettings(json);
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (gate)
        {
            Initialize();
            using var database = new NativeSqlite(databasePath);
            database.Execute("INSERT OR REPLACE INTO settings (key, json) VALUES (?, ?);", "application", WriteSettings(settings));
        }
    }

    private PresetRecord ReadPreset(string json)
    {
        var preset = JsonSerializer.Deserialize<PresetRecord>(json, LibraryJson.Options)
            ?? throw new InvalidDataException("音色记录缺少元数据。");
        preset.PreviewPath = PreviewPathStorage.Resolve(preset.PreviewPath, previewRoot);
        return preset;
    }

    private string WritePreset(PresetRecord preset)
    {
        var node = JsonSerializer.SerializeToNode(preset, LibraryJson.Options)!.AsObject();
        node[nameof(PresetRecord.PreviewPath)] = PreviewPathStorage.Store(preset.PreviewPath, previewRoot);
        return node.ToJsonString(LibraryJson.Options);
    }

    private AppSettings ReadSettings(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, LibraryJson.Options)
            ?? throw new InvalidDataException("应用设置缺少元数据。");
        settings.PreviewDirectory = PreviewPathStorage.Resolve(settings.PreviewDirectory, previewRoot);
        foreach (var plugin in settings.Plugins)
            plugin.InitBaselinePath = PreviewPathStorage.Resolve(plugin.InitBaselinePath, previewRoot);
        return settings;
    }

    private string WriteSettings(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, LibraryJson.Options)!.AsObject();
        node[nameof(AppSettings.PreviewDirectory)] = PreviewPathStorage.Store(settings.PreviewDirectory, previewRoot);
        foreach (var plugin in node[nameof(AppSettings.Plugins)]!.AsArray())
            plugin![nameof(PluginConfiguration.InitBaselinePath)] = PreviewPathStorage.Store(
                plugin[nameof(PluginConfiguration.InitBaselinePath)]!.GetValue<string>(), previewRoot);
        return node.ToJsonString(LibraryJson.Options);
    }

    public int RecoverInterrupted(bool preserveMissingPreviewMappings = false)
    {
        lock (gate)
        {
            var changed = new List<PresetRecord>();
            foreach (var preset in LoadPresets())
            {
                bool interrupted = preset.Status == PresetStatus.Rendering;
                bool missingPreview = (preset.Status == PresetStatus.Ready || preset.Status == PresetStatus.NeedsReview) &&
                    (string.IsNullOrWhiteSpace(preset.PreviewPath) || !File.Exists(preset.PreviewPath));
                if (!interrupted && !missingPreview) continue;
                if (missingPreview && !interrupted && preserveMissingPreviewMappings) continue;
                if (interrupted)
                {
                    preset.LastRenderOutcome = "已停止";
                    preset.LastRenderMessage = "上次生成被中断；已有完整预览保留，可继续生成。";
                }
                if (interrupted && PresetIndex.HasRecoverablePreview(preset))
                {
                    preset.Status = PresetStatus.NeedsReview;
                    preset.Message = "上次重新生成被中断；已保留原有预览，等待试听确认。";
                    changed.Add(preset);
                    continue;
                }
                preset.Status = PresetStatus.Pending;
                preset.Message = interrupted ? "上次渲染被中断，等待恢复。" : "预览音频已丢失，等待重新生成。";
                preset.PreviewPath = "";
                preset.CacheKey = "";
                preset.RenderedUtc = null;
                preset.Metrics = null;
                preset.Notes = [];
                preset.AdapterVersion = "";
                preset.PluginVersion = "";
                preset.TemplateJson = "";
                changed.Add(preset);
            }
            if (changed.Count > 0) UpsertMany(changed);
            return changed.Count;
        }
    }
}
