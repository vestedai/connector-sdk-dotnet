# Relational schema intelligence

An agent that can run SQL against your database has a problem before it writes a
single query: it does not know what the tables are called.

Left to itself it solves that with SQL — a `SELECT … FROM INFORMATION_SCHEMA.TABLES`
to find the table, another for the columns, then the real query. Every
conversation pays for it again, and a wrong guess costs a round trip and an
`Invalid object name`.

Schema intelligence replaces that. Declare a relational source and the platform
extracts your schema once, indexes it, and gives every agent two tools —
`search_schema` and `describe_entity` — that answer "what is this table called
and what columns does it have" from the index instead of from your database.

The gap is not small. On one production Business Central connector, over the
seven days to 2026-08-17, the SQL tool took **13,251 calls** while the schema
tools took **55** — and the platform was already holding a complete, embedded
snapshot of that database the whole time.

**Declaring nothing keeps you untouched.** A connector with no
`[RelationalSource]` registers no `relational_source`; the platform never
extracts its schema and never governs its SQL. Same contract as
[per-user credentials](credentials.md).

---

## Opting in

Annotate a class implementing `IRelationalSchemaProvider` and hand the host an
instance:

```csharp
using VestedAI.ConnectorSdk.Schema;

[RelationalSource(Engine       = "sqlserver",
                  DescribeTool = "erp_bc.describe_schema",
                  QueryTool    = "erp_bc.query_sql",
                  SqlArg       = "Sql")]
public sealed class BcSchemaProvider(ICatalogReader reader) : SqlServerProvider(reader);
```

```csharp
// Program.cs
return await ConnectorHost.CreateBuilder()
    .ScanAssembly(Assembly.GetExecutingAssembly())
    .UseRelationalSchemaProvider(new BcSchemaProvider(catalogReader))
    .Build()
    .RunFromEnvironmentAsync();
```

The attribute must sit on a class in **your** assembly. Everything it declares
is specific to your connector, so it cannot live on an SDK-owned provider:
`SqlServerProvider` deliberately carries no `[RelationalSource]`, and handing a
bare `new SqlServerProvider(...)` to `UseRelationalSchemaProvider` is refused at
startup for that reason. Subclass it and annotate the subclass — it is not
sealed and its methods are non-virtual, so the one-liner above inherits the
whole implementation.

`Build()` checks all three references before your connector ever dials the hub:
`DescribeTool` and `QueryTool` must name tools this connector actually declares,
and `SqlArg` must name an argument of the query tool.

### The fields

