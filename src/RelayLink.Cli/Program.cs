using RelayLink.Cli;

// Redirected streams are consumed by scripts and must not inherit a console code page.
using var redirectedOutput = Console.IsOutputRedirected ? CreateUtf8Writer(Console.OpenStandardOutput()) : null;
using var redirectedError = Console.IsErrorRedirected ? CreateUtf8Writer(Console.OpenStandardError()) : null;
var output = redirectedOutput ?? Console.Out;
var error = redirectedError ?? Console.Error;

if (!CliOptions.TryParse(args, out var options, out var parseError))
{
    error.WriteLine(parseError);
    return ExitCodes.Usage;
}

using var cancellation = new CancellationTokenSource(
    options.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromMinutes(5));

var app = new CliApp(options, output, error);
try
{
    return await app.RunAsync(args, cancellation.Token);
}
catch (OperationCanceledException)
{
    error.WriteLine("操作超时或被取消。");
    return ExitCodes.NetworkOrPin;
}
catch (HttpRequestException exception)
{
    error.WriteLine($"网络错误：{exception.Message}");
    return ExitCodes.NetworkOrPin;
}

static StreamWriter CreateUtf8Writer(Stream stream) => new(stream) { AutoFlush = true };
