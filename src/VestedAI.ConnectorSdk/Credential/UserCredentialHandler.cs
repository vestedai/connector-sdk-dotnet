using Google.Protobuf.WellKnownTypes;
using Vested.V1;

namespace VestedAI.ConnectorSdk.Credential;

/// <summary>
/// Identity context for a credential lifecycle operation.
///
/// Deliberately carries no agent or tool key — a credential op is not scoped to
/// a tool — and no raw envelope: the SDK opens it and hands the handler
/// plaintext, so connector authors cannot skip the identity check that makes
/// per-user auth mean anything.
/// </summary>
public sealed record CredentialContext(
    string OpId,
    string UserId,
    string UserEmail,
    string EmployeeNo = "",
    string ErpIdentifier = "");

/// <summary>
/// A handler's verdict. <c>Display</c> is shown to the user, so it must contain
/// only non-secret facts — an account name or role, never the credential.
/// </summary>
public sealed record CredentialValidation(
    bool Ok,
    string Error = "",
    IReadOnlyDictionary<string, string>? Display = null)
{
    public static CredentialValidation Succeeded(IReadOnlyDictionary<string, string>? display = null) =>
        new(true, "", display);

    /// <param name="userFacingMessage">
    /// Shown verbatim to the user. Do not include the credential, a stack
    /// trace, or internal hostnames.
    /// </param>
    public static CredentialValidation Failed(string userFacingMessage) =>
        new(false, userFacingMessage);
}

/// <summary>
/// Implemented by a connector that wants per-user credentials.
///
/// The platform cannot open a sealed credential — only this worker can — so
/// every question about whether a user's credentials work is answered here.
/// <paramref name="credential"/> arrives already decrypted and already verified
/// as belonging to the calling user.
/// </summary>
public interface IUserCredentialHandler
{
    Task<CredentialValidation> ValidateAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential);

    /// <summary>Best-effort: the platform deletes its copy regardless.</summary>
    Task RevokeAsync(CredentialContext ctx, IReadOnlyDictionary<string, string> credential);
}

/// <summary>
/// Worker-side dispatcher for credential ops.
///
/// Never throws and always answers — silence would make the platform wait out
/// its full deadline for an op that was never going to complete.
/// </summary>
public sealed class CredentialOpDispatcher
{
    private readonly CredentialOpener _opener;
    private readonly IUserCredentialHandler? _handler;
    private readonly Func<string> _connectorId;

    /// <param name="connectorId">
    /// Resolved lazily, for the same reason as in <see cref="CredentialResolver"/>:
    /// the hub assigns the connector id at HelloAck, which happens after this
    /// object is constructed. Capturing it eagerly would bind the empty string
    /// and fail the AAD identity check on every envelope.
    /// </param>
    public CredentialOpDispatcher(
        CredentialOpener opener, IUserCredentialHandler? handler, Func<string> connectorId)
    {
        _opener = opener;
        _handler = handler;
        _connectorId = connectorId;
    }

    public async Task<CredentialOpResponse> DispatchAsync(CredentialOpRequest req)
    {
        var resp = new CredentialOpResponse { OpId = req.OpId, Ok = false };

        if (_handler is null)
        {
            resp.Error = "This integration does not accept per-user credentials.";
            return resp;
        }

        CredentialEnvelope envelope;
        try
        {
            envelope = System.Text.Json.JsonSerializer.Deserialize<CredentialEnvelope>(
                req.EnvelopeJson.ToStringUtf8(),
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                })!;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentNullException)
        {
            resp.Error = "The stored credential is unreadable. Please enter it again.";
            return resp;
        }

        IReadOnlyDictionary<string, string> credential;
        try
        {
            credential = _opener.Open(envelope, _connectorId(), req.UserId);
        }
        catch (CredentialException e)
        {
            // The message can name key fingerprints and internals, so it is
            // logged but never returned. An identity mismatch is a security
            // event, not a user-fixable typo.
            Console.Error.WriteLine(
                $"[vested] credential envelope could not be opened (op={req.OpId} user={req.UserId}): {e.Code}");
            resp.Error =
                "The stored credential could not be read by this integration. Please enter it again.";
            return resp;
        }

        var ctx = new CredentialContext(
            req.OpId, req.UserId, req.UserEmail, req.EmployeeNo, req.ErpIdentifier);

        try
        {
            if (req.Op == "revoke")
            {
                await _handler.RevokeAsync(ctx, credential);
                resp.Ok = true;
                return resp;
            }

            var verdict = await _handler.ValidateAsync(ctx, credential);
            resp.Ok = verdict.Ok;
            resp.Error = verdict.Error;

            if (verdict.Display is { Count: > 0 })
            {
                var s = new Struct();
                foreach (var kv in verdict.Display)
                {
                    s.Fields[kv.Key] = Value.ForString(kv.Value);
                }
                resp.Display = s;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[vested] credential handler threw (op={req.OpId}): {e.Message}");
            resp.Ok = false;
            resp.Error = "The integration could not check these credentials right now.";
        }

        return resp;
    }
}
