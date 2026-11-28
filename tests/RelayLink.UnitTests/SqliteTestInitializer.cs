using System.Runtime.CompilerServices;
using RelayLink.Server.Configuration;

namespace RelayLink.UnitTests;

internal static class SqliteTestInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(ConfigurationLoader).Module.ModuleHandle);
    }
}
