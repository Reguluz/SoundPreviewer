using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Storage;

/// <summary>Persistent preview references are relative to the library's Preview root, never the working directory.</summary>
public static class PreviewPathStorage
{
    public static string Store(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string resolved = Resolve(path, root);
        // Historical external references are input for InstalledStorageService's safe import.
        // Do not discard them before it has preserved/copied the original file.
        if (!Path.IsPathFullyQualified(resolved) || !ApplicationPaths.IsWithin(resolved, root)) return resolved;
        string relative = Path.GetRelativePath(root, resolved).Replace('\\', '/');
        Resolve(relative, root); // Validate before committing a path that would be refused on the next load.
        return relative;
    }

    public static string Resolve(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (IsAbsolute(path)) return path;
        // Treat both separators consistently when a portable library crosses platforms.
        string relative = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("预览相对路径无效：" + path);
        string resolved = Path.GetFullPath(relative, Path.GetFullPath(root));
        if (!ApplicationPaths.IsWithin(resolved, root))
            throw new InvalidDataException("预览相对路径不能指向 Preview 以外：" + path);
        EnsureNoLinks(resolved, root);
        return resolved;
    }

    public static bool IsAbsolute(string path) => Path.IsPathFullyQualified(path) || path.StartsWith('/') ||
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') ||
        path.StartsWith("\\\\", StringComparison.Ordinal);

    private static void EnsureNoLinks(string path, string root)
    {
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (string? current = path; current is not null && ApplicationPaths.IsWithin(current, boundary); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("预览路径不能通过链接指向其他目录：" + current);
            if (ApplicationPaths.SamePath(current, boundary)) break;
        }
    }
}
