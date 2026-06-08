# API Reference

## ConnectorHost / ConnectorHostBuilder

The builder facade. Create one in `Program.cs`; it assembles the runtime and returns a `ConnectorApp`.

Source: `src/VestedAI.ConnectorSdk/ConnectorHost.cs`

**`ConnectorHost.CreateBuilder() -> ConnectorHostBuilder`**
Static entry point. All configuration follows via chained calls.

```csharp
var builder = ConnectorHost.CreateBuilder();
```

**`.ScanAssembly(Assembly assembly) -> ConnectorHostBuilder`**
Discover `[Agent]`- and `[Tool]`-decorated types in the given assembly. Pass `Assembly.GetExecutingAssembly()` to scan your connector's own assembly.

```csharp
builder.ScanAssembly(Assembly.GetExecutingAssembly());
```

**`.WithLogger(ILogger logger) -> ConnectorHostBuilder`**
Plug in any `Microsoft.Extensions.Logging.ILogger`-compatible instance. Default: a console-backed logger at `Information` level.

**`.UseInsecureTransport() -> ConnectorHostBuilder`**
Use plaintext HTTP (no TLS) for the gRPC connection. For local dev against a non-TLS hub only.

**`.Build() -> ConnectorApp`**
Validates the collected declarations and returns a `ConnectorApp`.

Validation performed at `Build()`:
- Each `[Tool]` class must derive from `ToolHandler<,>`.
- Tool key prefix must match an agent key + `.` (e.g. tool `myapp.orders.get` requires agent `myapp.orders`).
- `Sensitivity`, when non-empty, must be one of the canonical values; otherwise throws `ConnectorException`.
- Duplicate agent or tool keys throw `ConnectorException`.

---

## ConnectorApp

The runnable connector. Returned by `ConnectorHostBuilder.Build()`.

Source: `src/VestedAI.ConnectorSdk/ConnectorApp.cs`

**`RunAsync(string token, string hub, bool insecure = false) -> Task<int>`**
Run the supervisor loop. Connects to the hub, sends Hello + Register, then enters steady-state. On disconnect, backs off and reconnects. Returns `0` on clean shutdown (SIGTERM/SIGINT), `78` on token rejection.

```csharp
int code = await app.RunAsync(token, hub);
```

**`RunFromEnvironmentAsync() -> Task<int>`**
Reads `VESTED_CONNECTOR_TOKEN` and `VESTED_CONNECTOR_HUB` from environment variables. If either is missing, writes an error to stderr and returns `78`. Otherwise delegates to `RunAsync`.

```csharp
// Program.cs
return await ConnectorHost.CreateBuilder()
    .ScanAssembly(Assembly.GetExecutingAssembly())
    .Build()
    .RunFromEnvironmentAsync();
```

---

## `[Agent]` attribute

Declare an agent. Applied to a class — the class body is unused; it is a declaration container only.

```csharp
using VestedAI.ConnectorSdk;

[Agent(
    Key = "myns.orders",
    Name = "Orders",
    Description = "Manages order data",   // optional
    Status = "active",                    // default
    Model = "openai:gpt-4o")]             // required: "provider:model-name"
[Instruction(Type = "system",  Position = 0, Body = "You manage order data.")]
[Instruction(Type = "persona", Position = 1, Body = "Professional, concise.")]
public class OrdersAgent { }
```

`Key`, `Name`, and `Model` are required. All other fields are optional.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `Key` | string | — | Required. Namespace-prefixed agent identifier. |
| `Name` | string | — | Required. Human-readable label for the admin UI. |
| `Model` | string | — | Required. `"provider:model-name"` (e.g. `"openai:gpt-4o"`). |
| `Description` | string | `""` | Optional summary shown in the admin UI. |
| `Status` | string | `"active"` | `"active"` or `"inactive"`. |

---

## `[Instruction]` attribute

Declare one system-prompt block on an agent class. `AllowMultiple = true` — apply as many as needed.

```csharp
[Instruction(Type = "system", Position = 0, Body = "You manage order data.")]
[Instruction(Type = "persona", Position = 1, Body = "Professional, concise.", Format = "plain")]
public class OrdersAgent { }
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `Type` | string | — | Required. `"system"`, `"task"`, `"persona"`, or `"safety"`. |
| `Position` | int | — | Required. Ascending sort order within the agent. |
| `Body` | string | — | Required. Prompt text. |
| `Format` | string | `"markdown"` | `"markdown"`, `"jinja"`, or `"plain"`. |

---

## `[Tool]` attribute

Declare a tool and bind it to a handler class. The class must extend `ToolHandler<TArgs, TResult>`.

```csharp
using System.ComponentModel;
using VestedAI.ConnectorSdk;

[Tool(
    Key = "myns.orders.get",
    Name = "Get order",                   // optional; defaults to Key
    Description = "Returns a single order by ID.",
    DefaultDeadlineMs = 5000,             // optional; default 30 000
    MaxResultBytes = 65536,               // optional; default 1 MiB
    Sensitivity = "read")]                // optional; see below
public class GetOrder : ToolHandler<GetOrder.Args, GetOrder.Result>
{
    public class Args
    {
        [Description("Order ID")]
        public string Id { get; set; } = "";
    }

