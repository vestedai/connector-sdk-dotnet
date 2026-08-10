namespace VestedAI.ConnectorSdk.Credential;

/// <summary>
/// No sealed credential was forwarded for this tool call.
///
/// Defensive: when a connector declares a credential schema the platform gates
/// dispatch, so a gated tool should never run without one. Reaching this means
/// either the connector declares no schema (and the tool should not be asking)
/// or the gate is misconfigured — both worth failing loudly rather than
/// silently proceeding without an identity.
/// </summary>
public sealed class CredentialUnavailableException : Exception
{
    public string Code => "credential_unavailable";

    public CredentialUnavailableException(string message) : base(message) { }
}

/// <summary>
/// Lazily opens the caller's sealed credential for one tool invocation.
///
/// Lazy because most tools never read the credential, and one that doesn't ask
/// should neither pay for an ECDH key agreement nor fail because of one.
/// Memoized because a tool may read it more than once.
/// </summary>
public sealed class CredentialResolver
{
    private readonly CredentialOpener? _opener;
    private readonly byte[]? _envelopeJson;
    private readonly Func<string> _connectorId;
    private readonly string _userId;
    private IReadOnlyDictionary<string, string>? _opened;

    /// <param name="connectorId">
    /// Resolved lazily: the hub assigns the connector id at HelloAck, which
    /// happens after this object is constructed. Capturing it eagerly would
    /// bind the empty string and fail every AAD identity check.
    /// </param>
    public CredentialResolver(
        CredentialOpener? opener,
        byte[]? envelopeJson,
        Func<string> connectorId,
        string userId)
    {
        _opener = opener;
        _envelopeJson = envelopeJson;
        _connectorId = connectorId;
        _userId = userId;
    }

    public bool HasCredential() => _opener is not null && _envelopeJson is { Length: > 0 };

    public IReadOnlyDictionary<string, string> Credential()
    {
        if (_opened is not null) return _opened;

        if (!HasCredential())
        {
            throw new CredentialUnavailableException(
                "No user credential was supplied for this tool call. Either this connector " +
                "declares no credential schema, or the platform refused the call before dispatch.");
        }

        CredentialEnvelope envelope;
        try
        {
            envelope = System.Text.Json.JsonSerializer.Deserialize<CredentialEnvelope>(
                _envelopeJson!,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentNullException)
        {
            throw CredentialException.DecryptFailed("The forwarded credential envelope is malformed.");
        }

        // The AAD identity check happens inside Open(). Deliberately not
        // duplicated here: one implementation, on the only path a connector
        // author can reach.
        _opened = _opener!.Open(envelope, _connectorId(), _userId);
        return _opened;
    }
}
