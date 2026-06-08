using VestedAI.ConnectorSdk.Errors;

namespace CrmConnector;

// ---------------------------------------------------------------------------
// Domain value objects
// ---------------------------------------------------------------------------

/// <summary>A CRM contact record.</summary>
internal sealed record Contact(
    string Email,
    string Name,
    string Company,
    string LifecycleStage, // lead | prospect | customer | churned
    string OwnerEmail
);

/// <summary>A CRM deal record.</summary>
internal sealed record Deal(
    string Id,
    string Name,
    string ContactEmail,
    string Stage,           // prospect | qualified | proposal | won | lost
    decimal Value,          // USD
    DateTime CreatedAt
);

// ---------------------------------------------------------------------------
// Fake in-memory data store
// ---------------------------------------------------------------------------

/// <summary>
/// Deterministic fake dataset used by the CRM tool handlers.
/// All names, email addresses, and company names are clearly fictional.
/// </summary>
internal static class FakeData
{
    // ── Contacts ─────────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<Contact> _contacts = new[]
    {
        new Contact("alice@example-corp.com",    "Alice Nguyen",      "Example Corp",      "customer",  "sales1@crm.example.com"),
        new Contact("bob@sample-industries.com",  "Bob Tarleton",      "Sample Industries", "prospect",  "sales1@crm.example.com"),
        new Contact("carol@demo-widgets.com",     "Carol Petrov",      "Demo Widgets",      "lead",      "sales2@crm.example.com"),
        new Contact("david@fakefinancial.com",    "David Okonkwo",     "Fake Financial",    "customer",  "sales2@crm.example.com"),
        new Contact("eva@placeholder-tech.com",   "Eva Lindqvist",     "Placeholder Tech",  "prospect",  "sales1@crm.example.com"),
        new Contact("frank@noop-solutions.com",   "Frank Abramowitz",  "Noop Solutions",    "churned",   "sales3@crm.example.com"),
    };

    // ── Deals ─────────────────────────────────────────────────────────────────

    // In-memory mutable copy so update_deal_stage can mutate state.
    private static readonly List<MutableDeal> _deals = new()
    {
        new("D-0001", "Example Corp Platform Licence", "alice@example-corp.com",    "won",      48_000m, Ago(days:  90)),
        new("D-0002", "Sample Industries Pilot",       "bob@sample-industries.com",  "qualified", 12_500m, Ago(days:  30)),
        new("D-0003", "Demo Widgets Integration",      "carol@demo-widgets.com",     "proposal",  9_800m,  Ago(days:  14)),
        new("D-0004", "Fake Financial Enterprise",     "david@fakefinancial.com",    "proposal", 75_000m,  Ago(days:  20)),
        new("D-0005", "Fake Financial Renewal",        "david@fakefinancial.com",    "won",      55_000m,  Ago(days: 180)),
        new("D-0006", "Placeholder Tech Starter",      "eva@placeholder-tech.com",   "prospect",  6_200m,  Ago(days:   7)),
        new("D-0007", "Noop Solutions Upgrade",        "frank@noop-solutions.com",   "lost",      3_300m,  Ago(days: 120)),
        new("D-0008", "Example Corp Add-on",           "alice@example-corp.com",     "qualified", 8_400m,  Ago(days:  45)),
        new("D-0009", "Sample Industries Expansion",   "bob@sample-industries.com",  "prospect",  22_000m, Ago(days:   3)),
        new("D-0010", "Placeholder Tech Pro",          "eva@placeholder-tech.com",   "proposal",  18_600m, Ago(days:  10)),
    };

    // ---------------------------------------------------------------------------
    // Lookup helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Find a contact by email address (case-insensitive).
    /// Throws <see cref="ToolValidationException"/> when not found.
    /// </summary>
    public static Contact GetContactByEmail(string email)
    {
        var found = _contacts.FirstOrDefault(
            c => string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase));

        return found ?? throw new ToolValidationException(
            "crm.sales.lookup_contact",
            $"No contact found with email '{email}'.");
    }

    /// <summary>
    /// Find a deal by ID and move it to <paramref name="newStage"/> in-memory.
    /// Returns (previousStage, newStage).
    /// Throws <see cref="ToolValidationException"/> when not found.
    /// </summary>
    public static (string PreviousStage, string NewStage) UpdateDealStage(
        string dealId,
        string newStage)
    {
        var deal = _deals.FirstOrDefault(
            d => string.Equals(d.Id, dealId, StringComparison.OrdinalIgnoreCase));

        if (deal is null)
            throw new ToolValidationException(
                "crm.sales.update_deal_stage",
                $"No deal found with ID '{dealId}'.");

        var previous = deal.Stage;
        deal.Stage = newStage;

        return (previous, newStage);
    }

    /// <summary>
    /// Aggregate open deals created within the last <paramref name="windowDays"/> days.
    /// </summary>
    public static IReadOnlyList<Deal> GetDealsInWindow(int windowDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-windowDays);
        return _deals
            .Where(d => d.CreatedAt >= cutoff)
            .Select(d => new Deal(d.Id, d.Name, d.ContactEmail, d.Stage, d.Value, d.CreatedAt))
            .ToList();
    }

    // ---------------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------------

    private static DateTime Ago(int days) =>
        DateTime.UtcNow.AddDays(-days);

    // Mutable wrapper used to support in-memory stage updates.
    private sealed class MutableDeal(
        string id,
        string name,
        string contactEmail,
        string stage,
        decimal value,
        DateTime createdAt)
    {
        public string Id          { get; } = id;
        public string Name        { get; } = name;
        public string ContactEmail{ get; } = contactEmail;
        public string Stage       { get; set; } = stage;
        public decimal Value      { get; } = value;
        public DateTime CreatedAt { get; } = createdAt;
    }
}
