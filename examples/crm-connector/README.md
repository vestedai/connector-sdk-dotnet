# CRM Connector — .NET example

A runnable Vested AI connector that demonstrates the core `VestedAI.ConnectorSdk`
attribute API using a generic CRM domain.

The connector ships two agents and three tools backed by hardcoded fake data —
no external service or database is required.

---

## What this example demonstrates

| Feature | Where |
|---|---|
| `[Agent]` + `[Instruction]` attributes | `Agents.cs` |
| `[Tool]` with `Sensitivity` | `Tools.cs` |
| POCO `Args` / `Result` with `[Description]` | `Tools.cs` |
| `ToolHandler<TArgs, TResult>` base class | `Tools.cs` |
| `ToolValidationException` for not-found errors | `Tools.cs`, `FakeData.cs` |
| In-memory mutable state (deal stage updates) | `FakeData.cs` |
| `ConnectorHost.RunFromEnvironmentAsync` entrypoint | `Program.cs` |
| Multi-stage Docker build (SDK → runtime base) | `Dockerfile` |

---

## Domain model

### Agents

| Agent key | Name | Purpose |
|---|---|---|
| `crm.sales` | Sales Ops | Contact look-ups and deal stage updates |
| `crm.analytics` | Analytics | Pipeline aggregation and value summaries |

### Tools

| Tool key | Sensitivity | Description |
|---|---|---|
| `crm.sales.lookup_contact` | `read` | Find a contact by email; returns name, company, lifecycle stage, owner |
| `crm.sales.update_deal_stage` | `write` | Move a deal to a new stage; returns previous and new stage |
| `crm.analytics.pipeline_summary` | `read` | Aggregate open deals by stage over a rolling window |

### Fake data

`FakeData.cs` contains 6 contacts and 10 deals with clearly fictional names and
`example.com`-style domains. Deal stage mutations are held in memory for the
lifetime of the process — they reset on restart.

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8) (for `dotnet run`)
- A Vested AI connector token and hub address (see the platform docs)
- Or Docker (for the containerised path)

---

## Project layout

```
crm-connector/
├── CrmConnector.csproj   # Exe project; references the SDK via ProjectReference
├── Program.cs            # Top-level statements: ConnectorHost → RunFromEnvironmentAsync
├── Agents.cs             # [Agent] + [Instruction] declarations
├── Tools.cs              # Three [Tool] ToolHandler<,> implementations
├── FakeData.cs           # In-memory contacts + deals; lookup/mutate helpers
├── .env.example          # Environment variable template
├── Dockerfile            # Multi-stage build (dotnet/sdk:8.0 → dotnet/runtime:8.0)
└── README.md             # This file
```

---

## Running locally with `dotnet run`

### 1. Set the required environment variables

```bash
export VESTED_CONNECTOR_TOKEN=<your-token>
export VESTED_CONNECTOR_HUB=hub.example.com:4443
```

For a local hub over plain HTTP, also call `.UseInsecureTransport()` in `Program.cs`
(or set the `VESTED_CONNECTOR_HUB` to your local address and add the builder option).

### 2. Run from the dotnet sub-tree root

```bash
cd vested-ai-sdks/dotnet
dotnet run --project examples/crm-connector/CrmConnector.csproj
```

Or from the example directory itself:

```bash
cd vested-ai-sdks/dotnet/examples/crm-connector
dotnet run
```

The process starts, connects to the hub, registers both agents and all three tools,
then enters the steady-state receive loop. Press Ctrl-C for a graceful shutdown
(exit code 0).

### 3. Exit codes

| Code | Meaning |
|---|---|
| `0` | Graceful shutdown (SIGINT / SIGTERM) |
| `78` | Token rejected or missing environment variable |
| `1` | Unexpected error — supervisor will reconnect with backoff |

---

## Tool reference

### `crm.sales.lookup_contact` (sensitivity: `read`)

Finds a CRM contact by email address.

**Args**

