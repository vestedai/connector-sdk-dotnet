namespace VestedAI.ConnectorSdk.Credential;

/// <summary>
/// Raised when a sealed credential envelope cannot be opened, or is not ours to open.
/// </summary>
public sealed class CredentialException : Exception
{
    public string Code { get; }

    public CredentialException(string code, string message) : base(message) => Code = code;

    /// <remarks>
    /// Deliberately carries no decrypted payload. This is a security event: an
    /// envelope sealed for one identity arrived on a call made by another.
    /// </remarks>
    public static CredentialException IdentityMismatch(string expected, string actual) =>
        new("identity_mismatch",
            $"credential envelope identity mismatch: envelope is bound to '{actual}', invocation is '{expected}'");

    public static CredentialException DecryptFailed(string detail) => new("decrypt_failed", detail);

    public static CredentialException UnsupportedAlg(string alg) =>
        new("unsupported_alg", $"unsupported credential envelope algorithm '{alg}'");
}
