using VestedAI.ConnectorSdk;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

// ---------------------------------------------------------------------------
// xUnit collection: env-mutation (no-parallelism guard for env-var tests)
// ---------------------------------------------------------------------------

[CollectionDefinition("env-mutation", DisableParallelization = true)]
public class EnvMutationCollection { }

// ---------------------------------------------------------------------------
// ConnectorApp environment-variable tests (K-5)
// ---------------------------------------------------------------------------
// These tests mutate process-wide environment variables so they MUST NOT run
// in parallel.  The [Collection] attribute places them in a dedicated,
// single-threaded collection.
// ---------------------------------------------------------------------------

[Collection("env-mutation")]
public class ConnectorAppEnvTests
{
    private const string TokenVar = "VESTED_CONNECTOR_TOKEN";
    private const string HubVar   = "VESTED_CONNECTOR_HUB";

    // Build a minimal ConnectorApp that has no agents/tools so we can call
    // RunFromEnvironmentAsync() and test the early-exit paths without
    // requiring real network connectivity.
    private static ConnectorApp BuildEmptyApp()
        => ConnectorHostBuilder.BuildFromForTest(
            new List<VestedAI.ConnectorSdk.Agent.AgentDeclaration>(),
            new Dictionary<string, VestedAI.ConnectorSdk.Tool.ToolDeclaration>());

    // -----------------------------------------------------------------------
    // Both variables unset → 78

    [Fact]
    public async Task BothUnset_Returns78()
    {
        var savedToken = Environment.GetEnvironmentVariable(TokenVar);
        var savedHub   = Environment.GetEnvironmentVariable(HubVar);
        try
        {
            Environment.SetEnvironmentVariable(TokenVar, null);
            Environment.SetEnvironmentVariable(HubVar, null);

            var app    = BuildEmptyApp();
            var result = await app.RunFromEnvironmentAsync();

            Assert.Equal(78, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVar, savedToken);
            Environment.SetEnvironmentVariable(HubVar, savedHub);
        }
    }

    // -----------------------------------------------------------------------
    // Token set, hub unset → 78

    [Fact]
    public async Task TokenSet_HubUnset_Returns78()
    {
        var savedToken = Environment.GetEnvironmentVariable(TokenVar);
        var savedHub   = Environment.GetEnvironmentVariable(HubVar);
        try
        {
            Environment.SetEnvironmentVariable(TokenVar, "test-token-value");
            Environment.SetEnvironmentVariable(HubVar, null);

            var app    = BuildEmptyApp();
            var result = await app.RunFromEnvironmentAsync();

            Assert.Equal(78, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVar, savedToken);
            Environment.SetEnvironmentVariable(HubVar, savedHub);
        }
    }

    // -----------------------------------------------------------------------
    // Token unset, hub set → 78 (still missing token)

    [Fact]
    public async Task TokenUnset_HubSet_Returns78()
    {
        var savedToken = Environment.GetEnvironmentVariable(TokenVar);
        var savedHub   = Environment.GetEnvironmentVariable(HubVar);
        try
        {
            Environment.SetEnvironmentVariable(TokenVar, null);
            Environment.SetEnvironmentVariable(HubVar, "hub.example.com:4443");

            var app    = BuildEmptyApp();
            var result = await app.RunFromEnvironmentAsync();

            Assert.Equal(78, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVar, savedToken);
            Environment.SetEnvironmentVariable(HubVar, savedHub);
        }
    }

    // -----------------------------------------------------------------------
    // Empty-string token → 78 (treated as "not set" by IsNullOrEmpty)

    [Fact]
    public async Task EmptyToken_Returns78()
    {
        var savedToken = Environment.GetEnvironmentVariable(TokenVar);
        var savedHub   = Environment.GetEnvironmentVariable(HubVar);
        try
        {
            Environment.SetEnvironmentVariable(TokenVar, "");
            Environment.SetEnvironmentVariable(HubVar, "hub.example.com:4443");

            var app    = BuildEmptyApp();
            var result = await app.RunFromEnvironmentAsync();

            Assert.Equal(78, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVar, savedToken);
            Environment.SetEnvironmentVariable(HubVar, savedHub);
        }
    }

    // -----------------------------------------------------------------------
    // Empty-string hub → 78 (treated as "not set" by IsNullOrEmpty)

    [Fact]
    public async Task EmptyHub_Returns78()
    {
        var savedToken = Environment.GetEnvironmentVariable(TokenVar);
        var savedHub   = Environment.GetEnvironmentVariable(HubVar);
        try
        {
            Environment.SetEnvironmentVariable(TokenVar, "some-token");
            Environment.SetEnvironmentVariable(HubVar, "");

            var app    = BuildEmptyApp();
            var result = await app.RunFromEnvironmentAsync();

            Assert.Equal(78, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVar, savedToken);
            Environment.SetEnvironmentVariable(HubVar, savedHub);
        }
    }
}
