using System.Text.RegularExpressions;

namespace VestedAI.ConnectorSdk.Schema;

/// <summary>
/// A Business Central physical table name decomposed into its three parts.
/// </summary>
/// <remarks>
/// BC names base and extension tables as
/// <c>&lt;Company&gt;$&lt;LogicalName&gt;$&lt;ExtensionAppId&gt;</c>. Measured
/// against production on 2026-08-12: 26,191 base tables, 8 companies, 2,931
/// distinct logical names, 55 extension app ids.
///
/// Company names contain spaces and hyphens ("ASG - HData",
/// "ASG CLEAN - 20221230", "CRONUS - LS Central"). A \w+ company pattern
/// matches 3,249 of 26,191 rows — 12% — and reports success on every one of
/// them, so the failure is silent. The company segment is therefore ".+?" and
/// the strict GUID suffix is what anchors the parse.
/// </remarks>
public readonly record struct BcPhysicalName(string Company, string LogicalName, string ExtensionAppId)
{
    private static readonly Regex Pattern = new(
        @"^(?<company>.+?)\$(?<logical>.+)\$(?<ext>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string tableName, out BcPhysicalName parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(tableName))
        {
            return false;
        }

        var m = Pattern.Match(tableName);
        if (!m.Success)
        {
            return false;
        }

        parsed = new BcPhysicalName(
            m.Groups["company"].Value,
            m.Groups["logical"].Value,
            m.Groups["ext"].Value);

        return true;
    }
}
