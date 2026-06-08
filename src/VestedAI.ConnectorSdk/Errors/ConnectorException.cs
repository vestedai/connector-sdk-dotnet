namespace VestedAI.ConnectorSdk.Errors;

/// <summary>Base exception for all Vested AI Connector SDK errors.</summary>
public class ConnectorException : Exception
{
    public ConnectorException(string message) : base(message) { }
    public ConnectorException(string message, Exception inner) : base(message, inner) { }
}
