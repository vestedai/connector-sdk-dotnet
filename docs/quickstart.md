# Quickstart

Reading time: ~15 minutes. By the end, a connector worker is running locally, registered with the hub, and the agent is visible in the admin UI.

## Prerequisites

- **.NET 8 SDK** (`dotnet --version` should print `8.*`)
- A running Vested AI instance with admin access

## 1. Get a Connector Token

Sign in to the admin UI. Navigate to **Integrations → Add integration**. Fill in:

- **Namespace** — a short identifier for your connector (e.g., `myapp`). All agent and tool keys must start with this namespace.
- **Name** — human-readable label.

Click **Create**. Copy the token shown — it is displayed only once.

## 2. Create a Project

```bash
dotnet new console -n my-connector
cd my-connector
dotnet add package VestedAI.ConnectorSdk
```

Expected directory shape after setup:

```
my-connector/
  my-connector.csproj
  Program.cs          ← you will rewrite this
  Agents.cs           ← you will create this
  Tools.cs            ← you will create this
```

## 3. Declare Your First Agent and Tool

Create `Agents.cs`:

```csharp
using VestedAI.ConnectorSdk;

[Agent(
    Key = "myapp.greeting",
    Name = "Greeting Agent",
    Description = "Says hello",
    Model = "openai:gpt-4o")]
[Instruction(Type = "system", Position = 0, Body = "You greet users warmly and briefly.")]
public class GreetingAgent { }
```

Create `Tools.cs`:

```csharp
using System.ComponentModel;
using VestedAI.ConnectorSdk;

[Tool(Key = "myapp.greeting.hello",
      Name = "Say hello",
      Description = "Returns a greeting for the given name.")]
public class SayHello : ToolHandler<SayHello.Args, SayHello.Result>
{
    public class Args
    {
        [Description("The person's name to greet")]
        public string Name { get; set; } = "";
    }

    public class Result
    {
        public string Message { get; set; } = "";
    }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new Result { Message = $"Hello, {args.Name}!" });
}
```

`Args` is a plain C# class (POCO). NJsonSchema auto-generates the JSON Schema for the `input_schema_json` field from it. `[Description]` on properties flows into the schema so the LLM sees field descriptions when deciding what arguments to supply.

## 4. Wire Program.cs

Replace `Program.cs`:

```csharp
using System.Reflection;
using VestedAI.ConnectorSdk;

return await ConnectorHost.CreateBuilder()
    .ScanAssembly(Assembly.GetExecutingAssembly())
    .Build()
    .RunFromEnvironmentAsync();
```

`ScanAssembly` discovers all `[Agent]`- and `[Tool]`-decorated types in the assembly. `RunFromEnvironmentAsync` reads `VESTED_CONNECTOR_TOKEN` and `VESTED_CONNECTOR_HUB` from the environment and returns the process exit code.

## 5. Run the Worker Locally

```bash
export VESTED_CONNECTOR_TOKEN=eyJ…
export VESTED_CONNECTOR_HUB=ai-connect.example.com:4443
dotnet run
```

On success:

```
[Information] Connected to hub connector_id=42 namespace=myapp max_concurrent=16
```

The worker stays running. Leave it running for step 6.

To use plaintext gRPC against a local dev hub, call `.UseInsecureTransport()` on the builder:

```csharp
ConnectorHost.CreateBuilder()
    .ScanAssembly(Assembly.GetExecutingAssembly())
    .UseInsecureTransport()
    .Build()
    .RunFromEnvironmentAsync();
```

## 6. Verify in the Admin UI

1. Navigate to **Integrations**. The connector's status badge should read **active** (green).
2. Navigate to **Agents**. The `myapp.greeting` agent should appear with the source column showing your connector name.
3. Open the agent detail. The version is auto-published on first registration.
4. Open the **Test** tab on the agent. Invoke the `myapp.greeting.hello` tool with `{"name": "World"}`. The response should be `{"message": "Hello, World!"}`.

## Next

[Concepts](concepts.md)
