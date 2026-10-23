using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Services;

public sealed class MetadataText
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    [JsonIgnore] public string Label => string.Join(" / ", new[] { Name }.Concat(Aliases));
}

public sealed class MetadataDefinition
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public Dictionary<string, MetadataText> Languages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PreviousNames { get; set; } = [];
    public MetadataText Text(string language) => Languages.TryGetValue(language, out var text) ? text : new() { Name = Name, Aliases = Aliases };
    public IEnumerable<string> SearchTerms => new[] { Name }.Concat(Aliases).Concat(PreviousNames)
        .Concat(Languages.Values.SelectMany(text => new[] { text.Name }.Concat(text.Aliases)));
}

/// <summary>Numeric references keep preset identity independent of editable, translated names.</summary>
public sealed class MetadataCatalog
{
    private sealed class Document
    {
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
        public int Version { get; set; } = 1;
        public string CatalogId { get; set; } = "";
        public List<MetadataDefinition> Items { get; set; } = [];
    }
    private readonly string path;
    private readonly Document document;
    private string? savedHash;
    private static readonly JsonSerializerOptions JsonOptions = new(LibraryJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public IReadOnlyList<MetadataDefinition> Items => document.Items;
    public string Id => document.CatalogId;
    public string ExportJson() => JsonSerializer.Serialize(document, JsonOptions);
    public MetadataCatalog(string path, bool categories, string? backupJson = null)
    {
        this.path = Path.GetFullPath(path);
        savedHash = File.Exists(path) ? Fingerprints.HashFile(path) : null;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("标签或分类文件超过 4 MB：" + path);
            document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), LibraryJson.Options) ?? throw new InvalidDataException("标签或分类文件为空：" + path);
        }
        else
        {
            document = new();
            if (!string.IsNullOrWhiteSpace(backupJson)) document = JsonSerializer.Deserialize<Document>(backupJson, LibraryJson.Options) ?? throw new JsonException("Invalid metadata backup.");
            else if (categories)
            {
                using var stream = typeof(MetadataCatalog).Assembly.GetManifestResourceStream("SoundPreviewer.Core.Config.categories.default.json")!;
                document = JsonSerializer.Deserialize<Document>(stream, LibraryJson.Options)!;
            }
        }
        Validate(document);
        bool needsIdentity = document.CatalogId.Length == 0;
        if (needsIdentity) document.CatalogId = Guid.NewGuid().ToString("N");
        if (!File.Exists(path) || needsIdentity) Save();
    }

    public MetadataDefinition? Find(int id) => document.Items.FirstOrDefault(item => item.Id == id);
    public string Label(int id, string language) => Find(id)?.Text(language).Label ?? "#" + id;
    public IEnumerable<string> Labels(IEnumerable<int>? ids, string language) => (ids ?? []).Select(id => Label(id, language));
    public bool Matches(IEnumerable<int>? ids, string search) => (ids ?? []).Any(id => Find(id)?.SearchTerms.Any(term => term.Contains(search, StringComparison.CurrentCultureIgnoreCase)) == true);
    public int Resolve(string label)
    {
        var parts = label.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var existing = document.Items.FirstOrDefault(item => item.SearchTerms.Any(term => term.Equals(label, StringComparison.OrdinalIgnoreCase) ||
            (parts.Length > 0 && term.Equals(parts[0], StringComparison.OrdinalIgnoreCase))));
        if (existing is not null) return existing.Id;
        return Edit(null, parts.FirstOrDefault() ?? label, parts.Skip(1), "");
    }

    public int Edit(int? id, string name, IEnumerable<string> aliases, string language)
    {
        name = name.Trim();
        var names = aliases.Select(value => value.Trim()).Where(value => value.Length > 0 && !value.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ValidateText(name, names);
        string before = ExportJson();
        var target = id is { } value ? Find(value) ?? throw new ArgumentException("所选编号不存在。") : null;
        if (document.Items.Any(item => item != target && item.SearchTerms.Any(term => term.Equals(name, StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("已有同名分类或标签，请选择已有选项。编辑名称不改变原编号。");
        if (target is null)
        {
            if (document.Items.Count >= 4096) throw new ArgumentException("分类或标签最多 4096 项。");
            target = new() { Id = document.Items.Select(item => item.Id).DefaultIfEmpty(0).Max() + 1, Name = name, Aliases = names };
            document.Items.Add(target);
        }
        else
        {
            target.PreviousNames = target.PreviousNames.Concat(target.SearchTerms).Distinct(StringComparer.OrdinalIgnoreCase).Take(256).ToList();
            if (language.Length == 0) { target.Name = name; target.Aliases = names; }
            else target.Languages[language] = new() { Name = name, Aliases = names };
        }
        try { Save(); }
        catch { document.Items = JsonSerializer.Deserialize<Document>(before, LibraryJson.Options)!.Items; throw; }
        return target.Id;
    }

    public bool Synchronize(PresetRecord record, bool categories, string? previousJson = null)
    {
        var previousIds = categories ? record.CategoryIds : record.TagIds;
        string previousCatalogId = categories ? record.CategoryCatalogId : record.TagCatalogId;
        if (previousIds is not null && previousCatalogId == Id) return false;
        var names = categories ? PresetMetadataService.EffectiveCategories(record) : PresetMetadataService.EffectiveTags(record);
        if (previousIds is not null && !string.IsNullOrWhiteSpace(previousJson))
        {
            var previous = JsonSerializer.Deserialize<Document>(previousJson, LibraryJson.Options)!;
            Validate(previous);
            if (previous.CatalogId == previousCatalogId)
                names = previousIds.Select(id => previous.Items.FirstOrDefault(item => item.Id == id)?.Text("").Label ?? "#" + id).ToList();
        }
        var ids = names.Select(Resolve).Distinct().ToList();
        if (categories) { record.CategoryIds = ids; record.CategoryCatalogId = Id; }
        else { record.TagIds = ids; record.TagCatalogId = Id; }
        return true;
    }

    private static void Validate(Document data)
    {
        if (data.Version != 1 || data.Items is null || data.Items.Count > 4096 || data.Items.Any(item => item.Id < 1) || data.Items.Select(item => item.Id).Distinct().Count() != data.Items.Count)
            throw new InvalidDataException("标签或分类 JSON 的版本、编号或数量无效。");
        foreach (var item in data.Items)
        {
            ValidateText(item.Name, item.Aliases);
            foreach (var text in item.Languages.Values) ValidateText(text.Name, text.Aliases);
        }
    }

    private static void ValidateText(string name, List<string> aliases)
    {
        if (string.IsNullOrWhiteSpace(name) || aliases is null || aliases.Count > 32 || new[] { name }.Concat(aliases).Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(char.IsControl)))
            throw new ArgumentException("名称为必填项，名称及别名每项最多 64 个字符，别名最多 32 项。");
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (File.Exists(path) && (savedHash is null || Fingerprints.HashFile(path) != savedHash))
                throw new IOException("分类或标签 JSON 已被外部修改，请重启后再编辑：" + path);
            File.WriteAllText(temporary, ExportJson());
            // File indexers can briefly hold a just-replaced JSON on Windows.
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, true); savedHash = Fingerprints.HashFile(path); break; }
                catch (Exception error) when (attempt < 4 && error is IOException or UnauthorizedAccessException) { Thread.Sleep(40 * (attempt + 1)); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
