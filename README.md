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
| [Doc index](docs/README.md) | Full table of contents including protocol reference |

## License + Status

MIT. Current release: **v0.1.0** (.NET 8, C# attribute API, POCO + NJsonSchema args). Wire-parity with the PHP / Python / Node SDKs at v0.3. On [NuGet](https://www.nuget.org/packages/VestedAI.ConnectorSdk) (`dotnet add package VestedAI.ConnectorSdk`) and [Docker Hub](https://hub.docker.com/r/vestedai/vested-ai-connector-sdk-dotnet).
