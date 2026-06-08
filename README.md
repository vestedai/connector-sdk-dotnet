# VestedAI.ConnectorSdk

Official .NET 8 SDK for the [Vested AI](https://vested.ai) ConnectorHub. Build tool-serving connectors in C# using a clean attribute API.

## Install

```bash
dotnet add package VestedAI.ConnectorSdk
```

## Quick start

```csharp
using System.ComponentModel;
using System.Reflection;
using VestedAI.ConnectorSdk;

[Agent(Key = "myapp.orders", Name = "Orders", Model = "openai:gpt-4o",
       Description = "Looks up and manages orders.")]
[Instruction(Type = "system", Position = 0, Body = "You help users look up orders.")]
public class OrdersAgent { }

[Tool(Key = "myapp.orders.get", Description = "Returns an order by ID.", Sensitivity = "read")]
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

// Program.cs
return await ConnectorHost.CreateBuilder()
    .ScanAssembly(Assembly.GetExecutingAssembly())
    .Build()
    .RunFromEnvironmentAsync();
```

Set two environment variables and run:

```bash
export VESTED_CONNECTOR_TOKEN=<your-token>
export VESTED_CONNECTOR_HUB=hub.vested.ai:443
dotnet run
```

## Documentation

See [docs/quickstart.md](docs/quickstart.md) for the full walkthrough.

## Wire protocol

This SDK speaks the same bidirectional gRPC protocol as the PHP, Python, and Node SDKs. The proto definition lives in `Proto/connector_hub.proto` (synced from the monorepo canonical source at `proto/vested/v1/connector_hub.proto`).

## Requirements

- .NET 8 (LTS)
- A Vested AI ConnectorHub endpoint + token

## License

MIT
