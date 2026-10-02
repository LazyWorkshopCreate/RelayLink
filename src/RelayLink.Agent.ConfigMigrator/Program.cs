using RelayLink.Agent;

try
{
    if (args is ["--config", var installedPath]) AgentConfigurationMigrator.MigrateInstalled(installedPath);
    else if (args is ["--input", var inputPath, "--output", var outputPath]) AgentConfigurationMigrator.ConvertFile(inputPath, outputPath);
    else
    {
        Console.Error.WriteLine("Usage: RelayLink.Agent.ConfigMigrator --config <agent.json> | --input <legacy.json> --output <new.json>");
        return 2;
    }
    return 0;
}
catch (Exception exception) when (exception is AgentConfigurationException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
{
    Console.Error.WriteLine($"Agent configuration migration failed: {exception.Message}");
    return 2;
}
