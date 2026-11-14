namespace Encore.Tests;

/// <summary>
/// Tests that change process-wide environment variables belong to this collection.
/// xUnit runs a collection with DisableParallelization on its own, after the parallel ones,
/// so a host booted by another test (HealthTests via WebApplicationFactory) never reads a
/// half-written environment. Program exits with code 2 on a bad config, which shows up as
/// "The entry point exited without ever building an IHost".
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
