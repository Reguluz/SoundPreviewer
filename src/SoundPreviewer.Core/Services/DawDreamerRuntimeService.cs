using System.Text.Json;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Services;

public sealed record DawDreamerRuntime(string RootDirectory, string PythonPath, string ManifestPath, string Platform,
    IReadOnlyDictionary<string, string> Dependencies, IReadOnlyDictionary<string, string> Files)
{
    public bool SupportsSerum2 => Dependencies.GetValueOrDefault("serum2-preset-loader") == "0.1.3" &&
        Dependencies.GetValueOrDefault("cbor2") == "5.6.5" && Dependencies.GetValueOrDefault("zstandard") == "0.23.0";
}

public static class DawDreamerRuntimeService
{
    public const string ManifestName = "runtime.json";
    public const string RuntimeKind = "uv-managed-cpython";
    public const string PythonVersion = "3.12.15";
    public const string UvVersion = "0.13.4";
    private const string DirectoryPrefix = "dawdreamer-portable-";

    public static string GetRuntimeDirectory(string dataDirectory, PluginPlatform? platform = null, PluginArchitecture? architecture = null)
    {
        string rid = (platform ?? PluginSelectionService.CurrentPlatform) == PluginPlatform.Windows ? "win-x64" :
            (architecture ?? PluginSelectionService.CurrentArchitecture) == PluginArchitecture.Arm64 ? "osx-arm64" : "osx-x64";
        return Path.Combine(Path.GetFullPath(dataDirectory), "renderers", DirectoryPrefix + rid);
    }

    public static bool TryGetLocalRuntime(string dataDirectory, out DawDreamerRuntime? runtime,
        PluginPlatform? platform = null, PluginArchitecture? architecture = null)
    {
        runtime = null;
        var target = platform ?? PluginSelectionService.CurrentPlatform;
        var cpu = architecture ?? PluginSelectionService.CurrentArchitecture;
        if (target == PluginPlatform.Windows && cpu != PluginArchitecture.X64 ||
            target == PluginPlatform.MacOS && cpu is not (PluginArchitecture.X64 or PluginArchitecture.Arm64)) return false;
        string directory = GetRuntimeDirectory(dataDirectory, target, cpu);
        return TryGetRuntime(Path.Combine(directory, target == PluginPlatform.Windows ? "python.exe" : "bin/python3.12"), out runtime);
    }

    public static bool IsManagedExecutablePath(string pythonPath) => TryGetLayout(pythonPath, out _, out _, out _);

    public static bool TryGetRuntime(string pythonPath, out DawDreamerRuntime? runtime)
    {
        runtime = null;
        try
        {
            if (!TryGetLayout(pythonPath, out string root, out string rid, out string relativePython)) return false;
            string manifestPath = Path.Combine(root, ManifestName);
            if (!IsRegularFile(root, ManifestName) || new FileInfo(manifestPath).Length > 1024 * 1024) return false;
            var manifest = JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(manifestPath), LibraryJson.Options);
            if (manifest is null || manifest.SchemaVersion != 1 || manifest.RuntimeKind != RuntimeKind ||
                manifest.Platform != rid || manifest.PythonExecutable != relativePython || manifest.PythonVersion != PythonVersion ||
                manifest.UvVersion != UvVersion || manifest.Dependencies is null || manifest.Dependencies.Count > 32 ||
                manifest.Dependencies.GetValueOrDefault("dawdreamer") != "0.8.3" || manifest.Dependencies.GetValueOrDefault("numpy") != "1.26.4" ||
                manifest.Files is null || manifest.Files.Count is < 1 or > 64 || !manifest.Files.ContainsKey(relativePython)) return false;
            foreach (var file in manifest.Files)
                if (file.Value is null || file.Value.Length != 64 || !file.Value.All(char.IsAsciiHexDigit) || !IsRegularFile(root, file.Key)) return false;
            runtime = new(root, Path.GetFullPath(pythonPath), manifestPath, rid, manifest.Dependencies, manifest.Files);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        { return false; }
    }

    public static string Fingerprint(DawDreamerRuntime runtime, CancellationToken cancellationToken = default) =>
        Fingerprints.HashText(JsonSerializer.Serialize(new
        {
            Manifest = Fingerprints.HashFile(runtime.ManifestPath, cancellationToken),
            Files = runtime.Files.Keys.Order(StringComparer.Ordinal).Select(relative => new
            {
                Path = relative,
                Hash = Fingerprints.HashFile(Path.Combine(runtime.RootDirectory, relative), cancellationToken)
            }).ToArray()
        }));

    private static bool TryGetLayout(string pythonPath, out string root, out string rid, out string relativePython)
    {
        root = rid = relativePython = "";
        try
        {
            string absolute = Path.GetFullPath(pythonPath);
            string name = Path.GetFileName(absolute);
            string? parent = Path.GetDirectoryName(absolute);
            if (name.Equals("python.exe", StringComparison.OrdinalIgnoreCase)) { root = parent!; relativePython = "python.exe"; }
            else if (name == "python3.12" && Path.GetFileName(parent) == "bin")
            { root = Path.GetDirectoryName(parent!)!; relativePython = "bin/python3.12"; }
            else return false;
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(root)), "renderers", StringComparison.OrdinalIgnoreCase)) return false;
            string directory = Path.GetFileName(root);
            if (!directory.StartsWith(DirectoryPrefix, StringComparison.Ordinal)) return false;
            rid = directory[DirectoryPrefix.Length..];
            return relativePython == "python.exe" ? rid == "win-x64" : rid is "osx-arm64" or "osx-x64";
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static bool IsRegularFile(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':')) return false;
        string[] parts = relative.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or "..")) return false;
        string current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        foreach (string part in parts)
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return File.Exists(current);
    }

    private sealed class RuntimeManifest
    {
        public RuntimeManifest() { }
        public int SchemaVersion { get; set; }
        public string RuntimeKind { get; set; } = "";
        public string Platform { get; set; } = "";
        public string PythonExecutable { get; set; } = "";
        public string PythonVersion { get; set; } = "";
        public string UvVersion { get; set; } = "";
        public Dictionary<string, string>? Dependencies { get; set; }
        public Dictionary<string, string>? Files { get; set; }
    }
}
