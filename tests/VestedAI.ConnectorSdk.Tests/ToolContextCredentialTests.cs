using System.Text;
using System.Text.Json;
using VestedAI.ConnectorSdk.Credential;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

public class ToolContextCredentialTests
{
    private static readonly JsonElement Fixture = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "testdata", "credential-envelope-vectors.json")))
        .RootElement;

    private static JsonElement Vector => Fixture.GetProperty("vectors")[0];

    private static CredentialResolver ResolverFor(Func<string> connectorId, string userId) =>
        new(new CredentialOpener(Fixture.GetProperty("connector_private_key_pkcs8_pem").GetString()!),
            Encoding.UTF8.GetBytes(Vector.GetProperty("envelope").GetRawText()),
            connectorId,
            userId);

    [Fact]
    public void HandsATheToolDecryptedCredential()
    {
        var r = ResolverFor(
            () => Vector.GetProperty("connector_id").GetString()!,
            Vector.GetProperty("user_id").GetString()!);

        Assert.True(r.HasCredential());
        Assert.Equal("s3cr3t", r.Credential()["password"]);
    }

    [Fact]
    public void MemoizesSoTwoReadsCostOneKeyAgreement()
    {
        var r = ResolverFor(
            () => Vector.GetProperty("connector_id").GetString()!,
            Vector.GetProperty("user_id").GetString()!);

        Assert.Same(r.Credential(), r.Credential());
    }

    [Fact]
    public void RefusesAnEnvelopeSealedForADifferentUser()
    {
        // The check lives in CredentialOpener, on the only path a tool author
        // can reach — a tool cannot opt out of it.
        var r = ResolverFor(() => Vector.GetProperty("connector_id").GetString()!, "999999");

        var ex = Assert.Throws<CredentialException>(() => r.Credential());
        Assert.Equal("identity_mismatch", ex.Code);
    }

    [Fact]
    public void ReportsNoCredentialRatherThanThrowing()
    {
        var r = new CredentialResolver(null, null, () => "42", "1337");

        Assert.False(r.HasCredential());
    }

    [Fact]
    public void ThrowsNamedErrorWhenAskedForOneNeverSent()
    {
        var r = new CredentialResolver(null, null, () => "42", "1337");

        Assert.Throws<CredentialUnavailableException>(() => r.Credential());
    }

    [Fact]
    public void ResolvesTheConnectorIdLazilySinceItArrivesAtHelloAck()
    {
        var id = "";
        var r = ResolverFor(() => id, Vector.GetProperty("user_id").GetString()!);

        // Constructed before the handshake; the id lands afterwards.
        id = Vector.GetProperty("connector_id").GetString()!;

        Assert.Equal("s3cr3t", r.Credential()["password"]);
    }
}
