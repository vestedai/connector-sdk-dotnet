using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VestedAI.ConnectorSdk.Credential;

/// <summary>Sealed user-credential envelope.</summary>
public sealed class CredentialEnvelope
{
    public int V { get; init; } = 1;
    public string Alg { get; init; } = "";
    public string Kid { get; init; } = "";
    public string Epk { get; init; } = "";
    public string Iv { get; init; } = "";
    public string Ct { get; init; } = "";
    public string Aad { get; init; } = "";
}

/// <summary>
/// Opens sealed user-credential envelopes.
///
/// The core seals with this connector's public key and cannot open what it
/// stored; this class is the only place the plaintext exists. Connector authors
/// never touch it directly — the SDK calls it from the tool context, which is
/// what makes the identity check impossible to skip by accident.
///
/// Format: ECDH-P256 -&gt; HKDF-SHA256 -&gt; AES-256-GCM. See
/// docs/superpowers/specs/2026-08-10-connector-user-auth-design.md.
/// </summary>
public sealed class CredentialOpener
{
    public const string Alg = "ECDH-P256+HKDF-SHA256+A256GCM";

    private static readonly byte[] Info = Encoding.UTF8.GetBytes("vested-connector-credential-v1");
    private static readonly byte[] Salt = new byte[32];
    private const int TagBytes = 16;

    private readonly string[] _keyring;

    /// <param name="privateKeyPems">PKCS#8 PEM private keys, newest first.</param>
    public CredentialOpener(params string[] privateKeyPems) => _keyring = privateKeyPems;

    public IReadOnlyDictionary<string, string> Open(
        CredentialEnvelope envelope, string connectorId, string userId)
    {
        if (envelope.Alg != Alg)
        {
            throw CredentialException.UnsupportedAlg(envelope.Alg);
        }

        // Verify the binding BEFORE decrypting. GCM enforces the AAD anyway, but
        // checking here turns a generic decrypt failure into a specific,
        // alertable security signal.
        var expected = $"connector:{connectorId}|user:{userId}|v{envelope.V}";
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(envelope.Aad)))
        {
            throw CredentialException.IdentityMismatch(expected, envelope.Aad);
        }

        byte[] raw, iv, aad, epkDer;
        try
        {
            epkDer = Convert.FromBase64String(envelope.Epk);
            iv = Convert.FromBase64String(envelope.Iv);
            raw = Convert.FromBase64String(envelope.Ct);
            aad = Encoding.UTF8.GetBytes(envelope.Aad);
        }
        catch (FormatException e)
        {
            throw CredentialException.DecryptFailed($"credential envelope is malformed: {e.Message}");
        }

        if (raw.Length <= TagBytes)
        {
            throw CredentialException.DecryptFailed("credential envelope ciphertext is too short");
        }

        using var ephemeral = ECDiffieHellman.Create();
        try
        {
            ephemeral.ImportSubjectPublicKeyInfo(epkDer, out _);
        }
        catch (CryptographicException e)
        {
            throw CredentialException.DecryptFailed($"ephemeral public key is not importable: {e.Message}");
        }

        var body = raw.AsSpan(0, raw.Length - TagBytes).ToArray();
        var tag = raw.AsSpan(raw.Length - TagBytes).ToArray();

        foreach (var pem in _keyring)
        {
            var plaintext = new byte[body.Length];
            try
            {
                using var priv = ECDiffieHellman.Create();
                priv.ImportFromPem(pem);

                // DeriveRawSecretAgreement, NOT DeriveKeyFromHash — the latter
                // applies its own KDF and would not interoperate with the other
                // four implementations.
                var z = priv.DeriveRawSecretAgreement(ephemeral.PublicKey);
                var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, z, 32, Salt, Info);

                using var gcm = new AesGcm(key, TagBytes);
                gcm.Decrypt(iv, body, tag, plaintext, aad);
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException)
            {
                continue; // wrong key in the ring, or authentication failed
            }

            var decoded = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)
                          ?? throw CredentialException.DecryptFailed("credential payload is not an object");

            return decoded;
        }

        throw CredentialException.DecryptFailed(
            "credential envelope failed to decrypt or authenticate under any key in the ring");
    }
}
