# Operations

## Docker

A minimal customer Dockerfile for a .NET connector:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish MyConnector.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "MyConnector.dll"]
```

The entrypoint reads `VESTED_CONNECTOR_TOKEN` and `VESTED_CONNECTOR_HUB` from the environment via `RunFromEnvironmentAsync()`.

Run as a single long-lived container (`replicas: 1` per token in Kubernetes). Graceful shutdown on SIGTERM: in-flight tool calls drain up to their remaining `deadlineMs` before the process exits.

---

## Environment Variables

| Variable | Required | Default | Description |
|---|---|---|---|
| `VESTED_CONNECTOR_TOKEN` | Yes | — | JWT from the admin UI (Integrations → Add). |
| `VESTED_CONNECTOR_HUB` | Yes | — | Hub address as `host:port`, e.g. `ai-connect.example.com:4443`. |
| `LOG_LEVEL` | No | `Information` | Log level: `Debug`, `Information`, `Warning`, `Error`. Maps to `Microsoft.Extensions.Logging` levels. |

`RunFromEnvironmentAsync()` reads `VESTED_CONNECTOR_TOKEN` and `VESTED_CONNECTOR_HUB`. If either is absent, the SDK writes an error message to stderr and returns exit code `78` without connecting.

---

## Publishing Your Connector

Build and publish with `dotnet publish`:

```bash
dotnet publish MyConnector.csproj -c Release -r linux-x64 --self-contained false -o ./out
```

Or as a self-contained single-file executable:

```bash
dotnet publish MyConnector.csproj -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -o ./out
```

The published output can be placed in any `mcr.microsoft.com/dotnet/runtime:8.0`-based image for self-contained=false, or copied directly for self-contained builds.

---

## Observability

**Structured log fields** present on every log line emitted by the SDK:

| Field | Present on |
|---|---|
| `connector_id` | All lines after HelloAck |
| `invocation_id` | Tool-call lines |
| `agent_key` | Tool-call lines |
| `tool_key` | Tool-call lines |
| `duration_ms` | Tool-call completion |

Set `LOG_LEVEL=Debug` for verbose connection tracing during development.

**Key log events by level:**

- `Information` — `Connected to hub` (with `connector_id`, `namespace`, `max_concurrent`); `stream closed`; `shutdown requested`
- `Warning` — `Hub session ended, reconnecting` (with `delay_ms`, `handshake_completed`, `last_exit`); `GoAway from hub`
- `Error` — `Token rejected`; `register issue`; `session ended` (with exception type + message)

**Heartbeat**: the SDK sends a `Heartbeat` frame every 20 seconds. The hub replies with `HeartbeatAck`. No heartbeat acknowledgement within the idle-timeout window (30 s) causes the hub to send `GoAway{idle}`.

---

## Reconnect + Supervisor

`ConnectorApp.RunAsync()` embeds a supervisor loop. The lifecycle is:

```
supervisor loop
  └── new session
        ├── open gRPC stream
        ├── Hello/HelloAck
        ├── Register/RegisterAck  ← handshake_completed = true
        ├── steady-state (tool calls + heartbeats)
        └── disconnect / GoAway / error
              ↓
        if signal: exit 0
        if token rejected: exit 78 (EX_CONFIG)
        if handshake completed: reset backoff
        sleep(backoff.Next())
        → new session
```

**Backoff schedule**: 1 s → 2 s → 4 s → 8 s → 16 s → 30 s (cap). Each interval has ±20% random jitter. A session that completed handshake before disconnecting resets the backoff to 1 s — hub deploys and network maintenance cause fast reconnect.

SIGTERM during the inter-attempt sleep is caught immediately via `PosixSignalRegistration` installed at the supervisor level.

Token rotation sends `GoAway{token_rotated}` on the active stream. The process exits with code 78. Redeploy with the new token; the supervisor does not retry on exit 78.

---

## Signal Handling

The supervisor installs handlers for `SIGTERM` and `SIGINT` (Ctrl+C) at startup using `PosixSignalRegistration`. On signal receipt:

1. In-flight tool calls are allowed to complete up to their remaining `deadlineMs`.
2. The gRPC stream is half-closed.
3. The process exits with code `0`.

Do not install competing signal handlers in your connector. If your application needs signal hooks, use `CancellationToken` composition before calling `RunAsync`.

---

## Exit Codes

| Code | Meaning |
|---|---|
| `0` | Clean shutdown (SIGTERM, SIGINT, or hub GoAway that is not terminal). |
| `78` | Token rejected (`EX_CONFIG`). A configuration change (new token) is required before retry. |

All other non-zero exit codes indicate an unexpected error. Process managers should restart on non-78 exits.

---

## Deployment Recipes

**Kubernetes** — set `replicas: 1` per connector token; set `VESTED_CONNECTOR_TOKEN` from a Secret; set `terminationGracePeriodSeconds: 45` (longer than the 30 s drain window).

**systemd** — set `VESTED_CONNECTOR_TOKEN` via `EnvironmentFile`; set `Restart=on-failure` and `RestartSec=5`; set `RestartPreventExitStatus=78`.

---

## Troubleshooting

**`connector_unavailable`**
The tool dispatch arrived while the connector was disconnected. Check `Hub session ended, reconnecting` in the connector logs. Verify the supervisor is running and not stuck on exit 78.

**`tool_call_timeout`**
A tool handler exceeded `deadlineMs`. Either increase `DefaultDeadlineMs` in `[Tool(...)]`, or speed up the handler (add timeouts to outbound HTTP calls, cache expensive lookups, etc.).

**`tool_call_invalid_result`**
The handler returned data that does not conform to the declared output schema. Check that the return type of `HandleAsync` matches the declared `TResult`.

## Next

[Upgrading](upgrading.md)
