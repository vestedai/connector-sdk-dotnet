using System.ComponentModel;
using VestedAI.ConnectorSdk.Tool;

namespace CrmConnector;

// ---------------------------------------------------------------------------
// crm.sales.lookup_contact
// ---------------------------------------------------------------------------

/// <summary>
/// Finds a CRM contact by email address and returns their full profile.
/// </summary>
[Tool(
    Key         = "crm.sales.lookup_contact",
    Description = "Look up a CRM contact by email address. Returns name, company, lifecycle stage, and the owning sales rep.",
    Sensitivity = "read")]
public class LookupContact : ToolHandler<LookupContact.Args, LookupContact.Result>
{
    public class Args
    {
        [Description("Email address of the contact to look up.")]
        public string Email { get; set; } = "";
    }

    public class Result
    {
        [Description("Full name of the contact.")]
        public string Name { get; set; } = "";

        [Description("Company the contact belongs to.")]
        public string Company { get; set; } = "";

        [Description("CRM lifecycle stage: lead, prospect, customer, or churned.")]
        public string LifecycleStage { get; set; } = "";

        [Description("Email of the sales rep who owns this contact.")]
        public string OwnerEmail { get; set; } = "";
    }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
    {
        // Throws ToolValidationException when not found — dispatcher converts to an error reply.
        var contact = FakeData.GetContactByEmail(args.Email);

        return Task.FromResult(new Result
        {
            Name           = contact.Name,
            Company        = contact.Company,
            LifecycleStage = contact.LifecycleStage,
            OwnerEmail     = contact.OwnerEmail,
        });
    }
}

// ---------------------------------------------------------------------------
// crm.sales.update_deal_stage
// ---------------------------------------------------------------------------

/// <summary>
/// Moves a deal to a new pipeline stage and returns the transition.
/// </summary>
[Tool(
    Key         = "crm.sales.update_deal_stage",
    Description = "Move a deal to a new pipeline stage. Returns the previous stage and the new stage. Idempotent if the deal is already at the target stage.",
    Sensitivity = "write")]
public class UpdateDealStage : ToolHandler<UpdateDealStage.Args, UpdateDealStage.Result>
{
    /// <summary>Valid pipeline stages accepted by this tool.</summary>
    public static readonly string[] ValidStages =
        { "prospect", "qualified", "proposal", "won", "lost" };

    public class Args
    {
        [Description("Unique deal identifier (e.g. D-0001).")]
        public string DealId { get; set; } = "";

        [Description("Target pipeline stage. One of: prospect, qualified, proposal, won, lost.")]
        public string NewStage { get; set; } = "";
    }

    public class Result
    {
        [Description("Unique deal identifier.")]
        public string DealId { get; set; } = "";

        [Description("Pipeline stage before the update.")]
        public string PreviousStage { get; set; } = "";

        [Description("Pipeline stage after the update.")]
        public string NewStage { get; set; } = "";
    }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
    {
        // Validate the target stage before touching data.
        if (!Array.Exists(ValidStages,
                s => string.Equals(s, args.NewStage, StringComparison.OrdinalIgnoreCase)))
        {
            throw new VestedAI.ConnectorSdk.Errors.ToolValidationException(
                "crm.sales.update_deal_stage",
                $"Invalid stage '{args.NewStage}'. Valid stages: {string.Join(", ", ValidStages)}.");
        }

        // FakeData.UpdateDealStage throws ToolValidationException when DealId is unknown.
        var (previous, updated) = FakeData.UpdateDealStage(args.DealId, args.NewStage);

        return Task.FromResult(new Result
        {
            DealId        = args.DealId,
            PreviousStage = previous,
            NewStage      = updated,
        });
    }
}

// ---------------------------------------------------------------------------
// crm.analytics.pipeline_summary
// ---------------------------------------------------------------------------

/// <summary>
/// Aggregates open deals by stage over a rolling time window.
/// </summary>
[Tool(
    Key         = "crm.analytics.pipeline_summary",
    Description = "Summarise the open deal pipeline for a rolling time window. Returns per-stage deal counts, per-stage total value (USD), and overall open-deal totals.",
    Sensitivity = "read")]
public class PipelineSummary : ToolHandler<PipelineSummary.Args, PipelineSummary.Result>
{
    public class Args
    {
        [Description("Rolling window in days to include (1–365). Defaults to 30.")]
        public int WindowDays { get; set; } = 30;
    }

    public class Result
    {
        [Description("Window size that was used, in days.")]
        public int WindowDays { get; set; }

        [Description("Number of deals per stage within the window.")]
        public Dictionary<string, int> CountByStage { get; set; } = new();

        [Description("Total deal value (USD) per stage within the window.")]
        public Dictionary<string, decimal> ValueByStage { get; set; } = new();

        [Description("Total number of open deals (not won or lost) within the window.")]
        public int TotalOpenDeals { get; set; }

        [Description("Combined value (USD) of all open deals within the window.")]
        public decimal TotalOpenValue { get; set; }
    }

    private static readonly HashSet<string> _closedStages =
        new(StringComparer.OrdinalIgnoreCase) { "won", "lost" };

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
    {
        var windowDays = Math.Clamp(args.WindowDays, 1, 365);
        var deals = FakeData.GetDealsInWindow(windowDays);

        var countByStage  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var valueByStage  = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var deal in deals)
        {
            countByStage[deal.Stage]  = countByStage.GetValueOrDefault(deal.Stage) + 1;
            valueByStage[deal.Stage]  = valueByStage.GetValueOrDefault(deal.Stage) + deal.Value;
        }

        var openDeals = deals.Where(d => !_closedStages.Contains(d.Stage)).ToList();

        return Task.FromResult(new Result
        {
            WindowDays     = windowDays,
            CountByStage   = new Dictionary<string, int>(countByStage),
            ValueByStage   = new Dictionary<string, decimal>(valueByStage),
            TotalOpenDeals = openDeals.Count,
            TotalOpenValue = openDeals.Sum(d => d.Value),
        });
    }
}
