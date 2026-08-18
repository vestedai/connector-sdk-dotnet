# Upgrading

## Coming from the PHP, Python, or Node SDK

This section maps PHP, Python, and Node SDK concepts to their .NET equivalents for customers evaluating or migrating between the SDKs.

### Install

| PHP | Python | Node.js | .NET |
|---|---|---|---|
| `composer require vested-ai/connector-sdk-php` | `pip install vested-connect-sdk` | `npm install @vested-ai/connector-sdk` | `dotnet add package VestedAI.ConnectorSdk` |

### Declaring Agents

| PHP / Python / Node | .NET |
|---|---|
| PHP `#[Agent(key: '...')]` attribute on a class | `[Agent(Key = "...")]` attribute on a class |
| Python `@agent(key="...", model_provider="...", model_name="...")` | `[Agent(Key = "...", Model = "openai:gpt-4o")]` — single `"provider:model"` string |
| Node `@agent({ key: "...", instructions: [...] })` | `[Agent(...)]` + one `[Instruction(...)]` per instruction block (allows multiple) |
| Python `Instruction(type="system", position=0, body="...")` dataclass | `[Instruction(Type = "system", Position = 0, Body = "...")]` attribute |

### Declaring Tools

| PHP / Python / Node | .NET |
|---|---|
| PHP `#[Tool(agentKey: '...', inputSchema: [...])]` + hand-written JSON Schema | `[Tool(Key = "...", Description = "...")]` on class extending `ToolHandler<TArgs, TResult>` |
| Python `class Args(BaseModel): id: str = Field(...)` — Pydantic model, schema auto-generated | `public class Args { [Description("...")] public string Id { get; set; } = ""; }` — POCO, NJsonSchema auto-generates |
| Python `async def handle(self, args: Args, ctx: ToolContext)` | `public override Task<Result> HandleAsync(Args args, ToolContext ctx)` |
| Pydantic `BaseModel` / PHP array schema / Zod `z.object()` | POCO class + `[System.ComponentModel.Description]` — no extra schema library needed |

### Bootstrap / Entry Point

| PHP | Python | Node.js | .NET |
|---|---|---|---|
| `bootstrap.php` returns a `ConnectorApp` instance | `bootstrap.py` imports modules then `ConnectorApp.create().scan_module(...)` | `bootstrap.ts` with `export default await ConnectorApp.create().scanModule(...)` | `Program.cs` with `ConnectorHost.CreateBuilder().ScanAssembly(...).Build().RunFromEnvironmentAsync()` |
| `vendor/bin/vested-connect worker --bootstrap=./bootstrap.php` | `vested-connect worker --bootstrap=./bootstrap.py` | `vested-connect worker --bootstrap=./bootstrap.ts` | `dotnet run` (no CLI wrapper; the connector is a compiled console app) |

### Concurrency Model

| PHP | Python | Node.js | .NET |
|---|---|---|---|
| Swoole coroutines (`ext-swoole` required) | asyncio (`async def` handlers) | Node.js event loop (`async` handlers) | Task Parallel Library (`async Task` handlers) |
| `Coroutine::defer` for cleanup | `async with` / `asyncio.to_thread()` | `try/finally`; `worker_threads` for CPU-bound work | `try/finally`; `Task.Run` for CPU-bound work |

### Env Vars and CLI

Env var names are identical (`VESTED_CONNECTOR_TOKEN`, `VESTED_CONNECTOR_HUB`). Exit codes are identical (0/78). Reconnect backoff schedule is identical (1 s → 30 s cap, ±20% jitter).

### Items Exclusive to Other SDKs (not applicable to .NET)

The following are PHP-, Python-, or Node-specific implementation details. They appear only here for cross-SDK reference:

- `ext-swoole`, `Swoole\Coroutine::defer`, `PDOProxy` — PHP/Swoole runtime.
- `bootstrap.php` — PHP entry point filename convention.
- `composer require` / Packagist — PHP package manager.
- `pip install` / PyPI — Python package manager.
- Pydantic `BaseModel` / `Field` — Python schema generation.
- `asyncio.to_thread()`, `asyncpg`, `grpcio` — Python-specific async I/O.
- `npm install` / npmjs — Node package manager.
- Zod `z.object()` / `zod-to-json-schema` — Node schema generation.
- `vested-connect worker --bootstrap=...` CLI — Node/PHP/Python entry-point pattern.
- Monolog loop-detection workaround — PHP-specific logging issue.

---

## v0.9.0 Release Notes

### v0.9.0 — the core's SQL gate resolution is exposed on `ToolContext` — ADDITIVE

The core's SQL gate resolves the tables it is willing to vouch for on a
governed `run_sql` call before letting it through. `ToolContext` now carries
that resolution so a connector handler (erp_bc is the live one) can apply its
OWN permission layer on top:

