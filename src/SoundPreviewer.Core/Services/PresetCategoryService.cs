using System.Text;
using System.Text.Json;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Services;

public sealed record CategoryRulesLoadResult(bool Success, string Error, int RuleCount, bool UsedPreviousRules);
public sealed record CategoryApplyResult(bool Success, string Error, int Processed, int Changed);

public sealed class PresetCategoryService
{
    public const int MaximumRulesBytes = 256 * 1024;
    private const int MaximumCategories = 64;
    private const int MaximumTerms = 128;
    private const int MaximumTextLength = 64;
    private CompiledCategory[]? rules;

    public static string DefaultRulesPath => Path.Combine(AppContext.BaseDirectory, "Config", "preset-categories.json");
    public static string BundledRulesPath => Path.Combine(AppContext.BaseDirectory, "Config", "preset-categories.default.json");
    public string RulesPath { get; }
    public string DefaultFilePath { get; }

    public PresetCategoryService(string? rulesPath = null, string? bundledRulesPath = null)
    {
        RulesPath = Path.GetFullPath(rulesPath ?? DefaultRulesPath);
        DefaultFilePath = Path.GetFullPath(bundledRulesPath ?? BundledRulesPath);
    }

    public void EnsureRulesFile()
    {
        if (File.Exists(RulesPath)) return;
        string json = ReadBounded(DefaultFilePath);
        Parse(json);
        Directory.CreateDirectory(Path.GetDirectoryName(RulesPath)!);
        FileStream stream;
        try
        {
            stream = new FileStream(RulesPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(RulesPath))
        {
            // A concurrently created user file is never replaced by bundled defaults.
            return;
        }
        using (stream)
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json);
        }
    }

    public CategoryRulesLoadResult Load()
    {
        try
        {
            var loaded = Parse(ReadBounded(RulesPath));
            Volatile.Write(ref rules, loaded);
            return new(true, "", loaded.Length, false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        {
            var previous = Volatile.Read(ref rules);
            return new(false, "分类规则加载失败，已有分类保持不变：" + error.Message, previous?.Length ?? 0, previous is not null);
        }
    }

    public static CategoryRulesLoadResult Validate(string json)
    {
        try { return new(true, "", Parse(json).Length, false); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or JsonException)
        { return new(false, "分类规则无效：" + error.Message, 0, false); }
    }

    public List<string> Classify(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Volatile.Read(ref rules) ?? throw new InvalidOperationException("尚未成功加载分类规则，已有分类未变更。");
        return Classify(name ?? "", snapshot, cancellationToken);
    }

    public CategoryApplyResult Apply(IEnumerable<PresetRecord> presets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presets);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Volatile.Read(ref rules);
        if (snapshot is null) return new(false, "尚未成功加载分类规则，已有分类未变更。", 0, 0);
        var updates = new List<(PresetRecord Record, List<string> Categories)>();
        int processed = 0;
        foreach (var record in presets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(record);
            var categories = Classify(record.Name ?? "", snapshot, cancellationToken);
            if (record.CategoryTags is null || !record.CategoryTags.SequenceEqual(categories, StringComparer.Ordinal))
                updates.Add((record, categories));
            processed++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Finish analysis before changing records so invalid input or cancellation cannot apply half a ruleset.
        foreach (var update in updates) update.Record.CategoryTags = update.Categories;
        return new(true, "", processed, updates.Count);
    }

    public CategoryApplyResult ReloadAndApply(IEnumerable<PresetRecord> presets, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = Load();
        return loaded.Success ? Apply(presets, cancellationToken) : new(false, loaded.Error, 0, 0);
    }

    private static List<string> Classify(string name, CompiledCategory[] snapshot, CancellationToken token)
    {
        var result = new List<string>();
        foreach (var category in snapshot)
        {
            token.ThrowIfCancellationRequested();
            if (category.Tokens.Any(term => MatchesToken(name, term)) || category.Prefixes.Any(term => MatchesPrefix(name, term)))
                result.Add(category.Label);
        }
        return result;
    }

    private static bool MatchesToken(string name, string term)
    {
        int position = 0;
        bool chinese = term.Any(IsCjk);
        while ((position = name.IndexOf(term, position, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = position + term.Length;
            if (chinese || ((position == 0 || !IsWordPart(name[position - 1])) && (end == name.Length || !IsWordPart(name[end])))) return true;
            position++;
        }
        return false;
    }

    private static bool MatchesPrefix(string name, string term)
    {
        int start = 0;
        while (start < name.Length && (char.IsWhiteSpace(name[start]) || (char.IsLetterOrDigit(term[0]) && !char.IsLetterOrDigit(name[start])))) start++;
        if (!name.AsSpan(start).StartsWith(term, StringComparison.OrdinalIgnoreCase)) return false;
        int end = start + term.Length;
        return end == name.Length || !IsWordPart(term[^1]) || !IsWordPart(name[end]) || char.IsDigit(name[end]);
    }

    private static bool IsWordPart(char value) => char.IsLetterOrDigit(value) && !IsCjk(value);
    private static bool IsCjk(char value) => value is >= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';

    private static CompiledCategory[] Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumRulesBytes) throw new InvalidDataException("规则文件超过 256 KiB 限制。");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        CheckProperties(root, "根对象", ["version", "categories"]);
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1)
            throw new InvalidDataException("version 必须为 1。");
        if (!root.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array || categories.GetArrayLength() is < 1 or > MaximumCategories)
            throw new InvalidDataException("categories 必须包含 1 到 64 个分类。");
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CompiledCategory>();
        foreach (var category in categories.EnumerateArray())
        {
            CheckProperties(category, "分类", ["label", "tokens", "prefixes"]);
            if (!category.TryGetProperty("label", out var labelNode)) throw new InvalidDataException("分类缺少 label。");
            string label = ReadText(labelNode, "label");
            if (!labels.Add(label)) throw new InvalidDataException("分类 label 重复：" + label);
            var tokens = ReadTerms(category, "tokens");
            var prefixes = ReadTerms(category, "prefixes");
            if (tokens.Length + prefixes.Length == 0) throw new InvalidDataException("分类必须至少包含一个 token 或 prefix：" + label);
            result.Add(new(label, tokens, prefixes));
        }
        return result.ToArray();
    }

    private static void CheckProperties(JsonElement element, string description, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException(description + "必须为 JSON 对象。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw new InvalidDataException(description + "包含不支持的字段：" + property.Name + "；仅支持 token / prefix，不支持正则表达式。");
            if (!names.Add(property.Name)) throw new InvalidDataException(description + "包含重复字段：" + property.Name);
        }
    }

    private static string[] ReadTerms(JsonElement category, string property)
    {
        if (!category.TryGetProperty(property, out var terms)) return [];
        if (terms.ValueKind != JsonValueKind.Array || terms.GetArrayLength() > MaximumTerms)
            throw new InvalidDataException(property + "必须是最多 128 项的字符串数组。");
        return terms.EnumerateArray().Select(term => ReadText(term, property)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ReadText(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException(property + "必须为字符串。");
        string value = element.GetString()!.Trim();
        if (value.Length is < 1 or > MaximumTextLength || value.Any(char.IsControl))
            throw new InvalidDataException(property + "须为 1 到 64 字符且不能包含控制字符。");
        return value;
    }

    private static string ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumRulesBytes) throw new InvalidDataException("规则文件超过 256 KiB 限制。");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private sealed record CompiledCategory(string Label, string[] Tokens, string[] Prefixes);
}
