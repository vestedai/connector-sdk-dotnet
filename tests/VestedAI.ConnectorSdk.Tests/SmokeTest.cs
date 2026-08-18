using System.Reflection;
using Xunit;
using VestedAI.ConnectorSdk;

namespace VestedAI.ConnectorSdk.Tests;

public class SmokeTest
{
    // SdkVersion_IsExpected used to hardcode a literal ("0.7.0") here, which
    // silently went stale across three subsequent version bumps (0.8.0,
    // 0.9.0, 0.10.0) — the exact "hand-maintained value nobody re-checks"
    // failure mode. SdkInfoTests.SdkInfoVersion_MatchesTheAssemblyInformationalVersion
    // replaces it with a self-updating pin against the assembly's own build
    // metadata instead of a second copy of the literal.

    [Fact]
    public void ToolDecl_HasSensitivityProperty()
    {
        var prop = typeof(Vested.V1.ToolDecl).GetProperty("Sensitivity");
        Assert.NotNull(prop);
    }
}
