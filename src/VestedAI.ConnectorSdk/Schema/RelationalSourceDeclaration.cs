namespace VestedAI.ConnectorSdk.Schema;

/// <summary>
/// Normalized relational-source declaration derived from
/// <see cref="RelationalSourceAttribute"/>.
/// </summary>
/// <remarks>
/// Carries no fingerprint by design: the catalog hash is read live from the
/// provider when <c>Register</c> is built, never captured at scan time. See
/// <see cref="RelationalSourceAttribute"/> for why a captured one would be
/// worse than none at all.
/// </remarks>
/// <param name="Engine">Database engine — "sqlserver" or "mysql".</param>
/// <param name="DescribeTool">Rowset tool returning the canonical schema.</param>
/// <param name="QueryTool">The free-form SQL tool the core's gate governs.</param>
/// <param name="SqlArg">Which argument of <paramref name="QueryTool"/> carries the SQL.</param>
/// <param name="Scopes">The databases/companies this source spans. Empty for a scope-less source.</param>
/// <param name="DefaultScope">
/// Which of <paramref name="Scopes"/> an unqualified table name resolves in. Validated against
/// <paramref name="Scopes"/> in <see cref="Reflection.DeclarationFactory.FromRelationalSourceType"/>,
/// so a declaration reaching here already satisfies both bootstrap invariants.
/// </param>
/// <param name="ProviderType">
/// The annotated class. The SDK constructs it only when no ready-made instance
/// was supplied to <c>UseRelationalSchemaProvider</c>.
/// </param>
public sealed record RelationalSourceDeclaration(
    string Engine,
    string DescribeTool,
    string QueryTool,
    string SqlArg,
    IReadOnlyList<string> Scopes,
    string DefaultScope,
    Type ProviderType);
