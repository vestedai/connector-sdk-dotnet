using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Vested.V1;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Tool;

namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// Routes ToolCallRequest frames to registered ToolHandlers.
///
/// Each <see cref="Dispatch"/> call spawns a Task and returns immediately —
/// the daemon's read loop is never blocked by a slow handler invocation.
///
/// Error shape (matching Node / Python SDKs — NO synthetic code prefix):
///   "unknown tool: {key}"
///   "tool_call_invalid_args: {msg}"
///   or the raw handler exception message.
/// </summary>
internal sealed class Dispatcher
{
    private readonly IReadOnlyDictionary<string, ToolDeclaration> _tools;
    private readonly Func<ConnectorMsg, Task> _send;
    private readonly ILogger? _logger;
    private readonly VestedAI.ConnectorSdk.Credential.CredentialOpener? _credentialOpener;
    private readonly Func<string> _connectorId;

    /// <param name="tools">All registered tool declarations keyed by tool key.</param>
    /// <param name="send">
    /// Callback that sends a <see cref="ConnectorMsg"/> to the hub.
    /// Typically <c>client.SendAsync</c>.
    /// </param>
    /// <param name="logger">Optional logger; pass null to suppress runtime output.</param>
    public Dispatcher(
        IReadOnlyDictionary<string, ToolDeclaration> tools,
        Func<ConnectorMsg, Task> send,
        ILogger? logger = null,
        // Null for connectors that declare no credential schema.
        VestedAI.ConnectorSdk.Credential.CredentialOpener? credentialOpener = null,
        // Lazy: the hub assigns the connector id at HelloAck, after construction.
        Func<string>? connectorId = null)
    {
        _tools = tools;
        _send = send;
        _logger = logger;
        _credentialOpener = credentialOpener;
        _connectorId = connectorId ?? (() => "");
    }

    /// <summary>
    /// Fire-and-forget: spawns a Task for the invocation, returns immediately.
    /// </summary>
    public void Dispatch(ToolCallRequest req)
    {
        // Intentionally not awaited — each tool call runs concurrently.
        _ = HandleAsync(req).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger?.LogError(
                    t.Exception,
                    "[vested] dispatcher: unhandled error for invocation {InvocationId}",
                    req.InvocationId);
                Console.Error.WriteLine(
                    $"[vested] dispatcher: unhandled error for invocation " +
                    $"{req.InvocationId}: {t.Exception?.GetBaseException().Message}");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task HandleAsync(ToolCallRequest req)
    {
        // 1. Tool lookup
        if (!_tools.TryGetValue(req.ToolKey, out var decl))
        {
            await ReplyErrorAsync(req.InvocationId, $"unknown tool: {req.ToolKey}")
                .ConfigureAwait(false);
            return;
        }

        // 2. Args parse + validation
        object args;
        try
        {
            args = ArgsValidation.Parse(decl, req.ArgsJson.Span);
        }
        catch (ToolValidationException ex)
        {
            await ReplyErrorAsync(req.InvocationId, $"tool_call_invalid_args: {ex.Message}")
                .ConfigureAwait(false);
            return;
        }

        // 3. Build ToolContext from wire fields
        var ctx = BuildContext(req);

        // 4. Invoke handler
        try
        {
            if (decl.IsPaginated)
            {
                var cursor = new DatasetCursor
                {
                    Token    = string.IsNullOrEmpty(req.Cursor) ? null : req.Cursor,
                    PageSize = (int)req.PageSize,
                };
                var handler = (ToolHandlerBase)Activator.CreateInstance(decl.HandlerType)!;
                var page    = await handler.InvokePagedBoxedAsync(args, cursor, ctx).ConfigureAwait(false);
                var bytes   = JsonSerializer.SerializeToUtf8Bytes(new { rows = page.Rows });
                await ReplyPageAsync(req.InvocationId, bytes, page.NextCursor, page.Total).ConfigureAwait(false);
                return;
            }

            var result = await decl.InvokeAsync(args, ctx).ConfigureAwait(false);
            var resultBytes = JsonSerializer.SerializeToUtf8Bytes(result, result.GetType());
            await ReplyOkAsync(req.InvocationId, resultBytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ReplyErrorAsync(req.InvocationId, ex.Message).ConfigureAwait(false);
        }
    }

    private ToolContext BuildContext(ToolCallRequest req)
    {
        // organization_id is a string on the wire; coerce to int (0 when missing/unparseable).
        int orgId = int.TryParse(req.OrganizationId, out var n) ? n : 0;
        int userId = int.TryParse(req.UserId, out var uid) ? uid : 0;

        return new ToolContext(
            OrgId:          orgId,
            AgentKey:       req.AgentKey ?? "",
            RunId:          "",       // run_id not present in ToolCallRequest proto
            ConversationId: req.ConversationId ?? "",
            UserEmail:      req.UserEmail ?? "",
            UserId:         userId)
        {
            EmployeeNo                = req.EmployeeNo ?? "",
            ErpIdentifier             = req.ErpIdentifier ?? "",
            ErpDepartmentIdentifiers  = req.ErpDepartmentIdentifiers.ToArray(),
            SchemaContext             = ToSchemaContext(req.SchemaContext),
            // Lazy: most tools never read the credential, and one that doesn't
            // ask should neither pay for a decrypt nor fail because of one.
            Credentials = new VestedAI.ConnectorSdk.Credential.CredentialResolver(
                _credentialOpener,
                req.CredentialEnvelopeJson.IsEmpty ? null : req.CredentialEnvelopeJson.ToByteArray(),
                _connectorId,
                req.UserId ?? ""),
        };
    }

    /// <summary>
    /// Maps the wire message to the SDK's read-only record, mapping an ABSENT
    /// proto message to null — never to a <see cref="Tool.SchemaContext"/> with
    /// an empty table list. Protobuf3 singular message fields are already null
    /// when unset on the wire, so this is a direct translation, not a guess.
    /// </summary>
    private static Tool.SchemaContext? ToSchemaContext(Vested.V1.SchemaContext? proto)
    {
        if (proto is null)
        {
            return null;
        }

        return new Tool.SchemaContext(
            Tables: proto.Tables
                .Select(t => new Tool.SchemaContextTable(
                    t.LogicalName ?? "", t.Scope ?? "", t.Kind ?? "", t.Physical.ToArray()))
                .ToArray(),
            HasStar: proto.HasStar,
            GateMode: proto.GateMode ?? "");
    }

    private Task ReplyOkAsync(string invocationId, byte[] resultJson)
    {
        var msg = new ConnectorMsg
        {
            ToolCallResponse = new ToolCallResponse
            {
                InvocationId = invocationId,
                ResultJson    = ByteString.CopyFrom(resultJson),
                DurationMs    = 0,
            }
        };
        return _send(msg);
    }

    private Task ReplyPageAsync(string invocationId, byte[] rowsJson, string? nextCursor, long? total)
    {
        var msg = new ConnectorMsg
        {
            ToolCallResponse = new ToolCallResponse
            {
                InvocationId = invocationId,
                ResultJson   = ByteString.CopyFrom(rowsJson),
                NextCursor   = nextCursor ?? "",
                TotalRows    = total.HasValue ? (ulong)total.Value : 0UL,
                DurationMs   = 0,
            }
        };
        return _send(msg);
    }

    private Task ReplyErrorAsync(string invocationId, string error)
    {
        var msg = new ConnectorMsg
        {
            ToolCallResponse = new ToolCallResponse
            {
                InvocationId = invocationId,
                Error        = error,
                DurationMs   = 0,
            }
        };
        return _send(msg);
    }
}