| Field | What it is |
|---|---|
| `Engine` | `"sqlserver"` or `"mysql"`. Selects the core's dialect handling; the core owns the supported set and names it when it rejects one. |
| `DescribeTool` | Key of the rowset tool returning your canonical schema. See [The describe tool contract](#the-describe-tool-contract). |
| `QueryTool` | Key of the free-form SQL tool the query gate governs. |
| `SqlArg` | Which argument of `QueryTool` carries the SQL text. |
| `Scopes` | The databases (MySQL) or companies (Business Central) this source spans. Empty for a source with no meaningful scope split. |
| `DefaultScope` | Which of `Scopes` an *unqualified* table name resolves in. |

There is deliberately **no fingerprint field**. The catalog fingerprint is read
live from your provider each time `Register` is built. A value captured at
assembly-scan time would be stale the moment your catalog changed, and a stale
fingerprint tells the core "nothing changed, do not re-extract" — precisely the
silently-wrong-schema failure this layer exists to prevent.

### `SqlArg` is case-sensitive, and getting it wrong fails silently

`SqlArg` must match your query tool's input schema **exactly, including case**.
This SDK serialises tool arguments in PascalCase, so a .NET connector's key is
typically `"Sql"` where a PHP connector's is `"sql"`.

Name the wrong key and the gate reads `null` where the SQL should be. It then
authorizes an empty string — successfully — while the real statement goes
unseen. Nothing errors. In production this went unnoticed for **3,634 calls**.

`Build()` refuses a `SqlArg` that names no argument of the query tool, which
catches the typo case. It cannot catch a name that is real but wrong.

## Scopes

A scope is one database (MySQL) or one company (Business Central). Table names
are unique only *within* a scope, which is why the platform tracks them.

`Scopes` is declared statically on the attribute, not read from your provider's
`ScopesAsync`, because `Build()` must validate against it at **bootstrap** —
before any extraction has happened and before the worker dials the hub. That
rules out sourcing it from a live, I/O-bound call.

**A source declaring more than one scope MUST set `DefaultScope`.** You own the
connection string, so you are the only party that knows which scope an
unqualified `SELECT … FROM Customer` means. `Build()` throws
`ArgumentException` rather than let that ambiguity reach a query at runtime —
and it also refuses a `DefaultScope` that is not one of the scopes you declared.

```csharp
[RelationalSource(Engine       = "sqlserver",
                  DescribeTool = "erp_bc.describe_schema",
                  QueryTool    = "erp_bc.query_sql",
                  SqlArg       = "Sql",
                  Scopes       = new[] { "CRONUS-USA", "CRONUS-UK" },
                  DefaultScope = "CRONUS-USA")]
public sealed class BcSchemaProvider(ICatalogReader reader) : SqlServerProvider(reader);
```

`DefaultScope` decides what an unqualified name means and **nothing else**. A
qualified `scope.table` is never re-pointed at the default, and a cross-scope
join resolves each side in its own scope.

A single-scope or scope-less source may leave it blank.

## The describe tool contract

This is the part you cannot guess, and the part the platform cannot check for
you: `DescribeTool` must return your schema in the **canonical model**.

`SqlServerProvider` already implements it for SQL Server. Write your own only
for an engine the SDK does not cover.

### The shape

Two row kinds, selected by the tool's `part` argument — `"entities"` or
`"relations"`:

```jsonc
// part = "entities"
{
  "logical_name": "Customer",
  "scope_key":    "ASG",
  "kind":         "table",
  "comment":      null,
  "join_key":     ["No_"],
  "variants": [
    { "physical_name": "ASG$Customer$437dbf0e-…", "role": "base",      "ordinal": 0 },
    { "physical_name": "ASG$Customer$1b77ef42-…", "role": "extension", "ordinal": 1 }
  ],
  "columns": [
    { "name": "No_", "type": "nvarchar", "nullable": false, "position": 2,
      "is_pk": true, "caption": null, "variant_physical_name": "ASG$Customer$437dbf0e-…" }
  ]
}

// part = "relations"
{
  "from_entity":  "Sales Line",
  "from_columns": ["Document No_"],
  "to_entity":    "Sales Header",
  "to_columns":   ["No_"],
  "kind":         "fk"
}
```

`kind` on an entity is `table` or `view`; `role` on a variant is `base` or
`extension`. `caption` is optional — the SQL Server provider leaves it null, so
do not build anything that assumes a human-readable name is present.

Relations come in two kinds. `fk` is a real foreign key. `variant_join` is
emitted for the join between a logical entity's variants — the platform needs
it to reassemble the entity, and it is not a foreign key in the source.

### Three things that trip people up

**The keys are snake_case.** Deliberately, and against this SDK's conventions
everywhere else. The core reads these rows with `CanonicalEntity::fromArray()`,
which keys on snake_case, while the SDK serialises PascalCase by default. Use
`CanonicalJson.Options` — that is what it is for. This is the same PascalCase /
snake_case seam that produced the `Sql` / `sql` bug above.

**`variants[]` is the whole point.** One *logical* entity is often several
*physical* tables. `Item` is 8 physical tables for one Business Central company;
`LSC Retail Setup` is 14 — a base table plus its extensions, joined on
`join_key`. An EAV source is the same story with value tables. **Nothing in
`INFORMATION_SCHEMA` tells you this**, which is exactly why the model cannot
work it out from a discovery query and why the index is worth building. Each
column names the variant it came from via `variant_physical_name`.

**An unknown `part` must throw, not return `[]`.** An empty rowset ingests as
"this scope genuinely has no relations", producing a snapshot with no joins that
looks complete. The SDK's `SchemaDescribeTool` throws `ArgumentException`; keep
that behaviour in a hand-written provider.

### Why a tool and not a hub operation

The catalog is 26,191 tables and hundreds of thousands of columns for a single
company. That will never fit one response. The rowset path already solves this
with the dataset sink and the SDK's 16 KB chunking, so extraction rides on it
rather than inventing a second streaming mechanism.

### The provider interface

```csharp
public interface IRelationalSchemaProvider
{
    Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct);
    Task<CanonicalSchema>       DescribeAsync(string scopeKey, CancellationToken ct);
    Task<string>                CatalogFingerprintAsync(CancellationToken ct);
}
```

`CatalogFingerprintAsync` should be **cheap** — it is called on every
`Register`. Hash the catalog's own metadata (object count plus max modify date
is usually enough); do not walk every column. The core re-extracts only when
this value changes, so a connector that knows its database is unchanged costs
the platform nothing.

### When the fingerprint cannot be read

Two rules the SDK applies for you, both worth understanding because they shape
how you should write `CatalogFingerprintAsync`:

**On any failure it still declares, with an empty fingerprint.** A source
database that is unreachable or cold at connector startup is normal and
transient. An empty fingerprint costs a re-extraction — expensive, but correct
and visible. Omitting the declaration instead would silently disable schema
extraction *and* the SQL gate for the whole session, with nothing reporting that
governance had stopped.

**The wait is bounded** — `DefaultFingerprintTimeout`, 10 seconds. The
fingerprint is read before `Register` is sent, while the hub's 30-second idle
timer is already running. Unbounded, a cold database outlasts that timer, the
hub sends `GoAway{reason:"idle"}` before `Register` is ever sent, the supervisor
reconnects, and it repeats — leaving your connector's *entire* tool surface
offline with only an idleness message to explain it.

