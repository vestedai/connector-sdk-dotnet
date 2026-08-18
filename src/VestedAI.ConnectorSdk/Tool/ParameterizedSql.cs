using System.Collections;
using System.Text.Json;

namespace VestedAI.ConnectorSdk.Tool;

/// <summary>
/// Normalises caller-supplied SQL parameters into values a driver can BIND.
/// </summary>
/// <remarks>
/// <para>
/// The unsafe path is deliberately unreachable: NOTHING here accepts a SQL
/// string. A caller normalises values and hands SQL and parameters separately
/// to its own driver, because a value that reached the SQL text could widen a
/// filter or UNION against another ALLOWED table — which the core's gate would
/// bound but not prevent.
/// </para>
/// <para>
/// Arrays bind as ONE JSON string, expanded by the DATABASE:
/// <c>WHERE [x] IN (SELECT value FROM OPENJSON(@locations))</c>
/// — never by rewriting SQL here.
/// </para>
/// <para>
/// <see cref="Normalise"/> moves VALUES only, and never sanitises, escapes, or
/// reinterprets one. A string containing a quote, a semicolon, or a
/// <c>DROP TABLE</c> comes back byte-identical — the driver's bind is what
/// makes it safe, not this method.
/// </para>
/// </remarks>
public static class ParameterizedSql
{
    /// <summary>
    /// Normalise every value in <paramref name="parameters"/> into something a
    /// driver can bind directly.
    /// </summary>
    /// <remarks>
    /// A scalar (string, number, bool, date/time, <see cref="Guid"/>,
    /// <c>byte[]</c>, or <c>null</c>) passes through unchanged. An
    /// <see cref="IEnumerable"/> (anything else that can be walked — a list,
    /// an array, a set) becomes ONE JSON-string parameter via
    /// <see cref="ToJsonArray"/>, never multiple placeholders and never text
    /// spliced into a statement. Anything that is neither — a nested object,
    /// a dictionary, a POCO — cannot be bound as a single value or expanded
    /// as a flat array, so it is refused rather than silently substituted.
    /// </remarks>
    /// <param name="parameters">
    /// Caller-supplied parameter values, keyed by bind-parameter name.
    /// <c>null</c> is treated as "no parameters" and returns an empty map.
    /// </param>
    /// <returns>A new map with every value normalised. The input is not mutated.</returns>
    /// <exception cref="ArgumentException">
    /// A value is neither a scalar nor a flat enumerable of walkable items —
    /// e.g. a nested object. The message and <see cref="ArgumentException.ParamName"/>
    /// name the offending parameter.
    /// </exception>
    public static IReadOnlyDictionary<string, object?> Normalise(IDictionary<string, object?>? parameters)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (parameters is null)
            return result;

        foreach (var pair in parameters)
            result[pair.Key] = NormaliseValue(pair.Key, pair.Value);

        return result;
    }

    /// <summary>
    /// Serialise <paramref name="values"/> as a JSON array string — the ONE
    /// value a bound array parameter carries. Consumed by the DATABASE side
    /// (e.g. SQL Server's <c>OPENJSON</c>), never expanded into multiple
    /// placeholders or written into SQL text by this SDK.
    /// </summary>
    public static string ToJsonArray(IEnumerable<object?> values)
        => JsonSerializer.Serialize(values);

    private static object? NormaliseValue(string key, object? value)
    {
        if (value is null)
            return null;

        // A dictionary is a nested object, not a bindable sequence — refuse it
        // explicitly before the generic IEnumerable check below, which would
        // otherwise "succeed" by silently JSON-encoding its KeyValuePairs.
        if (value is IDictionary)
            throw Unbindable(key, value);

        if (IsScalar(value))
            return value;

        if (value is IEnumerable enumerable)
            return ToJsonArray(enumerable.Cast<object?>());

        throw Unbindable(key, value);
    }

    private static bool IsScalar(object value) => value switch
    {
        string or bool
            or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or char
            or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan
            or Guid or byte[] => true,
        _ => false,
    };

    private static ArgumentException Unbindable(string key, object value)
        => new(
            $"Parameter '{key}' cannot be bound: a {value.GetType()} is not a " +
            "scalar and not a flat sequence of values. Bind a scalar, or an " +
            "IEnumerable of scalars (sent as one JSON-string parameter).",
            key);
}
