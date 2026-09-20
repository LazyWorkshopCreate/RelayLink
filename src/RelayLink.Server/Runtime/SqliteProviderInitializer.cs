using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SQLitePCL;

namespace RelayLink.Server.Runtime;

internal static class SqliteProviderInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (OperatingSystem.IsLinux())
        {
            var providerAssembly = typeof(SQLite3Provider_sqlite3).Assembly;
            NativeLibrary.SetDllImportResolver(providerAssembly, ResolveSystemSqlite);
            raw.SetProvider(new SQLite3Provider_sqlite3());
        }
        else
        {
            raw.SetProvider(new SQLite3Provider_e_sqlite3());
        }

        raw.FreezeProvider();
    }

    private static nint ResolveSystemSqlite(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "sqlite3" && NativeLibrary.TryLoad("libsqlite3.so.0", assembly, searchPath, out var handle))
            return handle;
        return nint.Zero;
    }
}
