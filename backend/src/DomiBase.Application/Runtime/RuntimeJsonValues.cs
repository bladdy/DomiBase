using System.Text.Json;

namespace DomiBase.Application.Runtime;

/// <summary>
/// Converts a JSON request body into column/value pairs. Scalars become invariant text
/// and the SQL builder casts them server-side; objects/arrays become raw JSON.
/// </summary>
public static class RuntimeJsonValues
{
    public static IReadOnlyDictionary<string, string?> ReadObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new RuntimeQueryException(
                400,
                "INVALID_BODY",
                "The request body must be a JSON object.",
                "Send one object per insert or the fields to update.");
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (values.ContainsKey(property.Name))
            {
                throw new RuntimeQueryException(
                    400,
                    "INVALID_BODY",
                    $"Duplicate field '{property.Name}' in the request body.");
            }

            values[property.Name] = ToText(property.Value, property.Name);
        }

        return values;
    }

    /// <summary>
    /// Reads one JSON object or an array of objects (bulk insert). The caller enforces the
    /// maximum row count.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> ReadMany(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return [ReadObject(element)];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new RuntimeQueryException(
                400,
                "INVALID_BODY",
                "The request body must be a JSON object or an array of objects.",
                "Send one object per insert or the fields to update.");
        }

        var rows = new List<IReadOnlyDictionary<string, string?>>();
        foreach (var item in element.EnumerateArray())
        {
            rows.Add(ReadObject(item));
        }

        if (rows.Count == 0)
        {
            throw new RuntimeQueryException(
                400,
                "INVALID_BODY",
                "At least one row is required.");
        }

        return rows;
    }

    private static string? ToText(JsonElement value, string fieldName) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => throw new RuntimeQueryException(
            400,
            "INVALID_BODY",
            $"Field '{fieldName}' has an unsupported JSON value.")
    };
}
