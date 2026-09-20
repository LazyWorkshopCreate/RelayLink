using RelayLink.Agent;

var parsed = ParseArguments(args);
AgentConfiguration configuration;
try
{
    configuration = AgentConfigurationLoader.Load(parsed.ConfigurationPath);
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
    using var identity = AgentIdentity.LoadOrCreate(configuration.E2eIdentityPath, configuration.ClientId);
    Console.WriteLine(identity.Fingerprint);
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(console => console.IncludeScopes = true);
builder.Services.AddSystemd();
builder.Services.AddWindowsService(options => options.ServiceName = "RelayLink Agent");
builder.Services.AddSingleton(configuration);
builder.Services.AddSingleton<AgentStatus>();
builder.Services.AddSingleton<AgentDiagnosticLog>();
builder.Services.AddHostedService<ControlSessionWorker>();
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
    return (path!, check, showFingerprint);
}
