using System.Reflection;
using System.Text.Json.Nodes;
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
    private bool _relationalSourceWasScanned;

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
                // Same reason as in UseRelationalSchemaProvider: say where the
                // other one came from, so the operator looks in the right place.
                var origin = _relationalSourceWasScanned
                    ? "found across assemblies"
                    : "declared twice — one supplied to UseRelationalSchemaProvider, one scanned";

                throw new ConnectorException(
                    $"Two relational sources {origin}: " +
                    $"{_relationalSource.ProviderType.FullName} and " +
                    $"{relationalSource.ProviderType.FullName}. " +
                    "A connector may declare only one.");
            }

            _relationalSource = relationalSource;
            _relationalSourceWasScanned = true;
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
            // Name where the OTHER declaration came from, as the relational twin
            // below does. Saying "already scanned" when it was in fact supplied
            // by an earlier call sends the operator off to grep an assembly that
            // is perfectly fine. Only UseCredentialHandler sets
            // _credentialHandler, so its presence IS the provenance — no extra
            // flag needed.
            var origin = _credentialHandler is null
                ? "was already scanned"
                : "was already supplied to UseCredentialHandler";

            throw new ConnectorException(
                $"UseCredentialHandler supplied {declared.HandlerType.FullName} but " +
                $"{_credential.HandlerType.FullName} {origin}. " +
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
    /// reader — every realistic one does).
    /// </summary>
    /// <remarks>
    /// The instance must be of a class in your own assembly carrying
    /// <c>[RelationalSource]</c>: the declaration names your connector's tool
    /// keys, so it cannot live on an SDK-owned provider.
    /// <see cref="SqlServerProvider"/> therefore carries none — subclass it and
    /// annotate the subclass (it is not sealed):
    /// <code>
    /// [RelationalSource(Engine = "sqlserver", DescribeTool = "erp_bc.describe_schema",
    ///                   QueryTool = "erp_bc.query_sql", SqlArg = "Sql")]
    /// public sealed class BcSchemaProvider(ICatalogReader reader) : SqlServerProvider(reader);
    /// </code>
    /// </remarks>
    /// <exception cref="ConnectorException">
    /// Thrown when the supplied instance's class carries no
    /// <c>[RelationalSource]</c>, or when a different <c>[RelationalSource]</c>
    /// type was already declared.
    /// </exception>
    public ConnectorHostBuilder UseRelationalSchemaProvider(IRelationalSchemaProvider provider)
    {
        var providerType = provider.GetType();

        // Checked here rather than left to the factory so the message can name
        // the remedy. The failure is otherwise baffling: the type it points at
        // is a sealed-looking SDK class the connector author did not write and
        // cannot annotate, with no hint that the fix lives in their assembly.
        if (providerType.GetCustomAttributes(typeof(RelationalSourceAttribute), inherit: false).Length == 0)
        {
            throw new ConnectorException(
                $"UseRelationalSchemaProvider was given {providerType.FullName}, which carries no " +
                "[RelationalSource] attribute. The declaration names THIS connector's engine, tool " +
                "keys and SQL argument, so it must live on a class in your own assembly — the SDK's " +
                "own providers carry none. Subclass one and annotate the subclass, e.g. " +
                "[RelationalSource(...)] public sealed class MyProvider(ICatalogReader r) : SqlServerProvider(r);");
        }

        var declared = DeclarationFactory.FromRelationalSourceType(providerType);

        if (_relationalSource is not null &&
            _relationalSource.ProviderType != declared.ProviderType)
        {
            // Name where the OTHER declaration came from. Saying "already
            // scanned" when it was in fact supplied by an earlier call sends the
            // operator off to grep an assembly that is perfectly fine.
            var origin = _relationalSourceWasScanned
                ? "was already scanned"
                : "was already supplied to UseRelationalSchemaProvider";

            throw new ConnectorException(
                $"UseRelationalSchemaProvider supplied {declared.ProviderType.FullName} but " +
                $"{_relationalSource.ProviderType.FullName} {origin}. " +
                "A connector may declare only one relational source.");
        }

        _relationalSource = declared;
        _relationalSourceWasScanned = false;
        _relationalProvider = provider;
        return this;
    }

    /// <summary>
    /// Validates the accumulated declarations and returns a <see cref="ConnectorApp"/>.
    /// </summary>
    /// <exception cref="ConnectorException">
    /// Thrown when a tool key does not start with any registered agent key followed
    /// by a dot, when a credential handler is declared without a private key, or
    /// when a declared relational source names a tool this connector does not
    /// declare or an argument its query tool does not take.
    /// </exception>
    public ConnectorApp Build()
    {
        // Refuses an agent key this connector does not declare, "*" mixed with
        // explicit keys, and a tool that neither matches an agent namespace nor
        // names any agent. Warnings (a tool excluded from the agent its own key
        // names) go to stderr, matching the SDK's other startup diagnostics.
        ToolBinding.Validate(
            _agents, _tools,
            message => Console.Error.WriteLine($"[vested] {message}"));

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
            ValidateRelationalSourceTools(_relationalSource, _tools);

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
        // The SAME validation Build() runs. A test seam that validates
        // differently from production is a test that cannot catch production.
        ToolBinding.Validate(
            agents, tools,
            message => Console.Error.WriteLine($"[vested] {message}"));

        return new ConnectorApp(
            agents, tools, insecure, credential, credentialHandler, credentialOpener);
    }

    // ---------------------------------------------------------------------------
    // Shared validation logic

    // ValidateToolAgentPrefixes lived here and required EVERY tool key to sit
    // under some agent's namespace. ToolBinding.Validate supersedes it: that
    // rule is now correct only for a tool that names no agents, because a tool
    // that declares its own Agents list is legitimately allowed to live outside
    // all of their namespaces — which is what makes a shared `erp.shared.*`
    // tool expressible at all.

    /// <summary>
    /// Cross-checks a relational source against the tools the connector actually
    /// declares: both tool keys must exist, and <c>SqlArg</c> must name an
    /// argument of the query tool.
    /// </summary>
    /// <remarks>
    /// Nothing downstream catches these. The core validates
    /// <c>relational_source</c> for non-emptiness and a namespace prefix only
    /// (<c>ConnectorRegistryService::validateRelationalSource</c>) — it cannot
    /// know which tools exist or what arguments they take. So a one-character
    /// typo in <c>QueryTool</c>, or an <c>SqlArg</c> whose casing does not match
    /// the generated input schema, is ACCEPTED at registration: the gate then
    /// governs a tool key nothing answers to and reads an argument that is
    /// always null — authorizing an empty string — while the real query tool
    /// runs ungoverned. That is silent, and it is the failure this layer exists
    /// to prevent, so it is refused at startup instead.
    /// </remarks>
    private static void ValidateRelationalSourceTools(
        RelationalSourceDeclaration source,
        IReadOnlyDictionary<string, ToolDeclaration> tools)
    {
        if (!tools.ContainsKey(source.DescribeTool))
        {
            throw new ConnectorException(
                $"relational source declares DescribeTool '{source.DescribeTool}' " +
                $"but this connector declares no such tool " +
                $"(declared tools: {DescribeToolKeys(tools)})");
        }

        if (!tools.TryGetValue(source.QueryTool, out var queryTool))
        {
            throw new ConnectorException(
                $"relational source declares QueryTool '{source.QueryTool}' " +
                $"but this connector declares no such tool " +
                $"(declared tools: {DescribeToolKeys(tools)})");
        }

        var argNames = WireArgumentNames(queryTool);

        if (!argNames.Contains(source.SqlArg, StringComparer.Ordinal))
        {
            throw new ConnectorException(
                $"relational source declares SqlArg '{source.SqlArg}' but tool " +
                $"'{source.QueryTool}' has no such argument " +
                $"(its arguments are: {(argNames.Count == 0 ? "none" : string.Join(", ", argNames))}). " +
                "The name must match the tool's input schema exactly, including case.");
        }
    }

    private static string DescribeToolKeys(IReadOnlyDictionary<string, ToolDeclaration> tools)
        => tools.Count == 0 ? "none" : string.Join(", ", tools.Keys.OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>
    /// The argument names of a tool as they appear ON THE WIRE.
    /// </summary>
    /// <remarks>
    /// Read from the generated <c>InputSchemaJson</c>, never from the CLR
    /// properties of <c>ArgsType</c>. The two can differ — a JSON naming policy
    /// or a <c>[JsonPropertyName]</c> renames the wire field while the CLR
    /// property keeps its own name — and the wire name is the one that matters:
    /// it is what the schema advertises, what the caller sends, and therefore
    /// what the core's SQL gate looks up. Validating against CLR names would
    /// accept a declaration that reads null at gate time, which is the exact bug
    /// this check exists to catch.
    /// </remarks>
    private static IReadOnlyList<string> WireArgumentNames(ToolDeclaration tool)
    {
        if (JsonNode.Parse(tool.InputSchemaJson) is not JsonObject root)
        {
            return Array.Empty<string>();
        }

        // NJsonSchema emits an object root with inline `properties` for a POCO
        // args type (asserted by InheritedArgsSchemaTests, which covers the
        // hardest case — a flattened inheritance hierarchy). A root `$ref` into
        // `definitions` is resolved one level anyway: guessing wrong here would
        // reject a valid connector at startup.
        if (!root.ContainsKey("properties")
            && root["$ref"]?.GetValue<string>() is { } reference
            && reference.StartsWith("#/definitions/", StringComparison.Ordinal)
            && root["definitions"] is JsonObject definitions
            && definitions[reference["#/definitions/".Length..]] is JsonObject target)
        {
            root = target;
        }

        return root["properties"] is JsonObject properties
            ? properties.Select(p => p.Key).ToList()
            : Array.Empty<string>();
    }
}
