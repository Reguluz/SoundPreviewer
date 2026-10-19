using System.Globalization;
using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;

namespace SoundPreviewer.Core.Storage;

public sealed class LibraryStore
{
    public const int CurrentSchemaVersion = 2;
    private readonly string databasePath;
    private readonly object gate = new();
    private bool initialized;
    public string? LastMigrationBackupPath { get; private set; }
    public string DatabasePath => databasePath;

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

    public LibraryStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        databasePath = Path.GetFullPath(dbPath);
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
                .Select(json => JsonSerializer.Deserialize<PresetRecord>(json, LibraryJson.Options) ?? throw new InvalidDataException("A stored preset has no metadata."))
                .ToList();
        }
    }

    public void Upsert(PresetRecord preset) => UpsertMany([preset]);

    // Queue snapshots own only generated fields. Merge under the same transaction as the
    // read so a concurrent metadata edit cannot be replaced by an older job snapshot.
    internal PresetRecord MergeRenderState(PresetRecord rendered, PresetRecord identity) =>
        MergeRenderStates([(rendered, identity, false)])[0];

    internal List<PresetRecord> MergeRenderStates(IReadOnlyList<(PresetRecord Rendered, PresetRecord Identity, bool AudioOnly)> updates)
    {
        lock (gate)
        {
            Initialize();
            using var database = new NativeSqlite(databasePath);
            database.Execute("BEGIN IMMEDIATE;");
            try
            {
                var merged = new List<PresetRecord>();
                foreach (var (rendered, identity, audioOnly) in updates)
                {
                    string? json = database.QueryStrings("SELECT json FROM presets WHERE id=?;", identity.Id).SingleOrDefault();
                    var latest = JsonSerializer.Deserialize<PresetRecord>(json ?? JsonSerializer.Serialize(identity, LibraryJson.Options), LibraryJson.Options)
                        ?? throw new InvalidDataException("音色记录缺少元数据。");
                    if (latest.ContentHash != identity.ContentHash || latest.Synth != identity.Synth || latest.SourceId != identity.SourceId ||
                        latest.SourcePath != identity.SourcePath || latest.InternalPath != identity.InternalPath || latest.BankSlot != identity.BankSlot ||
                        latest.DuplicateOfId != identity.DuplicateOfId || latest.FileSize != identity.FileSize || latest.LastWriteUtc != identity.LastWriteUtc)
                        throw new InvalidOperationException("生成期间音色来源已变化；已保留新记录，未挂接旧任务的预览。");
                    if (!audioOnly) CopyRenderFields(latest, rendered);
                    else if (latest.PreviewPath == identity.PreviewPath && latest.CacheKey == identity.CacheKey)
                    {
                        CopyAudioFields(latest, rendered);
                        if (latest.Status == PresetStatus.Ready && rendered.Status == PresetStatus.NeedsReview)
                            latest.Status = PresetStatus.NeedsReview;
                    }
                    merged.Add(latest);
                }
                WritePresets(database, merged);
                database.Execute("COMMIT;");
                return merged;
            }
            catch { database.Execute("ROLLBACK;"); throw; }
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
                var latest = JsonSerializer.Deserialize<AppSettings>(json ?? JsonSerializer.Serialize(batchSettings, LibraryJson.Options), LibraryJson.Options)
                    ?? throw new InvalidDataException("应用设置缺少元数据。");
                var plugin = latest.Plugins.FirstOrDefault(candidate => candidate.Synth == synth);
                if (plugin is not null && (json is null || plugin.InitBaselinePath == previousPath))
                {
                    plugin.InitBaselinePath = nextPath;
                    database.Execute("INSERT OR REPLACE INTO settings (key, json) VALUES (?, ?);", "application", JsonSerializer.Serialize(latest, LibraryJson.Options));
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
                .Select(json => JsonSerializer.Deserialize<PresetRecord>(json, LibraryJson.Options) ?? throw new InvalidDataException("A stored preset has no metadata.")).ToList();
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
                database.Execute("INSERT OR REPLACE INTO settings (key, json) VALUES (?, ?);", "application", JsonSerializer.Serialize(settings, LibraryJson.Options));
                database.Execute("COMMIT;");
            }
            catch { database.Execute("ROLLBACK;"); throw; }
        }
    }

    private static void WritePresets(NativeSqlite database, IEnumerable<PresetRecord> presets)
    {
        foreach (var preset in presets)
        {
            ArgumentNullException.ThrowIfNull(preset);
            ArgumentException.ThrowIfNullOrWhiteSpace(preset.Id);
            database.Execute("INSERT OR REPLACE INTO presets (id, source_id, synth, source_path, internal_path, content_hash, content_md5, duplicate_of_id, status, favorite, feedback, json, updated_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                preset.Id, preset.SourceId, (int)preset.Synth, preset.SourcePath, preset.InternalPath, preset.ContentHash, preset.ContentMd5.ToLowerInvariant(), preset.DuplicateOfId, (int)preset.Status, preset.Favorite, preset.Feedback, JsonSerializer.Serialize(preset, LibraryJson.Options), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
    }

    public AppSettings LoadSettings()
    {
        lock (gate)
        {
            Initialize();
            using var database = new NativeSqlite(databasePath);
            string? json = database.QueryStrings("SELECT json FROM settings WHERE key=?;", "application").SingleOrDefault();
            return json is null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(json, LibraryJson.Options) ?? throw new InvalidDataException("Stored application settings have no metadata.");
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (gate)
        {
            Initialize();
            using var database = new NativeSqlite(databasePath);
            database.Execute("INSERT OR REPLACE INTO settings (key, json) VALUES (?, ?);", "application", JsonSerializer.Serialize(settings, LibraryJson.Options));
        }
    }

    public int RecoverInterrupted()
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
