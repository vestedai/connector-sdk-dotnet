using System.Reflection;
using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Credential;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Reflection;
using VestedAI.ConnectorSdk.Runtime;
using VestedAI.ConnectorSdk.Schema;
using VestedAI.ConnectorSdk.Tool;

namespace VestedAI.ConnectorSdk;

/// <summary>
/// Factory for the <see cref="ConnectorHostBuilder"/> used to construct a <see cref="ConnectorApp"/>.
///
/// Typical usage in Program.cs:
/// <code>
/// return await ConnectorHost.CreateBuilder()
///     .ScanAssembly(Assembly.GetExecutingAssembly())
///     .Build()
///     .RunFromEnvironmentAsync();
/// </code>
/// </summary>
public static class ConnectorHost
{
    /// <summary>Creates a new <see cref="ConnectorHostBuilder"/>.</summary>
    public static ConnectorHostBuilder CreateBuilder() => new();
}

/// <summary>
/// Fluent builder that scans one or more assemblies for
/// <c>[Agent]</c> / <c>[Tool]</c> / <c>[Credential]</c> / <c>[RelationalSource]</c>
/// decorated types, validates the declarations, and produces a
/// <see cref="ConnectorApp"/>.
/// </summary>
public sealed class ConnectorHostBuilder
{
    private readonly List<AgentDeclaration> _agents = new();
    private readonly Dictionary<string, ToolDeclaration> _tools =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenAgentKeys =
        new(StringComparer.Ordinal);
    private bool _insecure;
    private CredentialDeclaration? _credential;
    private IUserCredentialHandler? _credentialHandler;
    private string[]? _credentialKeys;
    private RelationalSourceDeclaration? _relationalSource;
    private IRelationalSchemaProvider? _relationalProvider;

    /// <summary>
    /// Scans <paramref name="asm"/> for <c>[Agent]</c> and <c>[Tool]</c> types and
    /// accumulates the resulting declarations.
    /// Multiple calls are allowed; duplicated tool keys across assemblies throw
    /// <see cref="ConnectorException"/>.
    /// Duplicate agent keys are silently deduped (same key, any assembly).
    /// </summary>
    public ConnectorHostBuilder ScanAssembly(Assembly asm)
    {
        var (agents, tools, credential, relationalSource) = Scanner.ScanAssembly(asm);

        if (relationalSource is not null)
        {
            if (_relationalSource is not null &&
                _relationalSource.ProviderType != relationalSource.ProviderType)
            {
                throw new ConnectorException(
                    $"Two relational sources found across assemblies: " +
                    $"{_relationalSource.ProviderType.FullName} and " +
                    $"{relationalSource.ProviderType.FullName}. " +
                    "A connector may declare only one.");
            }

            _relationalSource = relationalSource;
        }

        if (credential is not null)
        {
            if (_credential is not null && _credential.HandlerType != credential.HandlerType)
            {
                throw new ConnectorException(
                    $"Two credential handlers found across assemblies: " +
                    $"{_credential.HandlerType.FullName} and {credential.HandlerType.FullName}. " +
                    "A connector may declare only one.");
            }

            _credential = credential;
        }

        foreach (var agent in agents)
        {
            if (_seenAgentKeys.Add(agent.Key))
                _agents.Add(agent);
        }

        foreach (var (key, decl) in tools)
        {
            if (_tools.TryGetValue(key, out var existing))
            {
                // Same type registered twice (e.g. assembly loaded twice) — harmless.
                if (existing.HandlerType == decl.HandlerType)
                    continue;

                throw new ConnectorException(
                    $"Duplicate tool key \"{key}\" found across assemblies: " +
                    $"{existing.HandlerType.FullName} and {decl.HandlerType.FullName}.");
            }

            _tools[key] = decl;
        }

        return this;
    }

    /// <summary>
    /// Instructs the connector to connect to the hub over plain HTTP (no TLS).
    /// Only suitable for local development or trusted internal networks.
    /// </summary>
    public ConnectorHostBuilder UseInsecureTransport()
    {
        _insecure = true;
        return this;
    }

    /// <summary>
    /// Supplies the PKCS#8 PEM private keys that open sealed credential envelopes,
    /// newest first, instead of reading them from the environment.
    ///
    /// Most connectors omit this and let <see cref="Build"/> read
    /// <c>VESTED_CREDENTIAL_PRIVATE_KEY</c> (or
    /// <c>VESTED_CREDENTIAL_PRIVATE_KEY_FILE</c>). Use it when the keys come from
    /// a secret manager rather than the process environment.
    /// </summary>
    public ConnectorHostBuilder UseCredentialKeys(params string[] privateKeyPems)
    {
        _credentialKeys = privateKeyPems;
        return this;
    }

    /// <summary>
    /// Supplies a ready-made credential handler instead of letting the SDK
    /// construct the scanned <c>[Credential]</c> type. Use it when the handler
    /// takes constructor dependencies (an ERP client, a connection pool); the
    /// <c>[Credential]</c> attribute on its class still supplies the schema.
    /// </summary>
    /// <exception cref="ConnectorException">
    /// Thrown when a different <c>[Credential]</c> type was already scanned.
    /// </exception>
    public ConnectorHostBuilder UseCredentialHandler(IUserCredentialHandler handler)
    {
        var declared = DeclarationFactory.FromCredentialType(handler.GetType());

        if (_credential is not null && _credential.HandlerType != declared.HandlerType)
        {
            throw new ConnectorException(
                $"UseCredentialHandler supplied {declared.HandlerType.FullName} but " +
                $"{_credential.HandlerType.FullName} was already scanned. " +
                "A connector may declare only one credential handler.");
        }

        _credential = declared;
        _credentialHandler = handler;
        return this;
    }