    public class Result
    {
        public string Status { get; set; } = "";
    }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new Result { Status = "shipped" });
}
```

The input JSON Schema is auto-generated from the `TArgs` POCO via NJsonSchema. `[System.ComponentModel.Description]` on `Args` properties flows into the schema so the LLM sees field descriptions. The output JSON Schema is generated from `TResult` the same way.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `Key` | string | — | Required. Namespace-prefixed tool identifier. |
| `Description` | string | — | Required. Shown to the LLM and in the admin UI. |
| `Name` | string | `""` | Optional; defaults to `Key` if empty. |
| `Sensitivity` | string | `""` | Optional; see below. |
| `DefaultDeadlineMs` | int | `30000` | Tool-call timeout in milliseconds. |
| `MaxResultBytes` | int | `1048576` | Maximum byte length of the JSON result (1 MiB default). |

### `Sensitivity` field

Controls how the hub's policy engine classifies this tool's side-effects.

| Value | Meaning |
|---|---|
| `"read"` | Read-only; never mutates data. |
| `"write"` | Creates or updates data. |
| `"destructive"` | Irreversibly deletes or overwrites data. |
| `"external_call"` | Makes a network call to a third-party system. |
| `"medium"` | General-purpose intermediate severity. |

`Sensitivity` is optional. If omitted or empty (`""`), the hub defaults it to `"external_call"`. Admins can override the effective value later from the admin UI regardless of what the connector declares.

A non-empty value that is not in the list above throws a `ConnectorException` at `Build()` time (startup), not at runtime.

```csharp
using VestedAI.ConnectorSdk;
// Sensitivity.ToolSensitivities = new[] { "read", "write", "destructive", "external_call", "medium" }
```

`Sensitivity.All` exposes the same array as a convenience constant.

---

## `ToolHandler<TArgs, TResult>` base class

Source: `src/VestedAI.ConnectorSdk/Tool/ToolHandler.cs`

```csharp
public abstract class ToolHandler<TArgs, TResult> : ToolHandlerBase
{
    public abstract Task<TResult> HandleAsync(TArgs args, ToolContext ctx);
}
```

`TArgs` — deserialized from the `args_json` bytes sent by the hub (System.Text.Json, case-insensitive). Deserialization failure throws `ToolValidationException` before `HandleAsync` is called.

`TResult` — any JSON-serializable type. Serialized by the SDK before sending back to the hub.

Throw any exception to signal a handler error. The SDK converts it to a `ToolCallResponse{error: message}` and the hub surfaces it in the run timeline. Only the exception message is sent — the full stack trace is not forwarded.

---

## `ToolContext` record

Source: `src/VestedAI.ConnectorSdk/Tool/ToolContext.cs`

Read-only value object passed to every handler.

```csharp
public sealed record ToolContext(
    int    OrgId,
    string AgentKey,
    string RunId,
    string ConversationId,
    string UserEmail = "",
    int    UserId = 0)
{
    public string               EmployeeNo                { get; init; } = "";
    public string               ErpIdentifier             { get; init; } = "";
    public IReadOnlyList<string> ErpDepartmentIdentifiers { get; init; } = Array.Empty<string>();
}
```

| Field | Type | Description |
|---|---|---|
| `RunId` | string | Hub-minted UUIDv7. Stable across logs and traces. |
| `OrgId` | int | Org that owns this run. |
| `UserId` | int | User who triggered the run. `0` for system/scheduled runs. |
| `UserEmail` | string | Caller's email. Empty for system runs. **PII — do not log or persist.** |
| `ConversationId` | string | Conversation this run belongs to. |
| `AgentKey` | string | Key of the agent being run. |
| `EmployeeNo` | string | Caller's ERP employee number. Empty string when unset. Source: proto field 10. |
| `ErpIdentifier` | string | Caller's primary ERP user identifier (e.g. SAP user ID). Empty string when unset. Source: proto field 11. |
| `ErpDepartmentIdentifiers` | `IReadOnlyList<string>` | ERP identifiers of every department the caller belongs to. Empty list (never null) when unset. Source: proto field 12 (repeated). |

**Nullable note**: `EmployeeNo` and `ErpIdentifier` are always non-null strings (empty `""` when the hub sends no value). `ErpDepartmentIdentifiers` is always a non-null list (`Array.Empty<string>()` when absent). The three ERP fields are `init`-only properties rather than positional record parameters because C# positional parameters cannot default to collection literals while satisfying `TreatWarningsAsErrors` with nullable reference types enabled.

---

## Error types

Source: `src/VestedAI.ConnectorSdk/Errors/`

| Class | Thrown when |
|---|---|
| `ConnectorException` | Base class for all SDK errors. |
| `TokenException : ConnectorException` | Token rejected by the hub (`GoAway{token_rotated}` or `GoAway{revoked}`). Causes exit 78. |
| `ToolValidationException : ConnectorException` | Input JSON failed to deserialize into `TArgs`. Carries `ToolKey`. |

---

## `Sensitivity` class

`Sensitivity.ToolSensitivities` (alias `Sensitivity.All`) is a `string[]` constant: `{ "read", "write", "destructive", "external_call", "medium" }`.

## Next

[Operations](operations.md)
