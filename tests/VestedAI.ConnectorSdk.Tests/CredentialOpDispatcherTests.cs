using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Vested.V1;
using VestedAI.ConnectorSdk.Credential;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

public class CredentialOpDispatcherTests
{
    private static readonly JsonElement Fixture = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "testdata", "credential-envelope-vectors.json")))
        .RootElement;

    private static JsonElement Vector => Fixture.GetProperty("vectors")[0];

    private static string ConnectorKey =>
        Fixture.GetProperty("connector_private_key_pkcs8_pem").GetString()!;

    private static CredentialOpRequest Request(string userId, string op = "validate") =>
        new()
        {
            OpId = "op-1",
            Op = op,
            UserId = userId,
            UserEmail = "j.smith@example.com",
            EnvelopeJson = ByteString.CopyFrom(
                Encoding.UTF8.GetBytes(Vector.GetProperty("envelope").GetRawText())),
            DeadlineMs = 5000,
        };

    private static CredentialOpDispatcher Dispatcher(IUserCredentialHandler? handler) =>
        new(new CredentialOpener(ConnectorKey), handler,
            Vector.GetProperty("connector_id").GetString()!);

    private sealed class SpyHandler : IUserCredentialHandler
    {
        private readonly CredentialValidation? _verdict;
        public IReadOnlyDictionary<string, string>? SawCredential { get; private set; }
        public CredentialContext? SawCtx { get; private set; }
        public int RevokeCalls { get; private set; }

        public SpyHandler(CredentialValidation? verdict = null) => _verdict = verdict;

        public Task<CredentialValidation> ValidateAsync(
            CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        {
            SawCredential = credential;
            SawCtx = ctx;
            return Task.FromResult(_verdict ?? CredentialValidation.Succeeded(
                new Dictionary<string, string> { ["account"] = "j.smith@erp" }));
        }

        public Task RevokeAsync(
            CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        {
            RevokeCalls++;
            SawCredential = credential;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IUserCredentialHandler
    {
        public Task<CredentialValidation> ValidateAsync(
            CredentialContext ctx, IReadOnlyDictionary<string, string> credential) =>
            throw new InvalidOperationException(
                "ERP host db-prod-07.internal refused: connection reset");

        public Task RevokeAsync(
            CredentialContext ctx, IReadOnlyDictionary<string, string> credential) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task OpensTheEnvelopeAndHandsTheHandlerPlaintext()
    {
        var handler = new SpyHandler();
        var resp = await Dispatcher(handler).DispatchAsync(
            Request(Vector.GetProperty("user_id").GetString()!));

        Assert.True(resp.Ok);
        Assert.NotNull(handler.SawCredential);
        Assert.Equal("s3cr3t", handler.SawCredential!["password"]);
        Assert.Equal("j.smith@erp", resp.Display.Fields["account"].StringValue);
    }

    [Fact]
    public async Task SurfacesAHandlerRefusal()
    {
        var resp = await Dispatcher(
                new SpyHandler(CredentialValidation.Failed("ERP rejected those credentials.")))
            .DispatchAsync(Request(Vector.GetProperty("user_id").GetString()!));

        Assert.False(resp.Ok);
        Assert.Equal("ERP rejected those credentials.", resp.Error);
    }

    [Fact]
    public async Task RefusesEnvelopeForADifferentUserWithoutCallingTheHandler()
    {
        var handler = new SpyHandler();
        var resp = await Dispatcher(handler).DispatchAsync(Request("999999"));

        Assert.False(resp.Ok);
        Assert.Null(handler.SawCredential);
    }

    [Fact]
    public async Task NeverLeaksHandlerExceptionText()
    {
        var resp = await Dispatcher(new ThrowingHandler()).DispatchAsync(
            Request(Vector.GetProperty("user_id").GetString()!));

        Assert.False(resp.Ok);
        Assert.DoesNotContain("db-prod-07.internal", resp.Error);
    }

    [Fact]
    public async Task RunsRevokeWhenAsked()
    {
        var handler = new SpyHandler();
        var resp = await Dispatcher(handler).DispatchAsync(
            Request(Vector.GetProperty("user_id").GetString()!, "revoke"));

        Assert.True(resp.Ok);
        Assert.Equal(1, handler.RevokeCalls);
    }

    [Fact]
    public async Task AnswersRatherThanStayingSilentWithoutAHandler()
    {
        var resp = await Dispatcher(null).DispatchAsync(
            Request(Vector.GetProperty("user_id").GetString()!));

        Assert.False(resp.Ok);
        Assert.Equal("op-1", resp.OpId);
    }
}