```csharp
public SchemaContext? SchemaContext { get; init; }
```

`SchemaContext` (`Tables`, `HasStar`, `GateMode`) and `SchemaContextTable`
(`LogicalName`, `Scope`, `Kind`, `Physical`) are new sealed records under
`VestedAI.ConnectorSdk.Tool`. Source: `ToolCallRequest.schema_context` (proto
field 16), mapped in `Dispatcher.BuildContext`. See that type's own XML doc
remarks for the full, current contract — corrected 2026-08-18 in the final
whole-branch review; this section is a summary, not the source of truth.

**Ignoring it is safe.** A handler that never reads `ctx.SchemaContext`
behaves exactly as before — nothing else on `ToolContext` changed shape, and
no existing constructor call (positional or named) needs updating.

⚠ **Null is NOT an empty table list.** `ctx.SchemaContext == null` means the
core sent nothing: this connector declares no relational source, its gate
mode is `off`, this is not the governed query tool, or (added 2026-08-18) the
gate's own refusal reason was `parse_failed` or `lookup_failed` — the gate
never got to read the statement at all in either case. It never means "no
tables were touched". A present `SchemaContext` with an empty `Tables` list is
a different claim: the gate genuinely decided and resolved nothing (e.g. a
catalog-only read). A handler that conflates the two ends up approving
everything a `null` reaches. The message is advisory and one-way — the core
has already decided; a handler's own refusal never reaches back to it.

⚠ **`GateMode` cannot tell you whether THIS call was refused.** It names
which mode the connector's gate is configured in, and reads exactly
`"observe"` on a genuine allow AND on a refusal the call is proceeding through
alike — nothing on `SchemaContext` distinguishes the two.

⚠ **In `"observe"`, `Tables` is not the complete set of objects the statement
touches.** A table the core's per-entity check refused is excluded from this
list, but in `observe` the call proceeds and reads it anyway — so a statement
joining a denied table alongside granted ones arrives with `Tables` missing
exactly the table the core flagged. The `foreach` below is therefore checking
"every object the core vouches for," not "every object this statement reads".

**To adopt it**, read `ctx.SchemaContext` in a tool handler and apply your own
permission check against `Tables[i].Physical`:

```csharp
public override Task<Result> HandleAsync(Args args, ToolContext ctx)
{
    if (ctx.SchemaContext is { } schema)
    {
        foreach (var table in schema.Tables)
        {
            if (IsRestricted(table.Physical))
                throw new ConnectorException("refused: restricted table");
        }
    }
    // ...
}
```

Intended git tag: `v0.9.0` (on the public mirror repo).

---

## v0.8.0 Release Notes

### v0.8.0 — Business Central's system tables can be described — ADDITIVE

