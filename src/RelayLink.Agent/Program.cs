using RelayLink.Agent;
using Serilog;

var parsed = ParseArguments(args);
Serilog.Debugging.SelfLog.Enable(message => Console.Error.WriteLine(message));
// Installer validation commands must not create files before service ACLs are set.
using var logger = parsed.CheckOnly || parsed.ShowE2eFingerprint ? null : AgentFileLogging.Create(parsed.ConfigurationPath);
AgentProcessConfiguration configuration;
try
{
    configuration = AgentProcessConfigurationLoader.Load(parsed.ConfigurationPath);
}
catch (AgentConfigurationException exception)
{
    logger?.Error("Agent configuration validation failed: {Reason}", exception.Message);
    Console.Error.WriteLine($"Agent configuration validation failed: {exception.Message}");
    return 2;
}

if (parsed.CheckOnly)
{
    Console.WriteLine("Agent configuration validation succeeded.");
    return 0;
}
if (parsed.ShowE2eFingerprint)
{
    if (configuration.Servers.Count != 1)
    {
        Console.Error.WriteLine("--show-e2e-fingerprint requires exactly one server profile.");
        return 2;
    }
    var server = AgentProcessConfigurationLoader.CreateRuntimeConfiguration(configuration, configuration.Servers[0], parsed.ConfigurationPath);
    var stateDirectory = Path.GetDirectoryName(server.E2eIdentityPath)!;
    Directory.CreateDirectory(stateDirectory);
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(Path.GetDirectoryName(stateDirectory)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    using var identity = AgentIdentity.LoadOrCreate(server.E2eIdentityPath, server.ClientId);
    Console.WriteLine(identity.Fingerprint);
    return 0;
}

try
{
    logger!.Information("Agent starting.");
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSystemd();
    builder.Services.AddWindowsService(options => options.ServiceName = "RelayLink Agent");
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(logger, dispose: false);
    builder.Services.AddSingleton(configuration);
    builder.Services.AddSingleton(new AgentConfigurationPath(parsed.ConfigurationPath));
    builder.Services.AddSingleton<AgentLocalWriteSession>();
    builder.Services.AddSingleton<AgentProcessRuntime>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<AgentProcessRuntime>());
    builder.Services.AddHostedService<AgentLocalDashboard>();
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception exception)
{
    logger!.Fatal(exception, "Agent host terminated unexpectedly.");
    return 1;
}

static (string ConfigurationPath, bool CheckOnly, bool ShowE2eFingerprint) ParseArguments(string[] arguments)
{
    string? path = null;
    var check = false;
    var showFingerprint = false;
    for (var index = 0; index < arguments.Length; index++)
    {
        if (arguments[index] == "--config" && index + 1 < arguments.Length) path = arguments[++index];
        else if (arguments[index] == "--check-config") check = true;
        else if (arguments[index] == "--show-e2e-fingerprint") showFingerprint = true;
        else { Console.Error.WriteLine("Usage: RelayLink.Agent --config <agent.json> [--check-config | --show-e2e-fingerprint]"); Environment.Exit(2); }
    }

    if (string.IsNullOrWhiteSpace(path)) { Console.Error.WriteLine("--config is required."); Environment.Exit(2); }
    if ((check ? 1 : 0) + (showFingerprint ? 1 : 0) > 1)
    {
        Console.Error.WriteLine("Select only one Agent command.");
        Environment.Exit(2);
    }
    return (path!, check, showFingerprint);
}
