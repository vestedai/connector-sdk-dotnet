using System.Reflection;
using VestedAI.ConnectorSdk;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

/// <summary>
/// Pins <see cref="SdkInfo.Version"/> — the literal <c>Runtime.Daemon</c>
/// sends to the hub as <c>SdkVersion</c> on every <c>Hello</c> — to the
/// version the assembly actually shipped with, so the two cannot silently
/// drift apart again (they did: the constant sat at "0.7.0" while the csproj
/// moved through 0.8.0/0.9.0/0.10.0).
/// </summary>
public class SdkInfoTests
{
    [Fact]
    public void SdkInfoVersion_MatchesTheAssemblyInformationalVersion()
    {
        var attr = typeof(SdkInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.NotNull(attr);

        // The csproj's <Version> flows into AssemblyInformationalVersionAttribute
        // at build time, with a "+<git-sha>" build-metadata suffix appended
        // when building inside a git checkout (SourceRevisionId). Strip that
        // suffix before comparing — SdkInfo.Version is the bare semantic
        // version, not the sha-qualified one.
        var informational = attr!.InformationalVersion.Split('+')[0];

        Assert.Equal(SdkInfo.Version, informational);
    }
}
