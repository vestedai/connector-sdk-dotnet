# Large Datasets — `PaginatedToolHandler<TArgs, TRow>`

*Available from SDK **v0.3.0**.*

Some tools return result sets that do not fit in an LLM context window —
*"all POS transactions today"* is thousands of rows. A plain
`ToolHandler<TArgs, TResult>` must return one atomic JSON result under the
tool's `MaxResultBytes` cap, so it simply cannot serve such a query.

`PaginatedToolHandler<TArgs, TRow>` is the SDK's answer. You implement **one
method** — fetch a single page of rows — and the platform does the rest:

- **The agent never sees the full set.** On a call, the platform fetches
  page 1, shows the LLM a bounded **sample (≤ 20 rows)** plus a
  `dataset_ref` handle and the total count.
- **Full data on demand.** When the user needs the complete set, the agent
  calls the platform's `materialize_dataset` tool: the platform replays your
  `FetchPageAsync` page by page (under org-configurable caps), builds a CSV,
  and delivers it as a downloadable file **and** as a code-interpreter input
  for full-set analysis — in the same conversation turn.
- **Governance built in.** Row/byte/page/time caps, a confirm gate for very
  large exports (the agent must confirm with the user above a row
  threshold), a structured audit event per materialization, and snapshot
  GC — all platform-side. Your handler stays a dumb page reader.

## When to use it

| Situation | Use |
|---|---|
| Single record or small fixed result ("look up order 1234") | `ToolHandler<TArgs, TResult>` |
| A list that is always small and bounded (≤ a few dozen rows) | `ToolHandler` returning the list |
| A list that **can** be large — report-style queries, "all X", filtered scans whose size depends on the filter | **`PaginatedToolHandler<TArgs, TRow>`** |

Rule of thumb: if you would need `LIMIT`/`TOP` to keep the response safe,
it should be a paginated tool. Do not cap silently inside a `ToolHandler` —
a truncated list the agent believes is complete is worse than an error.

## The contract

```csharp
public abstract class PaginatedToolHandler<TArgs, TRow> : ToolHandlerBase
{
    public abstract Task<DatasetPage<TRow>> FetchPageAsync(
        TArgs args, DatasetCursor cursor, ToolContext ctx);
}

public sealed class DatasetCursor
{
    public string? Token   { get; init; }  // null on the FIRST page; otherwise your previous NextCursor
    public int     PageSize { get; init; } // requested rows per page; 0 = your default
}

public sealed class DatasetPage<TRow>
{
    public required IReadOnlyList<TRow> Rows { get; init; } // never null; may be empty on the last page
    public string? NextCursor { get; init; }                // null = this was the LAST page
    public long?   Total      { get; init; }                // total matching rows, if known; null = unknown
}
```

- **`TArgs`** — your query arguments POCO, exactly like a normal tool.
  Do **not** add page/offset/cursor properties to it: pagination arrives via
  `DatasetCursor`, outside the LLM-visible schema. The LLM never manages
  the cursor.
- **`TRow`** — the POCO for **one row**. The SDK derives the tool's
  `output_schema` from `TRow` (one row, not the array). Use
  `[Description]` on its properties — those descriptions surface to the LLM
  and to the CSV/table consumers.
- **`cursor.Token` is opaque to the platform.** It round-trips your
  `NextCursor` verbatim. Use whatever suits your backend: a numeric offset,
  a keyset (`"2026-07-01T00:00:00Z|48213"`), or an ERP continuation token
  (OData `@odata.nextLink`, Business Central bookmark) — the platform never
  parses it.
- **`cursor.PageSize`** is the platform's requested page size (the org's
  `dataset.page_size` setting, default 200). Honor it when you can;
  clamp to your backend's maximum when you must. `0` means pick your own
  default.

## Correctness invariants (read these twice)

The platform replays your pages sequentially to build the full export. Three
things must hold or the export will be silently wrong:

1. **Stable order.** The underlying query must have a deterministic `ORDER BY`
   (a unique key, or a timestamp + unique tiebreaker). Unordered SQL pages
   can repeat or skip rows between pages.
