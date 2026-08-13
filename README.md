# VestedAI.ConnectorSdk

![Build](https://img.shields.io/github/actions/workflow/status/vestedai/connector-sdk-dotnet/ci.yml?branch=main)
![NuGet](https://img.shields.io/nuget/v/VestedAI.ConnectorSdk)
![License](https://img.shields.io/github/license/vestedai/connector-sdk-dotnet)
![.NET](https://img.shields.io/badge/.NET-8.0-blue)

Connect any .NET service to the Vested AI platform. The SDK opens a long-lived gRPC stream to the hub, declares agents and tools over that stream, and dispatches tool calls to your handler code — no polling, no webhook setup, no managing your own LLM client. The hub handles model selection, prompt composition, and conversation state; your connector owns the business logic.

## Install

```bash
dotnet add package VestedAI.ConnectorSdk
```

Requires .NET 8 (LTS). Or run the Docker image: `vestedai/vested-ai-connector-sdk-dotnet:0.1.0` (also `:latest`, multi-arch amd64/arm64).

## Connector Snippet

```csharp
using System.ComponentModel;
using System.Reflection;
using VestedAI.ConnectorSdk;

[Agent(Key = "myapp.orders", Name = "Orders", Model = "openai:gpt-4o",
       Description = "Looks up orders.")]
[Instruction(Type = "system", Position = 0, Body = "You help users look up orders.")]
public class OrdersAgent { }

[Tool(Key = "myapp.orders.get", Description = "Returns an order by ID.",
      Sensitivity = "read")]
public class GetOrder : ToolHandler<GetOrder.Args, GetOrder.Result>
{
    public class Args
    {
        [Description("Order ID")] public string Id { get; set; } = "";
    }
    public class Result
    {
        public string Status { get; set; } = "";
    }
    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new Result { Status = "shipped" });
}
```

```csharp
// Program.cs
return await ConnectorHost.CreateBuilder()
    .ScanAssembly(Assembly.GetExecutingAssembly())
    .Build()
    .RunFromEnvironmentAsync();   // reads VESTED_CONNECTOR_TOKEN + VESTED_CONNECTOR_HUB
```

## Declarations

Beyond agents and tools, a connector can declare two optional things on
`Register`. Both follow the same contract: **declare nothing and nothing
changes.** A connector that declares neither is untouched by both features.

### `[Credential]` — per-user credentials

Put `[Credential]` on your `IUserCredentialHandler`, one `[CredentialField]` per
field of the form the platform renders. Declaring a schema is what gates this
connector's tools on the calling user having valid credentials.

```csharp
using VestedAI.ConnectorSdk.Credential;

[Credential(Kind = "basic", Title = "Al-Saif ERP account")]
[CredentialField(Key = "username", Label = "ERP username", Type = "text",     Required = true)]
[CredentialField(Key = "password", Label = "ERP password", Type = "password", Required = true)]
public sealed class ErpCredentials : IUserCredentialHandler
{
    private readonly ErpClient _erp;

    public ErpCredentials(ErpClient erp) => _erp = erp;

    public async Task<CredentialValidation> ValidateAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
    {
        var who = await _erp.WhoAmIAsync(credential["username"], credential["password"]);

        return who is null
            ? CredentialValidation.Failed("ERP rejected those credentials.")
            : CredentialValidation.Succeeded(new Dictionary<string, string> { ["account"] = who.Login });
    }

    // Optional: tear down a remote session. Best-effort.
    public Task RevokeAsync(CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        => Task.CompletedTask;
}
```

```csharp
// Program.cs — a handler with constructor dependencies is handed over ready-made.
.UseCredentialHandler(new ErpCredentials(erpClient))
```

The handler needs a private key to open sealed envelopes — set
`VESTED_CREDENTIAL_PRIVATE_KEY` (or `..._FILE`), or call `UseCredentialKeys(…)`.
`Build()` throws at startup if a handler is registered without one, rather than
failing every credential check later with a puzzling message. Full guide:
[Per-user credentials](docs/credentials.md).

### `[RelationalSource]` — expose a database to schema extraction

Put `[RelationalSource]` on your `IRelationalSchemaProvider`. Declaring one is
what makes the connector's database visible to the platform's schema
extraction; a connector that declares none is never extracted.

The declaration names YOUR connector's tool keys, so it must sit on a class in
your own assembly. The SDK's `SqlServerProvider` therefore carries none — but it
is not sealed and its methods are non-virtual, so a one-line subclass inherits
the whole implementation and adds only the declaration:

```csharp
using VestedAI.ConnectorSdk.Schema;

[RelationalSource(
    Engine       = "sqlserver",
    DescribeTool = "erp_bc.data.describe_schema",  // a ROWSET tool you declare
    QueryTool    = "erp_bc.data.run_sql",          // the free-form SQL tool
    SqlArg       = "Sql")]                         // its SQL argument, wire-exact
public sealed class BcSchemaProvider(ICatalogReader reader) : SqlServerProvider(reader);
```

```csharp
// Program.cs — a provider with constructor dependencies is handed over ready-made.
return await ConnectorHost.CreateBuilder()
    .ScanAssembly(Assembly.GetExecutingAssembly())
    .UseRelationalSchemaProvider(new BcSchemaProvider(catalogReader))
    .Build()
    .RunFromEnvironmentAsync();
```

Three things worth knowing:

- **`Build()` cross-checks it.** Both tool keys must name tools this connector
  declares, and `SqlArg` must match an argument of the query tool **on the wire**
  — this SDK serialises PascalCase, so a .NET connector's key is typically
  `"Sql"` where a PHP one's is `"sql"`. Nothing downstream catches a typo: the
  platform would govern a key nothing answers to while the real tool ran
  ungoverned, which is why it is refused at startup instead.
- **The describe tool must be a `PaginatedToolHandler`.** A catalog does not fit
  one response, and only a paginated handler declares `result_kind = ROWSET`.
- **There is no fingerprint to supply.** `CatalogFingerprintAsync` is called live
  on every `Register`. If the database is unreachable, the SDK registers with an
  empty fingerprint and warns rather than dropping the declaration — an empty
  fingerprint costs a re-extraction; a dropped declaration would silently
  disable extraction altogether.

## What This Is

A **connector** is a long-lived worker process that registers one or more agents with the Vested AI hub. Each agent carries a model selection, a set of instruction blocks, and a set of tool definitions. Admins can override instruction bodies and disable tools in the admin UI; the connector's declared baseline is the floor that overrides are layered on top of. The hub routes LLM tool calls back to the connector over the same stream; the connector dispatches them to your handler code and returns results.

This differs from writing your own LLM client. The connector does not call the LLM directly. It registers capability and responds to callbacks. Prompt composition, model routing, conversation history, streaming to end users — all of that lives in the hub. The connector's surface area is: "declare what agents exist, implement what the tools do."

## Documentation

| Document | What's in it |
|---|---|
| [Quickstart](docs/quickstart.md) | Install, write your first agent + tool, run the worker, verify in the admin UI |
| [Concepts](docs/concepts.md) | Agents, tools, instructions, baselines vs overrides, inheritance state machine, reconciliation |
| [API reference](docs/api.md) | `ConnectorHost`, `[Agent]`, `[Tool]`, `ToolHandler<,>`, `ToolContext` |
| [Operations](docs/operations.md) | Docker, env vars, reconnect supervisor, exit codes, signals |
| [Upgrading](docs/upgrading.md) | Coming from the PHP / Python / Node SDK; v0.1.0 release notes |
| [Per-user credentials](docs/credentials.md) | Act on behalf of the calling user: sealed credentials the platform cannot read, validation, key rotation |
| [Doc index](docs/README.md) | Full table of contents including protocol reference |

## License + Status

MIT. Current release: **v0.1.0** (.NET 8, C# attribute API, POCO + NJsonSchema args, connector-declared tool sensitivity). Wire-parity with the PHP / Python / Node SDKs at v0.3. On [NuGet](https://www.nuget.org/packages/VestedAI.ConnectorSdk) (`dotnet add package VestedAI.ConnectorSdk`) and [Docker Hub](https://hub.docker.com/r/vestedai/vested-ai-connector-sdk-dotnet).

## Other language SDKs

Same wire protocol, same hub — [all four SDKs](../README.md) are at feature parity (including connector-declared tool sensitivity):

- [PHP](../php/README.md) — Packagist `vested-ai/connector-sdk-php`
- [Python](../python/README.md) — PyPI `vested-connect-sdk`
- [Node.js](../node/README.md) — npm `@vested-ai/connector-sdk`
