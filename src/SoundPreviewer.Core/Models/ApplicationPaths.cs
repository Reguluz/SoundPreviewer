using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SoundPreviewer.Core.Models;

/// <summary>Application files are replaceable; user libraries keep a stable location across upgrades.</summary>
public static class ApplicationPaths
{
    private static string? dataDirectory;
    public static string ApplicationDirectory => Path.GetFullPath(AppContext.BaseDirectory);
    public static string LegacyDataDirectory => Path.Combine(ApplicationDirectory, "local-data");
    public static string UserDataDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "SoundPreviewer")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundPreviewer");
    public static string DataDirectory => dataDirectory ??= ResolveDefaultDataDirectory(ApplicationDirectory, UserDataDirectory);
    public static string WorkerPath
    {
        get
        {
            string executable = Path.Combine(ApplicationDirectory, "SoundPreviewer.Worker" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            return File.Exists(executable) ? executable : Path.Combine(ApplicationDirectory, "SoundPreviewer.Worker.dll");
        }
    }

    public static void ConfigureDataDirectory(string? directory, string? applicationDirectory = null, string? userDataDirectory = null)
    {
        string application = applicationDirectory ?? ApplicationDirectory;
        // Resolve once for this process: opening another library may update the shared locator.
        dataDirectory = string.IsNullOrWhiteSpace(directory)
            ? ResolveDefaultDataDirectory(application, userDataDirectory ?? UserDataDirectory)
            : Path.GetFullPath(directory, application);
    }

    public static string ResolveDefaultDataDirectory(string applicationDirectory, string userDataDirectory)
    {
        string locationPath = Path.Combine(Path.GetFullPath(userDataDirectory), "library-location.json");
        if (File.Exists(locationPath))
        {
            if (new FileInfo(locationPath).Length > 16 * 1024) throw new InvalidDataException("音色库位置文件过大。请重新选择已有音色库。");
            var location = JsonSerializer.Deserialize<LibraryLocation>(File.ReadAllText(locationPath));
            if (location is null || location.FormatVersion != 1 || string.IsNullOrWhiteSpace(location.DataDirectory) || !Path.IsPathFullyQualified(location.DataDirectory))
                throw new InvalidDataException("音色库位置文件版本或路径无效。请重新选择已有音色库。");
            string directory = Path.GetFullPath(location.DataDirectory);
            if (!File.Exists(Path.Combine(directory, "library.db")))
                throw new DirectoryNotFoundException($"已记住的音色库无法找到：{directory}。请连接原磁盘，或使用“打开已有音色库”重新选择；现有记录未更改。");
            return directory;
        }
        string legacyDirectory = Path.Combine(Path.GetFullPath(applicationDirectory), "local-data");
        return File.Exists(Path.Combine(legacyDirectory, "library.db")) ? legacyDirectory : Path.GetFullPath(userDataDirectory);
    }

    public static void RememberDataDirectory(string directory, string? userDataDirectory = null)
    {
        string fullPath = Path.GetFullPath(directory, ApplicationDirectory);
        if (!File.Exists(Path.Combine(fullPath, "library.db"))) throw new DirectoryNotFoundException("只能记住已经成功打开的音色库目录。");
        string userDirectory = Path.GetFullPath(userDataDirectory ?? UserDataDirectory);
        Directory.CreateDirectory(userDirectory);
        string locationPath = Path.Combine(userDirectory, "library-location.json");
        string temporary = locationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new LibraryLocation(1, fullPath));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, locationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record LibraryLocation(int FormatVersion, string DataDirectory);

    // Windows keeps its historical case-insensitive IDs. Unix volumes may be case-sensitive.
    public static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static string PathIdentity(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) return fullPath.ToUpperInvariant();
        if (!OperatingSystem.IsMacOS()) return fullPath;
        // APFS can be either case-sensitive or case-insensitive. Ask the kernel for the
        // existing entry's spelling instead of folding distinct files into one identity.
        var bytes = new byte[4096];
        if (RealPath(fullPath, bytes) == IntPtr.Zero) return fullPath;
        int end = Array.IndexOf(bytes, (byte)0);
        return end > 0 ? Encoding.UTF8.GetString(bytes, 0, end) : fullPath;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "realpath", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] resolved);

    public static bool SamePath(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return string.Equals(PathIdentity(first), PathIdentity(second), StringComparison.Ordinal); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    public static bool IsWithin(string path, string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string candidate = Path.GetFullPath(path, ApplicationDirectory);
        return candidate.Equals(root, PathComparison) || candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    public static IReadOnlyList<string> DefaultPluginDirectories => OperatingSystem.IsMacOS()
        ? ["/Library/Audio/Plug-Ins/VST", "/Library/Audio/Plug-Ins/VST3",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Audio/Plug-Ins/VST"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Audio/Plug-Ins/VST3"),
            "/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications")]
        : [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VstPlugins"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steinberg", "VstPlugins"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3")];
}
