using RelayLink.Agent;

var parsed = ParseArguments(args);
AgentProcessConfiguration configuration;
try
{
    configuration = AgentProcessConfigurationLoader.Load(parsed.ConfigurationPath);
}
catch (AgentConfigurationException exception)
{
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

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(console => console.IncludeScopes = true);
builder.Services.AddSystemd();
builder.Services.AddWindowsService(options => options.ServiceName = "RelayLink Agent");
builder.Services.AddSingleton(configuration);
builder.Services.AddSingleton(new AgentConfigurationPath(parsed.ConfigurationPath));
builder.Services.AddSingleton<AgentLocalWriteSession>();
builder.Services.AddSingleton<AgentProcessRuntime>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<AgentProcessRuntime>());
builder.Services.AddHostedService<AgentLocalDashboard>();
await builder.Build().RunAsync();
return 0;

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
