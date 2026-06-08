using System.Reflection;
using Xunit;
using VestedAI.ConnectorSdk;

namespace VestedAI.ConnectorSdk.Tests;

public class SmokeTest
{
    [Fact]
    public void SdkVersion_IsExpected()
    {
        Assert.Equal("0.1.0", SdkInfo.Version);
    }

    [Fact]
    public void ToolDecl_HasSensitivityProperty()
    {
        var prop = typeof(Vested.V1.ToolDecl).GetProperty("Sensitivity");
        Assert.NotNull(prop);
    }
}
