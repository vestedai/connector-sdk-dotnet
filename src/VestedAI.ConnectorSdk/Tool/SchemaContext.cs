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
/// The canonical object name(s) THIS ENTITY resolved to, as stored in the
/// snapshot — not as the model spelled them, which resolution matches
/// case-insensitively. NOT necessarily every physical name the statement
/// touches: in <c>"observe"</c>, an entity the core refused never gets a
/// <see cref="SchemaContextTable"/> at all, even though the statement still
/// reads it — see <see cref="SchemaContext"/>'s own remarks.
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
/// <see cref="ToolContext.SchemaContext"/> itself being null. See also the
/// <c>observe</c> remark below: even non-empty, this is not guaranteed
/// complete.
/// </param>
/// <param name="HasStar">
/// The statement selects <c>*</c> somewhere. Carried because a connector's
/// own rule may be stricter than the core's about unbounded reads.
/// </param>
/// <param name="GateMode">
/// <c>"enforce"</c> | <c>"observe"</c> — which mode the CONNECTOR's gate is
/// configured in. See the remarks below: this does NOT distinguish a refusal
/// the call is proceeding through from a genuine allow.
/// </param>
/// <remarks>
/// ⚠ A NULL <see cref="ToolContext.SchemaContext"/> IS NOT AN EMPTY
/// <see cref="Tables"/>. Null means the core had nothing to tell you — this
/// connector declares no relational source, its gate mode is <c>off</c>, or
/// this is not the governed query tool. It NEVER means "no tables were
/// touched". An empty <see cref="Tables"/> list on a PRESENT context is a
/// different claim: the gate decided, and resolved nothing.
///
/// ⚠ IN <c>"observe"</c>, <see cref="Tables"/> IS NOT THE COMPLETE SET OF
/// OBJECTS THIS STATEMENT TOUCHES. A table the core's per-entity check
/// refused is excluded from this list, but in <c>observe</c> the call
/// proceeds and reads it anyway — so a statement joining a denied table
/// alongside granted ones arrives with <see cref="Tables"/> missing exactly
/// the table the core flagged. Treat this list as "every object the core is
/// willing to vouch for", never as "every object the statement reads".
///
/// ⚠ <see cref="GateMode"/> CANNOT TELL YOU "the core refused this and is
/// letting it through anyway" FROM "the core allowed it": it reads exactly
/// <c>"observe"</c> in both cases. Nothing on this record says which one
/// happened.
///
/// Advisory and one-way: the core has already decided. A handler is free to
/// refuse a call its own rules reject; nothing it does here reaches back to
/// the core.
/// </remarks>
public sealed record SchemaContext(
    IReadOnlyList<SchemaContextTable> Tables, bool HasStar, string GateMode);
