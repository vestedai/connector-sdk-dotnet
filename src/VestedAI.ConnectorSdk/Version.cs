namespace VestedAI.ConnectorSdk;

/// <summary>
/// SDK identity sent to the hub on every <c>Hello</c> (<see cref="Runtime.Daemon"/>).
/// </summary>
/// <remarks>
/// <see cref="Version"/> is a hand-maintained literal, not read from the
/// assembly at runtime — it must be bumped in lockstep with
/// <c>VestedAI.ConnectorSdk.csproj</c>'s <c>&lt;Version&gt;</c>, or the hub's
/// record of which SDK version a connector runs silently drifts.
/// <c>SdkInfoTests.SdkInfoVersion_MatchesTheAssemblyInformationalVersion</c>
/// pins the two together so that drift fails the build instead of shipping.
/// </remarks>
public static class SdkInfo
{
    public const string Version = "0.10.0";
}
