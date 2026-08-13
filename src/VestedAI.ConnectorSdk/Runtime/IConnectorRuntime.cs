using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Credential;
using VestedAI.ConnectorSdk.Schema;
using VestedAI.ConnectorSdk.Tool;

namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// Minimal contract the Daemon and Supervisor need from the app.
/// ConnectorApp (K-5) implements this interface.
/// </summary>
internal interface IConnectorRuntime
{
    IReadOnlyList<AgentDeclaration> Agents { get; }
    IReadOnlyDictionary<string, ToolDeclaration> Tools { get; }

    /// <summary>
    /// The declared credential form, or null when this connector uses no
    /// per-user auth. Null keeps <c>Register.credential_schema</c> absent, which
    /// is what tells the platform never to gate this connector's tools.
    /// </summary>
    CredentialDeclaration? CredentialSchema { get; }

    /// <summary>The connector's credential handler, or null when none is declared.</summary>
    IUserCredentialHandler? CredentialHandler { get; }

    /// <summary>
    /// Opens sealed envelopes with the connector's private-key ring. Null when
    /// no credential schema is declared.
    /// </summary>
    CredentialOpener? CredentialOpener { get; }

    /// <summary>
    /// The declared relational source, or null when this connector fronts no
    /// database. Null keeps <c>Register.relational_source</c> absent, which is
    /// what tells the platform never to extract a schema for this connector.
    /// </summary>
    RelationalSourceDeclaration? RelationalSource { get; }

    /// <summary>
    /// The connector's schema provider, or null when none is declared. The
    /// catalog fingerprint reported on <c>Register</c> is read from this
    /// instance at register time, never captured earlier.
    /// </summary>
    IRelationalSchemaProvider? RelationalSchemaProvider { get; }
}
