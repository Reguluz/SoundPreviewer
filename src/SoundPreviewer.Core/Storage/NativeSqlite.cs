using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;

namespace SoundPreviewer.Core.Storage;

internal sealed class NativeSqlite : IDisposable
{
    private const int Row = 100;
    private const int Done = 101;
    private IntPtr database;

    public NativeSqlite(string path)
    {
        int result = Native.sqlite3_open_v2(Utf8(path), out database, 0x00000002 | 0x00000004 | 0x00010000, IntPtr.Zero);
        if (result != 0)
        {
            string message = ErrorMessage();
            Dispose();
            throw new IOException($"SQLite database could not be opened: {message}");
        }
        Check(Native.sqlite3_busy_timeout(database, 5000));
    }

    public void Execute(string sql, params object?[] parameters)
    {
        using var statement = Prepare(sql, parameters);
        int result;
        do { result = Native.sqlite3_step(statement.Handle); } while (result == Row);
        if (result != Done) Check(result);
    }

    public List<string> QueryStrings(string sql, params object?[] parameters)
    {
        using var statement = Prepare(sql, parameters);
        var result = new List<string>();
        int status;
        while ((status = Native.sqlite3_step(statement.Handle)) == Row)
        {
            IntPtr pointer = Native.sqlite3_column_text(statement.Handle, 0);
            int length = Native.sqlite3_column_bytes(statement.Handle, 0);
            result.Add(pointer == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(pointer, length) ?? "");
        }
        if (status != Done) Check(status);
        return result;
    }

    private Statement Prepare(string sql, object?[] parameters)
    {
        Check(Native.sqlite3_prepare_v2(database, Utf8(sql), -1, out IntPtr handle, out _));
        var statement = new Statement(handle);
        try
        {
            if (Native.sqlite3_bind_parameter_count(handle) != parameters.Length)
                throw new ArgumentException("SQLite parameter count does not match the statement.");
            for (int i = 0; i < parameters.Length; i++)
            {
                int index = i + 1;
                int result = parameters[i] switch
                {
                    null => Native.sqlite3_bind_null(handle, index),
                    int number => Native.sqlite3_bind_int64(handle, index, number),
                    long number => Native.sqlite3_bind_int64(handle, index, number),
                    bool flag => Native.sqlite3_bind_int64(handle, index, flag ? 1 : 0),
                    double number => Native.sqlite3_bind_double(handle, index, number),
                    string value => BindText(handle, index, value),
                    _ => throw new ArgumentException($"Unsupported SQLite parameter type: {parameters[i]!.GetType().Name}")
                };
                Check(result);
            }
            return statement;
        }
        catch
        {
            statement.Dispose();
            throw;
        }
    }

    private static int BindText(IntPtr statement, int index, string text)
    {
        byte[] bytes = Utf8(text);
        return Native.sqlite3_bind_text(statement, index, bytes, bytes.Length - 1, new IntPtr(-1));
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + '\0');

    private void Check(int result)
    {
        if (result != 0) throw new IOException($"SQLite operation failed ({result}): {ErrorMessage()}");
    }

    private string ErrorMessage() => database == IntPtr.Zero ? "No database handle." : Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(database)) ?? "Unknown error.";

    public void Dispose()
    {
        if (database == IntPtr.Zero) return;
        Native.sqlite3_close_v2(database);
        database = IntPtr.Zero;
    }

    private sealed class Statement(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public void Dispose() => Native.sqlite3_finalize(Handle);
    }

    private static class Native
    {
        private const string Library = "SoundPreviewer.Sqlite";

        static Native() => NativeLibrary.SetDllImportResolver(typeof(NativeSqlite).Assembly, ResolveLibrary);

        private static IntPtr ResolveLibrary(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (name != Library) return IntPtr.Zero;
            string platformLibrary = OperatingSystem.IsWindows() ? "winsqlite3.dll" :
                OperatingSystem.IsMacOS() ? "/usr/lib/libsqlite3.dylib" : "libsqlite3.so.0";
            return NativeLibrary.Load(platformLibrary, assembly, searchPath);
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, out IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_parameter_count(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_double(IntPtr statement, int index, double value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes(IntPtr statement, int index);
    }
}
