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
