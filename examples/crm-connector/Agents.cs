using VestedAI.ConnectorSdk.Agent;

namespace CrmConnector;

// ---------------------------------------------------------------------------
// crm.sales — Sales operations agent
// ---------------------------------------------------------------------------

/// <summary>
/// Handles day-to-day sales operations: contact look-ups and deal stage updates.
/// </summary>
[Agent(
    Key         = "crm.sales",
    Name        = "Sales Ops",
    Model       = "openai:gpt-4o",
    Description = "Assists account executives with contact data retrieval and deal pipeline management.")]
[Instruction(
    Type     = "system",
    Position = 0,
    Body     = """
        You are a Sales Operations assistant for the CRM connector.

        Your responsibilities:
        - Look up contact details (name, company, lifecycle stage, owner) by email address.
        - Move deals through the pipeline stages: prospect → qualified → proposal → won / lost.

        Always confirm the current deal stage before updating it.
        Never invent data — rely on the tools for all CRM facts.
        """)]
public class SalesOpsAgent { }

// ---------------------------------------------------------------------------
// crm.analytics — Analytics agent
// ---------------------------------------------------------------------------

/// <summary>
/// Provides pipeline analytics: stage-level counts and total deal value summaries.
/// </summary>
[Agent(
    Key         = "crm.analytics",
    Name        = "Analytics",
    Model       = "openai:gpt-4o",
    Description = "Answers pipeline health questions by aggregating open deals across stages and time windows.")]
[Instruction(
    Type     = "system",
    Position = 0,
    Body     = """
        You are a CRM Analytics assistant.

        Your responsibilities:
        - Summarise the open deal pipeline: counts per stage, total value, and trends.
        - Scope summaries to configurable time windows (e.g. last 7 days, last 30 days).

        Interpret "open" as any deal not in won or lost stage.
        Report monetary values in USD.
        When asked about a specific time window, pass it directly to the pipeline_summary tool.
        """)]
public class AnalyticsAgent { }
