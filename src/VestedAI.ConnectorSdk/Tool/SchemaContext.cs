namespace VestedAI.ConnectorSdk.Tool;

/// <summary>One entity the core's SQL gate resolved for this call.</summary>
/// <param name="LogicalName">
/// The platform's label for the entity. NOT a queryable object name — on
/// Business Central the real object carries a company prefix and an
/// extension suffix. Key permission checks on <see cref="Physical"/>.
/// </param>
/// <param name="Scope">The scope (e.g. company) this table was resolved in.</param>
/// <param name="Kind">"table" | "view".</param>
/// <param name="Physical">
/// The canonical object name(s) this statement actually referenced, as
/// stored in the snapshot — not as the model spelled them, which resolution
/// matches case-insensitively.
/// </param>
public sealed record SchemaContextTable(
    string LogicalName, string Scope, string Kind, IReadOnlyList<string> Physical);

/// <summary>
/// What the core's SQL gate resolved for a governed <c>run_sql</c> call, so a
/// connector can apply its OWN permission layer on top of the core's decision.
/// </summary>
/// <param name="Tables">
/// The tables/views the gate resolved. Can be empty on a PRESENT context —
/// see the remarks below for why that is not the same claim as
/// <see cref="ToolContext.SchemaContext"/> itself being null.
/// </param>
/// <param name="HasStar">
/// The statement selects <c>*</c> somewhere. Carried because a connector's
/// own rule may be stricter than the core's about unbounded reads.
/// </param>
/// <param name="GateMode">
/// <c>"enforce"</c> | <c>"observe"</c>. Lets a handler tell "the core refused
/// this and is letting it through anyway" (observe) from "the core allowed
/// it" (enforce) — the core not enforcing does not mean the connector should
/// not.
/// </param>
/// <remarks>
/// ⚠ A NULL <see cref="ToolContext.SchemaContext"/> IS NOT AN EMPTY
/// <see cref="Tables"/>. Null means the core had nothing to tell you — this
/// connector declares no relational source, its gate mode is <c>off</c>, or
/// this is not the governed query tool. It NEVER means "no tables were
/// touched". An empty <see cref="Tables"/> list on a PRESENT context is a
/// different claim: the gate decided, and resolved nothing.
///
/// Advisory and one-way: the core has already decided. A handler is free to
/// refuse a call its own rules reject; nothing it does here reaches back to
/// the core.
/// </remarks>
public sealed record SchemaContext(
    IReadOnlyList<SchemaContextTable> Tables, bool HasStar, string GateMode);
