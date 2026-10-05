using System.Globalization;
using System.Text.Json;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Storage;

public sealed class LibraryStore
{
    private const int SchemaVersion = 1;
    private readonly string databasePath;
    private readonly object gate = new();
    private bool initialized;

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
            if (version > SchemaVersion) throw new InvalidDataException($"Database schema {version} is newer than supported schema {SchemaVersion}.");
            database.Execute("PRAGMA journal_mode=WAL;");
            database.Execute("PRAGMA synchronous=NORMAL;");
            database.Execute("BEGIN IMMEDIATE;");
            try
            {
                database.Execute("CREATE TABLE IF NOT EXISTS presets (id TEXT PRIMARY KEY NOT NULL, source_id TEXT NOT NULL, synth INTEGER NOT NULL, source_path TEXT NOT NULL, internal_path TEXT NOT NULL, content_hash TEXT NOT NULL, status INTEGER NOT NULL, favorite INTEGER NOT NULL, feedback INTEGER NOT NULL, json TEXT NOT NULL, updated_utc TEXT NOT NULL);");
                database.Execute("CREATE INDEX IF NOT EXISTS ix_presets_source ON presets(source_id);");
                database.Execute("CREATE INDEX IF NOT EXISTS ix_presets_status ON presets(status);");
                database.Execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY NOT NULL, json TEXT NOT NULL);");
                database.Execute($"PRAGMA user_version={SchemaVersion};");
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
                foreach (var preset in presets)
                {
                    ArgumentNullException.ThrowIfNull(preset);
                    ArgumentException.ThrowIfNullOrWhiteSpace(preset.Id);
                    database.Execute("INSERT OR REPLACE INTO presets (id, source_id, synth, source_path, internal_path, content_hash, status, favorite, feedback, json, updated_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                        preset.Id, preset.SourceId, (int)preset.Synth, preset.SourcePath, preset.InternalPath, preset.ContentHash, (int)preset.Status, preset.Favorite, preset.Feedback, JsonSerializer.Serialize(preset, LibraryJson.Options), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                }
                database.Execute("COMMIT;");
            }
            catch
            {
                database.Execute("ROLLBACK;");
                throw;
            }
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
