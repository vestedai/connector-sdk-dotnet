namespace VestedAI.ConnectorSdk.Schema;

/// <summary>
/// Marks the class implementing <see cref="IRelationalSchemaProvider"/> as this
/// connector's relational source, and declares how the platform reaches it.
///
/// Declaring one is what makes the connector's database visible to schema
/// extraction: a connector with no <c>[RelationalSource]</c> class registers no
/// <c>relational_source</c>, so the core never extracts its schema and — in a
/// later slice — never governs its SQL. That is the same "declare nothing, stay
/// untouched" contract as <c>[Credential]</c>.
///
/// Apply once per assembly.
/// </summary>
/// <remarks>
/// <para>
/// This attribute must sit on a class in YOUR connector's assembly. Everything
/// it declares — the engine, the two tool keys, the SQL argument name — is
/// specific to your connector, so it cannot live on an SDK-owned provider:
/// <see cref="SqlServerProvider"/> deliberately carries no
/// <c>[RelationalSource]</c> of its own, and handing a bare
/// <c>new SqlServerProvider(...)</c> to
/// <c>ConnectorHostBuilder.UseRelationalSchemaProvider</c> is refused at startup
/// for exactly that reason. Subclass it and annotate the subclass — it is not
/// sealed, and its methods are non-virtual, so the one-liner below inherits the
/// whole implementation.
/// </para>
/// <para>
/// The class this sits on is the one the SDK constructs when no instance is
/// supplied. A provider with constructor dependencies (a connection factory, a
/// catalog reader — every realistic one) is handed to
/// <c>UseRelationalSchemaProvider</c> ready-made instead; the attribute on its
/// class still supplies the declaration.
/// </para>
/// <para>
/// There is deliberately no fingerprint here. The catalog fingerprint is read
/// live from the provider at register time — a value captured when the assembly
/// was scanned would be stale the moment the source catalog changed, and a
/// stale fingerprint tells the core "nothing changed, do not re-extract", which
/// is precisely the silently-wrong-schema failure this layer exists to prevent.
/// </para>
/// </remarks>
/// <example>
/// Annotate a subclass of the SDK's provider, then hand the host a ready-made
/// instance. <c>DescribeTool</c> and <c>QueryTool</c> must name tools this
/// connector actually declares, and <c>SqlArg</c> must name an argument of the
/// query tool — the host checks all three at startup.
/// <code>
/// [RelationalSource(Engine = "sqlserver",
///                   DescribeTool = "erp_bc.describe_schema",
///                   QueryTool = "erp_bc.query_sql",
///                   SqlArg = "Sql")]
/// public sealed class BcSchemaProvider(ICatalogReader reader) : SqlServerProvider(reader);
///
/// // Program.cs
/// return await ConnectorHost.CreateBuilder()
///     .ScanAssembly(Assembly.GetExecutingAssembly())
///     .UseRelationalSchemaProvider(new BcSchemaProvider(catalogReader))
///     .Build()
///     .RunFromEnvironmentAsync();
/// </code>
/// </example>
/// <example>
/// A source spanning more than one scope (multiple BC companies, multiple
/// MySQL databases) MUST set <see cref="DefaultScope"/>: it is the only party
/// that knows which scope an UNQUALIFIED table name should resolve in, and
/// <c>Build()</c> throws <see cref="ArgumentException"/> rather than let that
/// ambiguity reach a query at runtime. A single-scope (or scope-less) source
/// may leave it blank.
/// <code>
/// [RelationalSource(Engine = "sqlserver",
///                   DescribeTool = "erp_bc.describe_schema",
///                   QueryTool = "erp_bc.query_sql",
///                   SqlArg = "Sql",
///                   Scopes = new[] { "CRONUS-USA", "CRONUS-UK" },
///                   DefaultScope = "CRONUS-USA")]
/// public sealed class BcSchemaProvider(ICatalogReader reader) : SqlServerProvider(reader);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RelationalSourceAttribute : Attribute
{
    /// <summary>
    /// The database engine behind this connector — <c>"sqlserver"</c> or
    /// <c>"mysql"</c>. It selects the core's dialect handling.
    /// </summary>
    public string Engine { get; set; } = "";

    /// <summary>
    /// Key of the rowset tool that returns this source's canonical schema
    /// (e.g. <c>"erp_bc.describe_schema"</c>).
    /// </summary>
    public string DescribeTool { get; set; } = "";

    /// <summary>
    /// Key of the free-form SQL tool the core's query gate governs
    /// (e.g. <c>"erp_bc.query_sql"</c>).
    /// </summary>
    public string QueryTool { get; set; } = "";

    /// <summary>
    /// Which argument of <see cref="QueryTool"/> carries the SQL text.
    /// Per-connector and never assumed: this SDK serialises tool arguments in
    /// PascalCase, so a .NET connector's key is typically <c>"Sql"</c>, where a
    /// PHP one's is <c>"sql"</c>. Naming the wrong key reads null downstream and
    /// silently gates nothing.
    /// </summary>
    public string SqlArg { get; set; } = "";

    /// <summary>
    /// Which argument of <see cref="QueryTool"/> carries bind parameters, as an
    /// object of name to value. Empty = this source takes none.
    /// </summary>
    /// <remarks>
    /// Values behind this argument are BOUND by the connector, never
    /// interpolated into the SQL.
    /// </remarks>
    public string ParamsArg { get; set; } = "";

    /// <summary>
    /// The databases (MySQL) or companies (Business Central) this source
    /// spans. Declared here, statically, because <c>Build()</c> must validate
    /// against it at BOOTSTRAP — before any extraction has happened and
    /// before the worker ever dials the hub — which rules out sourcing this
    /// from the live, I/O-bound <see cref="IRelationalSchemaProvider.ScopesAsync"/>.
    /// Leave empty for a source with no meaningful scope split.
    /// </summary>
    public string[] Scopes { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Which of <see cref="Scopes"/> an UNQUALIFIED table name resolves in.
    /// Required when <see cref="Scopes"/> has more than one entry —
    /// <c>Build()</c> throws <see cref="ArgumentException"/> otherwise.
    ///
    /// This decides what an unqualified name means and NOTHING else. A
    /// qualified <c>scope.table</c> is never re-pointed at this default, and
    /// a cross-scope join resolves each side in its own scope.
    /// </summary>
    public string DefaultScope { get; set; } = "";
}
