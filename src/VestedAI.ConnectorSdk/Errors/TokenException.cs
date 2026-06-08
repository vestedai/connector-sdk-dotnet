namespace VestedAI.ConnectorSdk.Errors;

/// <summary>
/// Thrown when the connector token is rejected by the hub
/// (RegisterAck status == "rejected", or GoAway reason == "revoked"/"token_revoked").
/// The supervisor exits with code 78 when this is raised.
/// </summary>
public class TokenException : ConnectorException
{
    public TokenException(string message) : base(message) { }
    public TokenException(string message, Exception inner) : base(message, inner) { }
}
