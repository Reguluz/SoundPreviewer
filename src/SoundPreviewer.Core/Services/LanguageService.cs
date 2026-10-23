using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SoundPreviewer.Core.Services;

public sealed record LanguageChoice(string Id, string Name);

public sealed class LanguageService : INotifyPropertyChanged
{
    private sealed class LanguageDocument
    {
        public string LanguageName { get; set; } = "";
        public Dictionary<string, string> Strings { get; set; } = [];
    }
    public static LanguageService Current { get; } = new();
    private Dictionary<string, string> strings = [];
    private readonly Dictionary<string, string> cache = [];
    private List<(Regex Pattern, string Text)> templates = [];
    private List<KeyValuePair<string, string>> fragments = [];
    public string Id { get; private set; } = "zh-CN";
    public int Revision { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "language");
    public IReadOnlyList<LanguageChoice> Languages(string? directory = null)
    {
        var result = new List<LanguageChoice>();
        if (!Directory.Exists(directory ?? DirectoryPath)) return [new("zh-CN", "简体中文"), new("en-US", "English")];
        foreach (var path in Directory.EnumerateFiles(directory ?? DirectoryPath, "*.json").Order())
        {
            try { var data = Read(path); result.Add(new(Path.GetFileNameWithoutExtension(path), data.LanguageName)); }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException) { System.Diagnostics.Trace.TraceWarning("Language: {0}", error.Message); }
        }
        return result;
    }
    public void Select(string id, string? directory = null)
    {
        if (!Regex.IsMatch(id, "^[a-zA-Z0-9_-]{2,32}$")) throw new ArgumentException("语言文件名称无效。");
        string path = Path.Combine(directory ?? DirectoryPath, id + ".json");
        LanguageDocument data;
        if (File.Exists(path)) data = Read(path);
        else
        {
            using var stream = typeof(LanguageService).Assembly.GetManifestResourceStream("SoundPreviewer.Core.language." + id + ".json") ?? throw new FileNotFoundException("找不到语言文件：" + path);
            data = JsonSerializer.Deserialize<LanguageDocument>(stream, Models.LibraryJson.Options)!;
        }
        strings = data.Strings;
        templates = strings.Where(pair => pair.Key != pair.Value && Regex.IsMatch(pair.Key, @"\{\d+\}"))
            .OrderByDescending(pair => Regex.Replace(pair.Key, @"\{\d+\}", "").Length).Select(pair =>
            {
                string pattern = Regex.Escape(pair.Key);
                pattern = Regex.Replace(pattern, @"\\\{(\d+)}", match => "(?<p" + match.Groups[1].Value + ">.*?)");
                // The end placeholder belongs to the rest of this message, including paths.
                if (Regex.IsMatch(pair.Key, @"\{\d+\}$")) pattern = pattern[..^4] + ".*)";
                return (new Regex(pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)), pair.Value);
            }).ToList();
        fragments = strings.Where(pair => pair.Key != pair.Value && !Regex.IsMatch(pair.Key, @"\{\d+\}")).OrderByDescending(pair => pair.Key.Length).ToList();
        Id = id;
        Revision++;
        cache.Clear();
        PropertyChanged?.Invoke(this, new(nameof(Revision)));
    }
    public string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (strings.TryGetValue(text, out var exact)) return exact;
        if (!Regex.IsMatch(text, "[\u3400-\u9fff]")) return text;
        if (cache.TryGetValue(text, out var cached)) return cached;
        string translated = text;
        var arguments = new List<string>();
        foreach (var (pattern, replacement) in templates)
            translated = pattern.Replace(translated, match => Regex.Replace(replacement, @"\{(\d+)\}", part =>
            {
                string token = "\ue000" + arguments.Count + "\ue001";
                arguments.Add(match.Groups["p" + part.Groups[1].Value].Value);
                return token;
            }));
        foreach (var fragment in fragments) translated = translated.Replace(fragment.Key, fragment.Value, StringComparison.Ordinal);
        for (int index = arguments.Count - 1; index >= 0; index--) translated = translated.Replace("\ue000" + index + "\ue001", arguments[index], StringComparison.Ordinal);
        if (cache.Count < 10000) cache[text] = translated;
        return translated;
    }
    private static LanguageDocument Read(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("Language file exceeds 2 MB: " + path);
        var data = JsonSerializer.Deserialize<LanguageDocument>(File.ReadAllText(path), Models.LibraryJson.Options);
        if (data is null || string.IsNullOrWhiteSpace(data.LanguageName) || data.Strings is null || data.Strings.Any(pair => pair.Key.Length == 0 || pair.Value is null)) throw new JsonException("Invalid language JSON: " + path);
        return data;
    }
}
