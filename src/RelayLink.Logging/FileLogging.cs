using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace RelayLink.Logging;

public static class FileLogging
{
    public const long FileSizeLimitBytes = 10 * 1024 * 1024;
    public const int RetentionDays = 14;

    public static Logger Create(string directory, string component, long fileSizeLimitBytes = FileSizeLimitBytes)
    {
        if (component is not ("agent" or "server")) throw new ArgumentException("Unsupported logging component.", nameof(component));
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var formatter = new JsonFormatter(renderMessage: true);
        var errorPrefix = component == "agent" ? "error" : "server-error";
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Logger(ordinary => ordinary
                .Filter.ByIncludingOnly(entry => entry.Level < LogEventLevel.Error)
                .WriteTo.File(formatter, Path.Combine(directory, $"{component}-.jsonl"),
                    rollingInterval: RollingInterval.Day, fileSizeLimitBytes: fileSizeLimitBytes,
                    rollOnFileSizeLimit: true, retainedFileCountLimit: null,
                    retainedFileTimeLimit: TimeSpan.FromDays(RetentionDays)))
            .WriteTo.Logger(errors => errors
                .Filter.ByIncludingOnly(entry => entry.Level >= LogEventLevel.Error)
                .WriteTo.File(formatter, Path.Combine(directory, $"{errorPrefix}-.jsonl"),
                    rollingInterval: RollingInterval.Day, fileSizeLimitBytes: fileSizeLimitBytes,
                    rollOnFileSizeLimit: true, retainedFileCountLimit: null,
                    retainedFileTimeLimit: null))
            .CreateLogger();
    }
}
