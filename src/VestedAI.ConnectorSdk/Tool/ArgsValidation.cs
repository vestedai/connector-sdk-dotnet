using System.Text.Json;
using VestedAI.ConnectorSdk.Errors;

namespace VestedAI.ConnectorSdk.Tool;

/// <summary>
/// Parses incoming tool-call args JSON into a strongly-typed POCO.
/// </summary>
public static class ArgsValidation
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Deserialize <paramref name="json"/> into the POCO type declared on
    /// <paramref name="decl"/>. Returns the boxed args object.
    /// </summary>
    /// <exception cref="ToolValidationException">
    /// Thrown when the JSON is null, malformed, or cannot be deserialized into the args type.
    /// </exception>
    public static object Parse(ToolDeclaration decl, ReadOnlySpan<byte> json)
    {
        object? result;
        try
        {
            result = JsonSerializer.Deserialize(json, decl.ArgsType, _options);
        }
        catch (JsonException ex)
        {
            throw new ToolValidationException(
                decl.Key,
                $"args is not valid JSON: {ex.Message}",
                ex);
        }

        if (result is null)
        {
            throw new ToolValidationException(
                decl.Key,
                "args deserialized to null");
        }

        return result;
    }
}
