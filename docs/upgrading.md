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