    /// <summary>
    /// Supplies a ready-made relational schema provider instead of letting the
    /// SDK construct the scanned <c>[RelationalSource]</c> type. Use it when the
    /// provider takes constructor dependencies (a connection factory, a catalog
    /// reader — every realistic one does, including
    /// <see cref="SqlServerProvider"/>); the <c>[RelationalSource]</c> attribute
    /// on its class still supplies the declaration.
    /// </summary>
    /// <exception cref="ConnectorException">
    /// Thrown when a different <c>[RelationalSource]</c> type was already scanned.
    /// </exception>
    public ConnectorHostBuilder UseRelationalSchemaProvider(IRelationalSchemaProvider provider)
    {
        var declared = DeclarationFactory.FromRelationalSourceType(provider.GetType());

        if (_relationalSource is not null &&
            _relationalSource.ProviderType != declared.ProviderType)
        {
            throw new ConnectorException(
                $"UseRelationalSchemaProvider supplied {declared.ProviderType.FullName} but " +
                $"{_relationalSource.ProviderType.FullName} was already scanned. " +
                "A connector may declare only one relational source.");
        }

        _relationalSource = declared;
        _relationalProvider = provider;
        return this;
    }

    /// <summary>
    /// Validates the accumulated declarations and returns a <see cref="ConnectorApp"/>.
    /// </summary>
    /// <exception cref="ConnectorException">
    /// Thrown when a tool key does not start with any registered agent key followed
    /// by a dot, or when a credential handler is declared without a private key.
    /// </exception>
    public ConnectorApp Build()
    {
        var agentKeys = _agents.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        ValidateToolAgentPrefixes(agentKeys, _tools);

        // A connector that declares no credential schema stays entirely
        // unaffected: no opener, no handler, and no credential_schema on
        // Register — so the hub never gates its tools.
        IUserCredentialHandler? handler = null;
        CredentialOpener? opener = null;

        if (_credential is not null)
        {
            var keys = _credentialKeys ?? CredentialKeyring.FromEnvironment();

            // Fail at startup rather than at the first credential op: without a
            // key every check would fail later with a puzzling message.
            if (keys.Length == 0)
            {
                throw new ConnectorException(
                    $"Credential handler {_credential.HandlerType.FullName} is registered but no " +
                    $"private key was supplied. Set {CredentialKeyring.EnvVar} (or " +
                    $"{CredentialKeyring.EnvFileVar}), or call UseCredentialKeys(...).");
            }

            if (_credentialHandler is null &&
                _credential.HandlerType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new ConnectorException(
                    $"Credential handler {_credential.HandlerType.FullName} has no parameterless " +
                    "constructor. Either add one, or pass a ready-made instance to " +
                    "UseCredentialHandler(...).");
            }

            handler = _credentialHandler
                      ?? (IUserCredentialHandler)Activator.CreateInstance(_credential.HandlerType)!;
            opener = new CredentialOpener(keys);
        }

        // Same shape: a connector that declares no relational source registers
        // none, and the platform then never extracts its schema.
        IRelationalSchemaProvider? provider = null;

        if (_relationalSource is not null)
        {
            if (_relationalProvider is null &&
                _relationalSource.ProviderType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new ConnectorException(
                    $"Relational schema provider {_relationalSource.ProviderType.FullName} has no " +
                    "parameterless constructor. Either add one, or pass a ready-made instance to " +
                    "UseRelationalSchemaProvider(...).");
            }

            provider = _relationalProvider
                       ?? (IRelationalSchemaProvider)Activator.CreateInstance(_relationalSource.ProviderType)!;
        }

        return new ConnectorApp(
            _agents.AsReadOnly(),
            _tools,
            _insecure,
            _credential,
            handler,
            opener,
            _relationalSource,
            provider);
    }

    // ---------------------------------------------------------------------------
    // Internal test seam — lets tests exercise the validation logic without
    // needing a globally-visible "bad" fixture that would interfere with other tests.

    /// <summary>
    /// Runs the same tool-key/agent-key prefix validation that <see cref="Build"/> performs,
    /// but against caller-supplied declarations rather than the accumulated scanned ones.
    /// Only accessible from the test assembly via InternalsVisibleTo.
    /// </summary>
    internal static ConnectorApp BuildFromForTest(
        IReadOnlyList<AgentDeclaration> agents,
        IReadOnlyDictionary<string, ToolDeclaration> tools,
        bool insecure = false,
        CredentialDeclaration? credential = null,
        IUserCredentialHandler? credentialHandler = null,
        CredentialOpener? credentialOpener = null)
    {
        var agentKeys = agents.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        ValidateToolAgentPrefixes(agentKeys, tools);
        return new ConnectorApp(
            agents, tools, insecure, credential, credentialHandler, credentialOpener);
    }

    // ---------------------------------------------------------------------------
    // Shared validation logic

    private static void ValidateToolAgentPrefixes(
        IReadOnlySet<string> agentKeys,
        IReadOnlyDictionary<string, ToolDeclaration> tools)
    {
        foreach (var toolKey in tools.Keys)
        {
            bool hasMatchingAgent = agentKeys.Any(
                agentKey => toolKey.StartsWith(agentKey + ".", StringComparison.Ordinal));

            if (!hasMatchingAgent)
            {
                throw new ConnectorException(
                    $"tool '{toolKey}' has no matching agent " +
                    $"(key must start with '<agentKey>.')");
            }
        }
    }
}
