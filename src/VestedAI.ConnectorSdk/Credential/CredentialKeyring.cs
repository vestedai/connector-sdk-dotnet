using System.Text.RegularExpressions;
using VestedAI.ConnectorSdk.Errors;

namespace VestedAI.ConnectorSdk.Credential;

/// <summary>
/// Loads the connector's private-key ring from the environment.
///
/// An operator can rotate the connector's keypair. Envelopes sealed under the
/// old key stop being readable, so to ride out the overlap both keys live in
/// the ring — newest first, separated by a blank line — and
/// <see cref="CredentialOpener"/> tries each in turn.
/// </summary>
public static class CredentialKeyring
{
    /// <summary>Environment variable holding one or more PKCS#8 PEM private keys.</summary>
    public const string EnvVar = "VESTED_CREDENTIAL_PRIVATE_KEY";

    /// <summary>Environment variable holding a path to a file of the same content.</summary>
    public const string EnvFileVar = "VESTED_CREDENTIAL_PRIVATE_KEY_FILE";

    // A blank line (optionally carrying whitespace) separates keys. PEM bodies
    // never contain one, so this cannot split a key in half.
    private static readonly Regex Separator = new(@"\r?\n[ \t]*\r?\n", RegexOptions.Compiled);

    /// <summary>
    /// Read the key ring from <see cref="EnvVar"/>, falling back to the file named
    /// by <see cref="EnvFileVar"/>. Returns an empty array when neither is set.
    /// </summary>
    /// <exception cref="ConnectorException">
    /// Thrown when <see cref="EnvFileVar"/> names a file that cannot be read.
    /// </exception>
    public static string[] FromEnvironment()
    {
        var inline = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(inline))
            return Parse(inline);

        var path = Environment.GetEnvironmentVariable(EnvFileVar);
        if (string.IsNullOrWhiteSpace(path))
            return Array.Empty<string>();

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConnectorException(
                $"{EnvFileVar} points at \"{path}\", which could not be read: {e.Message}");
        }
    }

    /// <summary>
    /// Split raw key-ring text into individual PEMs, newest first, dropping
    /// blank entries.
    /// </summary>
    public static string[] Parse(string raw) =>
        Separator.Split(raw.Trim())
                 .Select(k => k.Trim())
                 .Where(k => k.Length > 0)
                 .ToArray();
}
