# Concepts

## Agents

An agent is the unit of work the LLM acts through. Each agent has a model (provider + name + config), a set of ordered instruction blocks, and a set of tools. The connector declares the baseline agent shape; the hub persists it and creates an `AgentVersion`.

An agent key must start with the connector's namespace (e.g., `myapp.products`). Keys are stable identifiers — the admin UI uses them to surface the agent and its version history.

The first registration of an agent auto-publishes the resulting `AgentVersion`. Subsequent changes to the baseline produce a draft version that the admin must review and publish.

## Tools

A tool is a function definition the LLM may call. It carries:

- An input JSON Schema (auto-generated from the `Args` POCO via NJsonSchema, or provided manually via `InputSchema`).
- An output JSON Schema (auto-generated from the `Result` POCO via NJsonSchema if `TResult` is declared).
- A `ToolHandler<TArgs, TResult>` subclass that does the actual work.
- A `DefaultDeadlineMs` (default 30 000) and `MaxResultBytes` (default 1 MiB).

Tool calls are request/response in v1. The handler receives already-deserialized `args` (typed to the `TArgs` POCO) and a `ToolContext` carrying the caller's identity. It returns a `TResult` value serialized to JSON by the SDK; the hub validates the result against the output schema before passing it back to the runtime.

Tool keys must also be namespaced: `myapp.products.search`, not `search`.

`[Description]` attributes on `Args` properties flow directly into the generated `input_schema_json`, so the LLM sees field descriptions when deciding which arguments to supply.

## Sensitivity

Each tool may declare a `Sensitivity` to tell the hub how to classify its side-effects. Allowed values: `read`, `write`, `destructive`, `external_call`, `medium`. If omitted or empty, the hub defaults it to `external_call`. Admins can override the effective value from the admin UI regardless of what the connector declares.

```csharp
[Tool(Key = "myapp.orders.delete", Description = "Permanently deletes an order.",
      Sensitivity = "destructive")]
public class DeleteOrder : ToolHandler<DeleteOrder.Args, DeleteOrder.Result> { ... }
```

See `Sensitivity.ToolSensitivities` for the canonical list at runtime.

## Instructions

Instructions are prompt segments injected into the agent's system prompt at runtime. Each instruction has a `Type` (`system`, `task`, `persona`, `safety`), a `Position` (integer, ascending order), a `Body`, and a `Format` (`markdown`, `jinja`, `plain`).

At compose time, the `SystemPromptComposer` iterates instructions in position order, resolves each one through the inheritance state (see below), and concatenates the results. Org-wide shared instructions append at the end.

## Baselines vs. Overrides

The connector owns the **baseline**: the canonical instruction bodies, tool schemas, and model selection. The admin can **override** any instruction body or disable any tool, but cannot modify tool schemas directly (the connector owns the execution contract).

When the connector connects and sends a `Register` frame, the hub computes a fingerprint over the entire declaration. If the fingerprint matches the stored baseline, registration is a no-op (common on reconnect). If it differs, the hub calls `ConnectorRegistry::reconcile()`, which creates a new `connector_baseline_*` snapshot and — for existing agents — creates a new draft `AgentVersion` with overrides re-applied on top of the new baseline.

## Inheritance State Machine

Each instruction pivot row (`agent_instructions`) carries an `inheritance_state` that controls what `SystemPromptComposer` uses at runtime:

```
                  ┌─────────────────────────────────────────┐
   first push     │                                         │
   ──────────►  inherit ──── admin edits ──►  replaced     │
                  │                                         │
                  │          admin disables ►  disabled     │
                  │                                         │
                  │          admin adds new ►  admin_added  │
                  │                                         │
                  └── connector drops position ► orphaned  │
                                                            │
                  (new baseline push re-links all states    │
                   except orphaned)                         │
                  └─────────────────────────────────────────┘
```

**`inherit`** — The admin has not touched this row. At compose time, the runtime reads the body from `connector_baseline_instructions`. Default state on first registration.

**`replaced`** — The admin wrote a custom body. At compose time, the runtime uses the admin's `instructions.body`.

**`disabled`** — The admin suppressed this instruction. `SystemPromptComposer` skips the position entirely.

**`admin_added`** — The admin added an instruction with no baseline counterpart. Not touched by reconciliation.

**`orphaned`** — A `replaced` or `disabled` override remains, but the connector no longer declares a baseline at this position. `SystemPromptComposer` skips orphaned rows.

## Reconciliation

When the connector pushes a new baseline (different fingerprint):

1. Hub short-circuits on fingerprint match — no database hop.
2. On mismatch, `ConnectorRegistry::reconcile()` runs in a transaction.
3. For each agent in the new baseline, if an `Agent` row already exists, a new draft `AgentVersion` is created with overrides re-applied on top of the new baseline.
4. For each agent absent from the new baseline, `Agent.status` flips to `inactive`.
5. New agents in the baseline are created and published immediately.

## Next

[API reference](api.md)
