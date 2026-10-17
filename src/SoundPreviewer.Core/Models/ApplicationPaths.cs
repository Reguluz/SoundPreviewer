using System.Runtime.InteropServices;
using System.Text;

namespace SoundPreviewer.Core.Models;

/// <summary>One portable storage and executable layout for both desktop platforms.</summary>
public static class ApplicationPaths
{
    private static string? dataDirectory;
    public static string ApplicationDirectory => Path.GetFullPath(AppContext.BaseDirectory);
    public static string DataDirectory => dataDirectory ?? Path.Combine(ApplicationDirectory, "local-data");
    public static string WorkerPath
    {
        get
        {
            string executable = Path.Combine(ApplicationDirectory, "SoundPreviewer.Worker" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            return File.Exists(executable) ? executable : Path.Combine(ApplicationDirectory, "SoundPreviewer.Worker.dll");
        }
    }

    public static void ConfigureDataDirectory(string? directory) => dataDirectory = string.IsNullOrWhiteSpace(directory)
        ? null : Path.GetFullPath(directory, ApplicationDirectory);

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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Audio/Plug-Ins/VST3"), "/Applications"]
        : [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VstPlugins"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steinberg", "VstPlugins"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3")];
}