2. **`NextCursor` null exactly on the last page.** Returning `null` too early
   truncates the export; never returning it loops until the platform's page
   cap trips and the export is flagged truncated.
3. **`Total` is the true match count when you report it.** If your backend
   can `COUNT(*)` cheaply, return it — the platform uses it for the LLM's
   `total_estimate` and for the confirm-before-huge-export gate. If counting
   is expensive, return `null` (unknown) rather than a guess.

Pages should also be **idempotent** — replaying the same `(args, token)`
should return the same rows. Snapshot-consistent backends give you this for
free; for live tables, accept that a row inserted mid-export may appear or
not (that is fine; duplicating or skipping *existing* rows is not).

## Full example — ERP POS transactions

An offset-based cursor against a SQL-style backend. This is the shape most
existing `run_sql` / report tools convert to:

```csharp
using System.ComponentModel;
using VestedAI.ConnectorSdk.Tool;

/// <summary>
/// POS transactions for a date range as a large dataset: the agent sees a
/// sample + a dataset_ref; the full set is exportable / analyzable on demand.
/// </summary>
[Tool(
    Key         = "erp.retail.pos_transactions",
    Description = "List POS transactions in a date range as a dataset — returns a sample plus a dataset_ref to export or compute over the full set. Use for any transaction-list request; the platform handles size.",
    Sensitivity = "read")]
public class PosTransactions : PaginatedToolHandler<PosTransactions.Args, PosTransactions.Row>
{
    public class Args
    {
        [Description("Start date (inclusive), ISO 8601, e.g. 2026-07-01.")]
        public string From { get; set; } = "";

        [Description("End date (inclusive), ISO 8601.")]
        public string To { get; set; } = "";

        [Description("Optional store code to filter by. Empty = all stores.")]
        public string Store { get; set; } = "";
    }

    public class Row
    {
        [Description("Transaction number.")]
        public string TransactionNo { get; set; } = "";

        [Description("Store code.")]
        public string Store { get; set; } = "";

        [Description("Transaction timestamp, ISO 8601.")]
        public string At { get; set; } = "";

        [Description("Total amount in SAR.")]
        public decimal AmountSar { get; set; }

        [Description("Payment method: cash, card, or wallet.")]
        public string Payment { get; set; } = "";
    }

    private readonly IPosRepository _repo;
    public PosTransactions(IPosRepository repo) => _repo = repo;

    public override async Task<DatasetPage<Row>> FetchPageAsync(
        Args args, DatasetCursor cursor, ToolContext ctx)
    {
        // Cursor token = row offset. Null token means the first page.
        var offset   = cursor.Token is null ? 0 : int.Parse(cursor.Token);
        var pageSize = cursor.PageSize > 0 ? cursor.PageSize : 200;

        // COUNT is cheap here, so report the true total (drives the LLM's
        // total_estimate and the platform's confirm-before-huge-export gate).
        var total = await _repo.CountTransactionsAsync(args.From, args.To, args.Store);

        // INVARIANT: deterministic ORDER BY (timestamp + transaction_no
        // tiebreaker) so sequential pages never skip or duplicate rows.
        var rows = await _repo.GetTransactionsPageAsync(
            args.From, args.To, args.Store, offset, pageSize);

        var next = offset + rows.Count < total
            ? (offset + rows.Count).ToString()   // more pages remain
            : null;                              // null = last page

        return new DatasetPage<Row>
        {
            Rows       = rows.Select(t => new Row
            {
                TransactionNo = t.No,
                Store         = t.Store,
                At            = t.At.ToString("O"),
                AmountSar     = t.Amount,
                Payment       = t.Payment,
            }).ToList(),
            NextCursor = next,
            Total      = total,
        };
    }
}
```

### Keyset / continuation-token variant

When offsets are unstable or expensive (large OData sets, Business Central
APIs), carry the backend's own continuation token:

