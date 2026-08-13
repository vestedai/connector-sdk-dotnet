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
/// The class this sits on is the one the SDK will construct when no instance is
/// supplied. A provider with constructor dependencies (a connection factory, a
/// catalog reader — <see cref="SqlServerProvider"/> and every realistic one) is
/// handed to <c>ConnectorHostBuilder.UseRelationalSchemaProvider</c> ready-made
/// instead; the attribute on its class still supplies the declaration.
///
/// There is deliberately no fingerprint here. The catalog fingerprint is read
/// live from the provider at register time — a value captured when the assembly
/// was scanned would be stale the moment the source catalog changed, and a
/// stale fingerprint tells the core "nothing changed, do not re-extract", which
/// is precisely the silently-wrong-schema failure this layer exists to prevent.
/// </remarks>
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
}
