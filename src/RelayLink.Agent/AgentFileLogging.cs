using RelayLink.Logging;
using Serilog.Core;

namespace RelayLink.Agent;

internal static class AgentFileLogging
{
    internal const long FileSizeLimitBytes = FileLogging.FileSizeLimitBytes;
    internal const int RetentionDays = FileLogging.RetentionDays;

    internal static Logger Create(string configurationPath, long fileSizeLimitBytes = FileSizeLimitBytes)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configurationPath))!, "logs");
        return FileLogging.Create(directory, "agent", fileSizeLimitBytes);
    }
}