Both failure modes end in the same place: silent, misattributed disablement.
Keep the fingerprint fast enough that neither fires in normal operation.

## Extraction

Extraction is **system-initiated**: the platform calls your describe tool, it is
not something a user triggers.

```
php artisan schema:extract [--connector=<id>] [--scope=<key>] [--force]
```

With no `--scope`, every scope that already has an active snapshot is refreshed.
`--force` re-extracts even when your declared fingerprint is unchanged.

A snapshot supersedes the previous one for that `(connector, scope)`; agents
read the active one.

### If you also declare per-user credentials

These two declarations used to be mutually exclusive — declaring a credential
schema made a connector permanently un-extractable, and both SDK READMEs said
so. **That is no longer true**, and nothing ever enforced it at registration.

Extraction has no acting human, so when your connector is credential-gated the
platform needs to know whose credential to spend. An operator names a
**schema automation user** on the connector. Until they do, every schema
operation is refused.

Use a dedicated service account, not an employee: that user's credential is what
gets spent, so **their** name lands in your downstream system's audit log for
every schema read.

Read the refusal reason to tell the cases apart:

| Reason | Meaning |
|---|---|
| `credential_gated` | Nobody is named as the automation user. |
| `no_credential` | Named, but that user has saved no credential. |
| `not_validated` | Named and saved, but the credential has not validated yet. |

A user from another organization is refused before any request leaves the
platform.

## What the agents get

Two tools, injected into agent runs by the platform — you do not declare them:

- **`search_schema`** — takes a question, returns the matching entities with
  their physical table names, join keys and ranked columns.
- **`describe_entity`** — returns one entity's full column list.

Both are gated by a system-wide switch (`schema.search_tool.enabled`) and belong
to the `schema` core-tool group, which an agent version can opt out of. So an
agent may not have them.

**Write your SQL tool's instructions accordingly.** Point the model at
`search_schema` / `describe_entity` first and keep engine-native discovery
(`INFORMATION_SCHEMA`, `sys.*`) as a documented fallback — both because the
tools can be switched off, and because a snapshot covers the scopes that were
extracted and nothing else.

Instruction text attached to the SQL tool's own argument is read at the moment
the model writes SQL, so it outweighs anything in the system prompt. That is
where the erp_bc numbers at the top of this page came from: the argument
description said *"NEVER write a table name you have not seen returned by
INFORMATION_SCHEMA"* and handed over the discovery query, and the schema tools
went unused for months.

## The SQL gate

Once your schema is extracted, the platform can authorize the SQL your query
tool receives: "may this run read the tables this statement names?"

Per-connector, an operator sets one of three modes:

| Mode | Behaviour |
|---|---|
| `off` | No gating. The default, and what every connector that declares nothing gets. |
| `observe` | The decision is computed and recorded; the call proceeds regardless. |
| `enforce` | A refusal stops the call. |

Start at `observe`. It tells you what `enforce` would have broken before it
breaks it.

**Your SQL is parsed in the runtime, not by the policy service.** Only the
extracted table references are sent for a decision — never the statement.
Model-authored SQL can quote customer data straight out of a `WHERE` clause, and
this keeps it out of that service entirely.

Refusal reasons you will actually meet:

| Reason | Meaning |
|---|---|
| `parse_failed` | Not a single read-only `SELECT`, or unparseable. |
| `select_star` | The statement contains `*`. |
| `unknown_table` | A referenced table is in no active snapshot. |
| `ambiguous_table` | An unqualified name resolves in more than one active scope. |
| `stale_snapshot` | The snapshot is too old to authorize against. |
| `no_grant` | Policy said no. |
| `lookup_failed` | The gate could not reach the platform and **failed closed**. An outage signal, not a user problem. |

`unknown_table` is the one to expect first, and usually it is correct: queries
against `INFORMATION_SCHEMA` itself resolve as unknown tables, because they are
not in your snapshot. Another reason to move discovery onto the schema tools
before turning `enforce` on.

## Things worth knowing

- **A stale fingerprint is worse than an expensive one.** If in doubt whether
  your hash covers a kind of change, make it coarser so it changes more often.
  Over-extracting costs a rowset; under-extracting serves the model a schema
  that no longer exists.
- **Extraction reads through your tools**, so whatever a describe call does to
  your database — locks, plan cache, connection budget — happens on your side.
  Keep the catalog read cheap.
- **Scopes are declared, not discovered.** Adding a company or a database to
  your source needs a new `Scopes` value and a re-register; extraction will not
  find it on its own.
- **Rolling back a declaration works.** `Connector::credentialSchema()` and the
  relational source both read the *current* baseline, so withdrawing a
  declaration takes effect on the next register.
