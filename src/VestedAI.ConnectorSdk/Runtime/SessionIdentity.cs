namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// Per-session holder for the connector id the hub assigns at HelloAck.
///
/// The <see cref="Dispatcher"/> and the credential op dispatcher are both
/// constructed before the handshake runs, but both need the connector id to
/// verify the identity binding on a sealed credential envelope. They take a
/// <c>Func&lt;string&gt;</c> reading this holder, which <see cref="Daemon"/>
/// fills in as soon as HelloAck arrives.
///
/// One instance per session: a reconnect gets a fresh holder, so a stale id can
/// never outlive the stream it belonged to.
/// </summary>
internal sealed class SessionIdentity
{
    /// <summary>
    /// The hub-assigned connector id, or empty string before HelloAck.
    /// Written once by the Daemon, read by the credential paths.
    /// </summary>
    public string ConnectorId { get; set; } = "";
}