`SqlServerProvider` dropped every table with no `<Company>$` prefix. That was
deliberate and documented ("$ndo$ internals, Access Control and the other 105
system tables ... drop out here") and invisible while the core's SQL gate ran in
`observe`. With the gate at `enforce` it became a hole: measured on the live
Al-Saif catalog, **0 of 16,250 extracted variants lacked a company prefix**, so
any query touching `User`, `Object`, `Access Control` or a permission set was
refusable as an unknown table — and no scope existed that an operator could pass
to `schema:extract` to fix it.

| Added | Meaning |
|---|---|
| `SqlServerProvider.SystemScopeKey` (`"$system"`) | The scope key those company-less tables are described under. |
| `ScopesAsync` / `DescribeAsync` are now `virtual` | A connector can extend or replace scope handling without forking the class. |

**Nothing changes for a connector that does not ask for it.** `DescribeAsync`
behaves exactly as before for a company scope. The one visible difference is
that `ScopesAsync` now appends `$system` — and ONLY when the catalog actually
contains such tables, so a source with none does not advertise a scope that
would extract to an empty catalog and be refused.

Each system table becomes one entity with one variant carrying its LITERAL
name, because that is what a caller must write: these tables are referenced
unprefixed. There is no variant set to stitch, so no join key. BC's own storage
internals (anything starting with `$`, e.g. `$ndo$cachesync`) stay excluded —
they describe how the catalog is stored, not anything a question can be asked
about.

⚠ **`$system` is unlikely to collide with a real company, not impossible.**
`BcPhysicalName`'s company group is non-greedy, which does not stop it spanning
a `$` when that is the only way the remainder matches — a company named
`$system` really would parse out of `$system$Item$<app-id>`. `ScopesAsync`
therefore DETECTS that clash and throws, rather than silently describing one
company's tables under a key that claims to hold none.

**To adopt it** a connector bumps the package and adds the key to its
`[RelationalSource]` attribute, e.g.:

```csharp
Scopes = new[] { "ASG", "ASG - KWT", "ASG - OM", "ASG - QAR", "ASG - UAE",
                 SqlServerProvider.SystemScopeKey },
```

then the operator extracts it once:
`php artisan schema:extract --connector=<id> --scope='$system'`. Declaring it
without extracting leaves the core's declared-vs-extracted drift alarm firing,
which is the intended signal that step two is outstanding.

Intended git tag: `v0.8.0` (on the public mirror repo).

---

## v0.7.0 Release Notes

### v0.7.0 — a tool can declare the agents it binds to

Tools bind to agents by namespace today: `myns.orders.get` belongs to agent `myns.orders` and nowhere else. Sharing behaviour across agents therefore meant duplicating the handler — a second class in a second namespace wrapping the same logic.

A tool can now name the agents it binds to. ```csharp
[Tool(Key = "erp.data.run_sql", Description = "…",
      Agents = new[] { "erp.data", "erp.retail" })]
```

Adding `Agents` to the attribute is source-compatible — C# attributes are named-property construction, so call sites that do not set it keep compiling unchanged.

**Omitting it changes nothing.** A connector that never sets it binds exactly the tools it binds today.

**A present list is authoritative, not additive.** The key's namespace confers nothing once a list is present, so a tool may live in one namespace and be callable only from another. ``"*"`` means every agent this connector declares and cannot be combined with explicit keys.

Refused before the worker dials the hub: an agent key this connector does not declare, ``"*"`` mixed with explicit keys, and a tool that neither matches an agent namespace nor names any agent. Declaring a list that omits the agent named in the tool's own key is legal — it is how you say "lives here, callable from there" — and logs a startup warning.

### v0.7.0 — the baseline fingerprint now covers agent→tool binding

**Behavioural, not source-breaking. Every connector re-registers once.**

`baseline_fingerprint` did not cover which agents a tool was bound to. That was safe only while binding was *derived* from the tool key — you could not change one without changing the other. With an explicit binding field, re-pointing a tool at different agents would have produced an identical fingerprint, and the hub would have short-circuited the registration as unchanged. Nothing would error; the change simply would not happen.

Each agent's canonical entry now carries its bound tool keys, so your connector's fingerprint changes once on upgrade even if you never use the new field. The re-registration produces **no draft** for review unless an agent's actual tool set changed.

### v0.7.0 — two cross-SDK fingerprint divergences fixed

Found while adding the above, and fixed in the same release. .NET, Node and Python canonicalise the same structure and are meant to agree; nothing checked that they did.

- **Sort comparer.** Node used `localeCompare`, .NET a bare `OrderBy` (`Comparer<string>.Default` is `CurrentCulture`), Python ordinal `sorted()`. Measured on realistic agent keys, ordinal and locale disagree on two independent pairs — so keys differing by case, or by `_` against a letter, already hashed differently per SDK. All three are now ordinal.
- **`model_config`.** .NET emitted `null` where Node and Python emit `{}`, which made .NET's fingerprint differ from both for *every* declaration set that has ever existed. .NET now emits `{}`.

Both are pinned by `vested-ai-sdks/testdata/fingerprint-vectors.json`, a shared fixture the three SDKs assert against.

Intended git tag: `v0.7.0` (on the public mirror repo).

---

## v0.6.0 Release Notes

### v0.6.0 — `[RelationalSource]` declares `Scopes`/`DefaultScope` — SOURCE-BREAKING

`[RelationalSource]` gains two new properties naming which databases/companies a connector's relational source spans and, when it spans more than one, which one an unqualified table name resolves in:

| Property | Type | Default | Meaning |
|---|---|---|---|
| `Scopes` | `string[]` | `Array.Empty<string>()` | The databases (MySQL) or companies (Business Central) this source spans. Declared statically on the attribute — not read from the live, I/O-bound `IRelationalSchemaProvider.ScopesAsync` — because `Build()` must validate it synchronously at bootstrap, before any extraction has happened and before the worker ever dials the hub. Empty for a source with no meaningful scope split; existing connectors are unaffected. |
| `DefaultScope` | `string` | `""` | Which of `Scopes` an unqualified table name resolves in. Required when `Scopes` has more than one entry. |

Adding these two properties to the attribute itself is source-compatible — `[RelationalSource(Engine = ..., ...)]` call sites that do not set them keep compiling exactly as before, C# attributes being named-property construction rather than positional.

**The source-breaking change is in `RelationalSourceDeclaration`, the internal record `DeclarationFactory` builds from the attribute.** `Scopes` and `DefaultScope` were inserted as two new **positional** record parameters *before* `ProviderType`:

```diff
 public sealed record RelationalSourceDeclaration(
     string Engine,
     string DescribeTool,
     string QueryTool,
     string SqlArg,
+    IReadOnlyList<string> Scopes,
+    string DefaultScope,
     Type ProviderType);
```

Any code constructing `RelationalSourceDeclaration` positionally — the record is `public`, so this includes test doubles and any downstream code that built one directly rather than through `DeclarationFactory.FromRelationalSourceType` — fails to compile until the two new arguments are inserted in the same position, or the call is rewritten to named arguments.

**`ConnectorHostBuilder.Build()` can now throw where it previously could not.** The chain from `ScanAssembly`/`UseRelationalSchemaProvider` through `Build()` validates two invariants at bootstrap, before the worker ever dials the hub, via `DeclarationFactory.FromRelationalSourceType` → `ValidateScopes`, and throws `ArgumentException` (not this file's usual `ConnectorException` — the mistake is a bad VALUE relationship between two fields the author supplied, not a missing declaration):

1. `scopes.Count > 1 && defaultScope == ""` — a source spanning more than one scope must name a default; a `[RelationalSource]` type that used to build cleanly now fails at bootstrap if it declares two or more scopes with no `DefaultScope`.
2. `scopes.Count > 1 && defaultScope != "" && !scopes.Contains(defaultScope)` — with SEVERAL scopes, a named default must be one of them.

**With exactly one scope, `DefaultScope` is ignored and the sole scope is declared instead**, whatever the attribute says. Here both values are compile-time constants, so unlike the PHP SDK this cannot drift per environment — it is kept for parity, so the same connector shape declares the same thing in both languages. The visible consequence: a single-scope source that declares no default now ships `DefaultScope = <that scope>` rather than `""`.

Same seam and same reasoning as the existing credential-keyring check (`ConnectorHostBuilder.Build()` throwing when a `[Credential]` handler is registered without a private key): refuse on the connector author's own deploy, with a stack trace, rather than let an unqualified table name resolve ambiguously the first time a model calls the query tool in production. A connector declaring neither property is completely unaffected — `Scopes` comes back empty, `DefaultScope` comes back `""`, and neither check can fire. The PHP SDK throws the equivalent generic argument exception (`InvalidArgumentException`) for the identical two invariants, so a connector author moving between the two SDKs gets the same guidance.

No other public API changed. dotnet 0.5.0 → 0.6.0. Minor bump, not a patch: the record's positional shape is a compile-time break for any direct constructor caller, and a multi-scope provider that built cleanly under 0.5.x can now throw at its next `Build()`. Intended git tag: `v0.6.0`.

---

## v0.2.0 Release Notes

### v0.2.0 — ERP Identity on ToolContext (L-5)

**New fields on `ToolContext`** (additive, no breaking changes):

| Field | Type | Default | Source |
|---|---|---|---|
| `EmployeeNo` | `string` | `""` | `ToolCallRequest.employee_no` (proto field 10) |
| `ErpIdentifier` | `string` | `""` | `ToolCallRequest.erp_identifier` (proto field 11) |
| `ErpDepartmentIdentifiers` | `IReadOnlyList<string>` | `Array.Empty<string>()` | `ToolCallRequest.erp_department_identifiers` (proto field 12) |

These carry the calling user's ERP/HR identity into every tool handler. All three default to empty (string) or empty list (never null) when the hub sends no value, so existing handlers that ignore them need no changes.

The three fields are `init`-only properties rather than positional parameters — C# 12 positional parameters cannot default to collection literals without disabling nullable warnings. Existing code that constructs `ToolContext` positionally or with named parameters continues to compile unchanged; the ERP properties default automatically.

**No breaking changes** within the v0.2.x series.

---

## v0.1.0 Release Notes

### v0.1.0 — Initial .NET Release

First C# / .NET SDK implementation. Targets .NET 8 (LTS), C# 12, nullable reference types enabled. Attribute-first API (`[Agent]`, `[Instruction]`, `[Tool]`). POCO + NJsonSchema schema generation — `[Description]` on `Args` properties flows into the LLM's input schema. Task-based async handlers (`async Task<TResult> HandleAsync`).

Wire-parity with PHP / Python / Node SDKs at v0.3 (including connector-declared tool sensitivity). Available on [NuGet](https://www.nuget.org/packages/VestedAI.ConnectorSdk) (`dotnet add package VestedAI.ConnectorSdk`) and [Docker Hub](https://hub.docker.com/r/vestedai/vested-ai-connector-sdk-dotnet).

**Baseline fingerprint**: ships with the correct behavior from day one — `baseline_fingerprint` is always a non-empty SHA-256 over the canonical agent + tool declarations. (The Python v0.2.0 bug sent an empty fingerprint; this SDK never had that issue.)

**Sensitivity**: `[Tool(Sensitivity = "...")]` is supported from v0.1.0 (wire parity with the J-5 feature in the other SDKs). Allowed values: `read`, `write`, `destructive`, `external_call`, `medium`. Omitting or leaving empty is valid — the hub defaults it to `external_call`.

**No breaking changes** are expected within the v0.1.x series.

## Next

[Connector protocol overview](protocol/overview.md)
