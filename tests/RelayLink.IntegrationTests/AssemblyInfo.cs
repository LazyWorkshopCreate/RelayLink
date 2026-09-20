using Xunit;

// These tests start real Server/Agent processes and bind several ephemeral
// ports per fixture. Running fixtures concurrently makes slower CI hosts race
// on startup and obscures failures with shared process/port pressure.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
