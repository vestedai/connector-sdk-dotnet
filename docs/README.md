# .NET SDK Documentation

## Get Started

- [Quickstart](quickstart.md) — 15-minute walkthrough: install, declare an agent + tool, run the worker, verify in the admin UI
- [Concepts](concepts.md) — mental model: agents, tools, instructions, baselines, overrides, inheritance state machine

## Reference

- [API reference](api.md) — `ConnectorHost`, `[Agent]`, `[Instruction]`, `[Tool]`, `ToolHandler<,>`, `ToolContext`, sensitivity

## Operate

- [Operations](operations.md) — Docker, environment variables, reconnect supervisor, exit codes, signal handling, deployment recipes
- [Upgrading](upgrading.md) — coming from the PHP, Python, or Node SDK; v0.1.0 release notes

## Connector Protocol

- [Protocol overview](protocol/overview.md) — the bidi gRPC stream lifecycle
- [Messages](protocol/messages.md) — every frame, field by field
- [Authentication](protocol/auth.md) — JWT, rotation, revoke
- [Lifecycle](protocol/lifecycle.md) — handshake, heartbeats, drain, reconnect
- [Audit events](protocol/audit.md) — what the hub records