```csharp
public override async Task<DatasetPage<Row>> FetchPageAsync(
    Args args, DatasetCursor cursor, ToolContext ctx)
{
    // Token is the backend's continuation link, verbatim. The platform
    // round-trips it; only you interpret it.
    var page = await _client.GetPageAsync(
        filter: BuildFilter(args),
        continuation: cursor.Token,          // null = first page
        top: cursor.PageSize > 0 ? cursor.PageSize : 200);

    return new DatasetPage<Row>
    {
        Rows       = page.Items.Select(Map).ToList(),
        NextCursor = page.ContinuationToken,  // backend returns null when exhausted
        Total      = null,                    // COUNT not cheap here → report unknown
    };
}
```

## What the agent experiences

First call (the platform fetches page 1 only):

```json
{
  "sample": [ /* ≤ 20 rows */ ],
  "row_schema": { /* derived from TRow */ },
  "total_estimate": 11873,
  "dataset_ref": "ds_01KWHJY7K8GAW1KJHDGKVE7HXX",
  "_note": "Showing a sample of the rows. Call materialize_dataset(dataset_ref) to get the full set as a file or to compute over all rows."
}
```

If the user wants the full set, the agent calls
`materialize_dataset(dataset_ref)` — the platform then replays your
`FetchPageAsync` sequentially (cursor chain) under the org's caps
(`dataset.max_pages` / `max_rows` / `max_bytes` /
`materialize_deadline_sec`; defaults 200 pages / 100k rows / 32 MiB / 120 s)
and produces the CSV + code-interpreter file. Above
`dataset.confirm_threshold_rows` (default 5000) the agent must pass
`confirm_full: true`, which it does after confirming with the user. Every
materialization writes a `dataset_materialized` audit event.

## Practical notes

- **`MaxResultBytes`:** keep the default (1 MiB). It bounds the **sample
  page** the platform shows the LLM, not the full export (materialize pages
  are bounded by the org's export budget instead). Do not set small values
  like 32 KB on a paginated tool — a default 200-row page will not fit and
  the first sampled page can be rejected.
- **Deadline:** `DefaultDeadlineMs` applies **per page**, not per export.
  30 s (the default) is usually right; raise it only if a single page query
  is genuinely slow.
- **`HandleAsync` does not exist here.** Single-style invocation on a
  paginated tool throws `NotSupportedException` by design — the dispatcher
  always drives `FetchPageAsync`.
- **Description matters for tool selection.** Say what the tool covers and
  that it returns a dataset (sample + `dataset_ref`). If you keep a
  non-paginated sibling for the same data, agents tend to pick it and
  manually paginate — retire the sibling or scope its description to
  small, known lookups only.

## Migration checklist — converting an existing tool

1. Change the base class:
   `ToolHandler<Args, Result>` → `PaginatedToolHandler<Args, Row>` where
   `Row` is the **element type** of the old result's list (drop the wrapper
   `{ items, total_count }` type — the platform owns the envelope now).
2. Remove any `limit` / `offset` / `page` properties from `Args` — the
   cursor replaces them and the LLM must not see them.
3. Replace `HandleAsync` with `FetchPageAsync`; move the query behind a
   deterministic `ORDER BY`; wire `cursor.Token`/`PageSize` as above.
4. Return `Total` when a cheap count exists; otherwise `null`.
5. Remove `MaxResultBytes` overrides (or reset to the 1 MiB default).
6. Update the `[Tool]` `Description` to say it returns a dataset.
7. Test: page-1 shape, mid-page `NextCursor` round-trip, last-page
   `NextCursor == null`, and (if you report it) `Total` correctness — see
   `tests/VestedAI.ConnectorSdk.Tests/PaginatedToolHandlerTests.cs` for the
   harness pattern.
8. Redeploy the connector; the SDK re-registers the tool with
   `result_kind = ROWSET` automatically — no extra declaration needed.

## Requirements

- SDK **v0.3.0** or later (`dotnet add package VestedAI.ConnectorSdk --version 0.3.0`).
- Platform-side support (paginated sampling + `materialize_dataset`) is live
  on the Vested AI core as of 2026-07.
