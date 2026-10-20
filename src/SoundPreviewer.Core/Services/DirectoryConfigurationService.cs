using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Services;

/// <summary>Editable installation-local directory settings; the library keeps a compatible copy.</summary>
public sealed class DirectoryConfigurationService
{
    public const string FileName = "directories.json";
    public const int CurrentFormatVersion = 1;
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private readonly string applicationDirectory;
    private Configuration? loaded;
    private string? loadedHash;

    public DirectoryConfigurationService(string? applicationDirectory = null)
    {
        this.applicationDirectory = Path.GetFullPath(applicationDirectory ?? ApplicationPaths.PortableApplicationDirectory);
        FilePath = Path.Combine(this.applicationDirectory, FileName);
    }

    public string FilePath { get; }

    /// <summary>Loads explicit JSON over database directory settings, or exports the database on first use.</summary>
    public bool LoadOrCreate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool created = !File.Exists(FilePath);
        Configuration configuration;
        string? hash;
        if (created)
        {
            configuration = Normalize(new Configuration { Sources = Clone(settings.Sources), PluginDirectories = [.. settings.PluginDirectories] }, settings.Sources);
            hash = Write(configuration, expectedHash: null);
        }
        else
        {
            byte[] bytes = ReadBytes();
            hash = Hash(bytes);
            configuration = Read(bytes, settings);
        }
        // Apply only after the whole file has been validated or successfully created.
        settings.Sources = Clone(configuration.Sources!);
        settings.PluginDirectories = [.. configuration.PluginDirectories!];
        loaded = configuration;
        loadedHash = hash;
        return created;
    }

    /// <summary>Saves UI directory edits, rejecting a file changed by an external editor since loading.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (loaded is null) throw new InvalidOperationException("请先读取目录配置，再保存目录设置。");
        var candidate = new Configuration
        {
            Sources = Clone(settings.Sources),
            PluginDirectories = [.. settings.PluginDirectories],
            AdditionalProperties = loaded.AdditionalProperties is null ? null : new(loaded.AdditionalProperties)
        };
        candidate = Normalize(candidate, loaded.Sources!);
        string hash = Write(candidate, loadedHash);
        loaded = candidate;
        loadedHash = hash;
        settings.Sources = Clone(candidate.Sources!);
        settings.PluginDirectories = [.. candidate.PluginDirectories!];
    }

    private Configuration Read(byte[] bytes, AppSettings fallback)
    {
        try
        {
            // Editors on Windows can emit a UTF-8 BOM. Keep hashing the original
            // bytes for conflict detection, but omit that encoding marker while parsing.
            ReadOnlyMemory<byte> json = bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("文件根节点必须是对象");
            RejectDuplicateProperties(document.RootElement);
            var configuration = JsonSerializer.Deserialize<Configuration>(json.Span, Options) ?? throw Invalid("文件内容为空");
            if (configuration.FormatVersion != CurrentFormatVersion)
                throw Invalid($"配置格式版本 {configuration.FormatVersion} 暂不支持，请使用对应版本的程序");
            if (Property(document.RootElement, nameof(Configuration.Sources)) is { } sources)
            {
                if (sources.ValueKind != JsonValueKind.Array || configuration.Sources is null) throw Invalid("Sources 必须是目录数组，空列表请填写 []");
                int index = 0;
                foreach (var source in sources.EnumerateArray())
                {
                    if (source.ValueKind != JsonValueKind.Object || configuration.Sources[index] is null) throw Invalid("Sources 中每项必须是目录对象");
                    RejectDuplicateProperties(source);
                    // SourceFolder's default ID is for newly created UI rows, not a missing hand-edited ID.
                    if (Property(source, nameof(SourceFolder.Id)) is null) configuration.Sources[index].Id = "";
                    index++;
                }
            }
            else configuration.Sources = Clone(fallback.Sources);
            if (Property(document.RootElement, nameof(Configuration.PluginDirectories)) is { } directories)
            {
                if (directories.ValueKind != JsonValueKind.Array || configuration.PluginDirectories is null) throw Invalid("PluginDirectories 必须是路径数组，空列表请填写 []");
            }
            else configuration.PluginDirectories = [.. fallback.PluginDirectories];
            return Normalize(configuration, fallback.Sources);
        }
        catch (JsonException error)
        {
            throw Invalid($"JSON 语法或字段类型无效（第 {(error.LineNumber ?? 0) + 1} 行）", error);
        }
    }

    private Configuration Normalize(Configuration configuration, IReadOnlyList<SourceFolder> fallback)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in configuration.Sources!)
        {
            if (source is null) throw Invalid("Sources 中不能包含 null");
            source.Path = NormalizePath(source.Path, "音色来源");
            if (!Enum.IsDefined(source.Synth)) throw Invalid("音色来源的 Synth 无效");
            SourceFolder? previous = !string.IsNullOrWhiteSpace(source.Id)
                ? fallback.FirstOrDefault(item => item.Id == source.Id)
                : UniqueMatchingSource(fallback, source);
            if (string.IsNullOrWhiteSpace(source.Id)) source.Id = previous?.Id ?? Guid.NewGuid().ToString("N");
            if (!ids.Add(source.Id)) throw Invalid("Sources 中存在重复 Id，请为每个来源保留独立 Id");
            if (previous?.AdditionalProperties is not null)
            {
                var properties = new Dictionary<string, JsonElement>(previous.AdditionalProperties);
                if (source.AdditionalProperties is not null)
                    foreach (var property in source.AdditionalProperties) properties[property.Key] = property.Value;
                source.AdditionalProperties = properties;
            }
        }
        configuration.PluginDirectories = configuration.PluginDirectories!
            .Select(path => NormalizePath(path, "插件目录")).Distinct(ApplicationPaths.PathComparer).ToList();
        return configuration;
    }

    private SourceFolder? UniqueMatchingSource(IReadOnlyList<SourceFolder> sources, SourceFolder candidate)
    {
        var matches = sources.Where(source => source.Synth == candidate.Synth &&
            ApplicationPaths.SamePath(NormalizePath(source.Path, "原音色来源"), candidate.Path)).ToList();
        var sameDetection = matches.Where(source => source.AutoDetect == candidate.AutoDetect).Take(2).ToList();
        return sameDetection.Count == 1 ? sameDetection[0] : matches.Count == 1 ? matches[0] : null;
    }

    private string NormalizePath(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) throw Invalid($"{label}路径不能为空");
        try { return Path.GetFullPath(path, applicationDirectory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        { throw Invalid($"{label}路径无效：{path}", error); }
    }

    private string Write(Configuration configuration, string? expectedHash)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(configuration, Options);
        if (bytes.Length > MaximumFileBytes) throw Invalid("目录配置文件超过 4 MiB，未保存");
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            CheckUnchanged(expectedHash);
            Directory.CreateDirectory(applicationDirectory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            CheckUnchanged(expectedHash);
            File.Move(temporary, FilePath, overwrite: expectedHash is not null);
            return Hash(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException($"无法保存安装目录下的目录配置：{FilePath}。请确认目录可写；现有配置未主动清除。", error); }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void CheckUnchanged(string? expectedHash)
    {
        if (expectedHash is null ? File.Exists(FilePath) : !File.Exists(FilePath) || Hash(ReadBytes()) != expectedHash)
            throw new InvalidOperationException($"目录配置文件已被其他程序修改：{FilePath}。请重新启动应用以读取修改，再调整目录；未覆盖手动修改的文件。");
    }

    private byte[] ReadBytes()
    {
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw Invalid("目录配置文件超过 4 MiB");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static List<SourceFolder> Clone(List<SourceFolder> sources) =>
        JsonSerializer.Deserialize<List<SourceFolder>>(JsonSerializer.Serialize(sources, Options), Options)!;
    private InvalidDataException Invalid(string reason, Exception? inner = null) =>
        new($"目录配置文件无法读取或保存：{FilePath}。{reason}；原文件未更改。", inner);

    private void RejectDuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw Invalid($"字段 {property.Name} 重复");
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(LibraryJson.Options) { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class Configuration
    {
        public int FormatVersion { get; set; } = CurrentFormatVersion;
        public List<SourceFolder>? Sources { get; set; }
        public List<string>? PluginDirectories { get; set; }
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    }
}
