using System.Security.Cryptography;
using System.Text.Json;
using VestedAI.ConnectorSdk.Credential;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

public class CredentialOpenerTests
{
    private static readonly JsonElement Fixture = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "testdata", "credential-envelope-vectors.json")))
        .RootElement;

    private static string ConnectorKey =>
        Fixture.GetProperty("connector_private_key_pkcs8_pem").GetString()!;

    private static CredentialEnvelope Envelope(JsonElement e) => new()
    {
        V = e.GetProperty("v").GetInt32(),
        Alg = e.GetProperty("alg").GetString()!,
        Kid = e.GetProperty("kid").GetString()!,
        Epk = e.GetProperty("epk").GetString()!,
        Iv = e.GetProperty("iv").GetString()!,
        Ct = e.GetProperty("ct").GetString()!,
        Aad = e.GetProperty("aad").GetString()!,
    };

    private static JsonElement Negative(string name)
    {
        foreach (var n in Fixture.GetProperty("negative").EnumerateArray())
        {
            if (n.GetProperty("name").GetString() == name) return n;
        }
        throw new InvalidOperationException($"no negative vector named {name}");
    }

    private static string FreshKeyPem()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new string(PemEncoding.Write("PRIVATE KEY", key.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void OpensEveryPositiveVector()
    {
        var opener = new CredentialOpener(ConnectorKey);

        foreach (var v in Fixture.GetProperty("vectors").EnumerateArray())
        {
            var got = opener.Open(
                Envelope(v.GetProperty("envelope")),
                v.GetProperty("connector_id").GetString()!,
                v.GetProperty("user_id").GetString()!);

            var expected = v.GetProperty("plaintext");
            var expectedCount = 0;
            foreach (var field in expected.EnumerateObject())
            {
                Assert.Equal(field.Value.GetString(), got[field.Name]);
                expectedCount++;
            }
            Assert.Equal(expectedCount, got.Count);
        }
    }

    [Fact]
    public void RejectsEnvelopeSealedForADifferentUser()
    {
        var n = Negative("aad_identity_mismatch");
        var opener = new CredentialOpener(ConnectorKey);

        var ex = Assert.Throws<CredentialException>(() => opener.Open(
            Envelope(n.GetProperty("envelope")),
            n.GetProperty("open_as_connector_id").GetString()!,
            n.GetProperty("open_as_user_id").GetString()!));

        Assert.Equal("identity_mismatch", ex.Code);
    }

    [Fact]
    public void RejectsTamperedCiphertext()
    {
        var n = Negative("tampered_ciphertext");
        var opener = new CredentialOpener(ConnectorKey);

        var ex = Assert.Throws<CredentialException>(() => opener.Open(
            Envelope(n.GetProperty("envelope")),
            n.GetProperty("open_as_connector_id").GetString()!,
            n.GetProperty("open_as_user_id").GetString()!));

        Assert.Equal("decrypt_failed", ex.Code);
    }

    [Fact]
    public void RejectsUnknownAlgorithm()
    {
        var n = Negative("unknown_algorithm");
        var opener = new CredentialOpener(ConnectorKey);

        var ex = Assert.Throws<CredentialException>(() => opener.Open(
            Envelope(n.GetProperty("envelope")),
            n.GetProperty("open_as_connector_id").GetString()!,
            n.GetProperty("open_as_user_id").GetString()!));

        Assert.Equal("unsupported_alg", ex.Code);
    }

    [Fact]
    public void KeyringTriesEveryKey()
    {
        var opener = new CredentialOpener(FreshKeyPem(), ConnectorKey);
        var v = Fixture.GetProperty("vectors")[0];

        var got = opener.Open(
            Envelope(v.GetProperty("envelope")),
            v.GetProperty("connector_id").GetString()!,
            v.GetProperty("user_id").GetString()!);

        Assert.Equal("s3cr3t", got["password"]);
    }

    [Fact]
    public void FailsWhenNoKeyInTheRingOpensTheEnvelope()
    {
        var opener = new CredentialOpener(FreshKeyPem());
        var v = Fixture.GetProperty("vectors")[0];

        var ex = Assert.Throws<CredentialException>(() => opener.Open(
            Envelope(v.GetProperty("envelope")),
            v.GetProperty("connector_id").GetString()!,
            v.GetProperty("user_id").GetString()!));

        Assert.Equal("decrypt_failed", ex.Code);
    }
}
