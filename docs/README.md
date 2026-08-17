# .NET SDK Documentation

## Get Started

- [Quickstart](quickstart.md) — 15-minute walkthrough: install, declare an agent + tool, run the worker, verify in the admin UI
- [Concepts](concepts.md) — mental model: agents, tools, instructions, baselines, overrides, inheritance state machine

## Reference

- [API reference](api.md) — `ConnectorHost`, `[Agent]`, `[Instruction]`, `[Tool]`, `ToolHandler<,>`, `ToolContext`, sensitivity
- [Large datasets](large-datasets.md) — `PaginatedToolHandler<,>`: tools whose result sets exceed the LLM context; sample + `dataset_ref`, on-demand full export, migration checklist

- [Per-user credentials](credentials.md) — act on behalf of the calling user: sealed credentials the platform cannot read, validation, key rotation

- [Relational schema intelligence](schema.md) — declare a database the platform can index: the canonical describe contract, scopes, extraction, the SQL gate

## Operate

- [Operations](operations.md) — Docker, environment variables, reconnect supervisor, exit codes, signal handling, deployment recipes
- [Upgrading](upgrading.md) — coming from the PHP, Python, or Node SDK; v0.1.0 release notes

## Connector Protocol

- [Protocol overview](protocol/overview.md) — the bidi gRPC stream lifecycle
- [Messages](protocol/messages.md) — every frame, field by field
- [Authentication](protocol/auth.md) — JWT, rotation, revoke
- [Lifecycle](protocol/lifecycle.md) — handshake, heartbeats, drain, reconnect
- [Audit events](protocol/audit.md) — what the hub records