| Field | Type | Description |
|---|---|---|
| `email` | `string` | Email address of the contact to look up |

**Result**

| Field | Type | Description |
|---|---|---|
| `name` | `string` | Full name of the contact |
| `company` | `string` | Company the contact belongs to |
| `lifecycleStage` | `string` | `lead`, `prospect`, `customer`, or `churned` |
| `ownerEmail` | `string` | Email of the owning sales rep |

**Error:** throws `ToolValidationException` when no contact matches the email.

---

### `crm.sales.update_deal_stage` (sensitivity: `write`)

Moves a deal to a new pipeline stage. Safe to call multiple times — if the deal
is already at the target stage, the previous and new stage fields will be equal.

**Args**

| Field | Type | Description |
|---|---|---|
| `dealId` | `string` | Unique deal identifier (e.g. `D-0001`) |
| `newStage` | `string` | Target stage: `prospect`, `qualified`, `proposal`, `won`, or `lost` |

**Result**

| Field | Type | Description |
|---|---|---|
| `dealId` | `string` | Deal identifier echoed back |
| `previousStage` | `string` | Stage before the update |
| `newStage` | `string` | Stage after the update |

**Errors:**
- Invalid `newStage` value → `ToolValidationException` listing the valid options.
- Unknown `dealId` → `ToolValidationException`.

---

### `crm.analytics.pipeline_summary` (sensitivity: `read`)

Aggregates deals created within a rolling time window.

**Args**

| Field | Type | Description |
|---|---|---|
| `windowDays` | `int` | Rolling window in days (1–365). Defaults to `30` |

**Result**

| Field | Type | Description |
|---|---|---|
| `windowDays` | `int` | Window that was used |
| `countByStage` | `object` | Deal count per stage (e.g. `{"qualified": 2, "proposal": 2}`) |
| `valueByStage` | `object` | Total deal value (USD) per stage |
| `totalOpenDeals` | `int` | Open deals (excludes `won` and `lost`) |
| `totalOpenValue` | `decimal` | Combined USD value of all open deals |

---

## Building and running with Docker

Build context must be the `vested-ai-sdks/dotnet/` directory so that the
multi-stage build can copy both the SDK sources and the example project:

```bash
cd vested-ai-sdks/dotnet

docker build \
  --platform linux/amd64 \
  -f examples/crm-connector/Dockerfile \
  -t crm-connector:local \
  .
```

Run the image:

```bash
docker run --rm \
  -e VESTED_CONNECTOR_TOKEN=<your-token> \
  -e VESTED_CONNECTOR_HUB=hub.example.com:4443 \
  crm-connector:local
```

To use a local hub (insecure, no TLS), also set `UseInsecureTransport()` in
`Program.cs` before building the image, or expose the option via an environment
variable and read it in `Program.cs`.

---

## Customising for a real CRM

The example is deliberately self-contained so you can follow the data path end-to-end
without any external dependencies. To adapt it to a real CRM (HubSpot, Salesforce,
Dynamics 365, etc.):

1. Replace `FakeData.cs` with an actual API client. Use the environment or
   `Microsoft.Extensions.Configuration` to supply API keys and base URLs.
2. Update `HandleAsync` in each tool to call the real client.
3. Expand the `Args`/`Result` POCOs as needed — `[Description]` on each property
   flows into the JSON Schema shown to the LLM.
4. Add more agents and tools following the same `[Agent]` / `[Tool]` pattern; the
   assembly scanner in `ConnectorHost.ScanAssembly` picks them up automatically.
5. Update `.env.example` with any new environment variables.
6. Rebuild the Docker image with `docker build ...` as shown above.

---

## See also

- [`docs/quickstart.md`](../../docs/quickstart.md) — end-to-end quickstart for the .NET SDK
- [`docs/api.md`](../../docs/api.md) — full `[Agent]`, `[Tool]`, `ConnectorHost` API reference
- [`docs/concepts.md`](../../docs/concepts.md) — agents, tools, sensitivity, and the wire protocol
