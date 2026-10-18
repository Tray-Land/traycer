using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrayCer.Core.Json;

/// <summary>
/// Forgiving reads over <see cref="JsonNode"/>. Printer firmware is inconsistent: the same field
/// arrives as 42, 42.0 or "42.00" depending on model and version, and missing fields are normal.
/// Every accessor returns null instead of throwing, so a parser reads like a list of fields.
/// </summary>
internal static class JsonRead
{
    public static JsonNode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Walks object keys (or array indices given as digits) and returns the node there.</summary>
    public static JsonNode? At(this JsonNode? node, params ReadOnlySpan<string> path)
    {
        foreach (string key in path)
        {
            node = node switch
            {
                JsonObject obj => obj.TryGetPropertyValue(key, out JsonNode? child) ? child : null,
                JsonArray array when int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < array.Count => array[i],
                _ => null,
            };
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    public static double? Number(this JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.Number when value.TryGetValue(out double d):
                return double.IsFinite(d) ? d : null;
            case JsonValueKind.String when value.TryGetValue(out string? s):
                return double.TryParse(s.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)
                    ? parsed
                    : null;
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            default:
                return null;
        }
    }

    public static double? Number(this JsonNode? node, params ReadOnlySpan<string> path) => node.At(path).Number();

    public static int? Int(this JsonNode? node, params ReadOnlySpan<string> path) =>
        node.At(path).Number() is { } d && d >= int.MinValue && d <= int.MaxValue ? (int)Math.Round(d) : null;

    public static long? Long(this JsonNode? node, params ReadOnlySpan<string> path) =>
        node.At(path).Number() is { } d && d >= long.MinValue && d <= long.MaxValue ? (long)Math.Round(d) : null;

    public static string? Text(this JsonNode? node, params ReadOnlySpan<string> path)
    {
        if (node.At(path) is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    /// <summary>Like <see cref="Text"/> but treats empty and whitespace strings as missing.</summary>
    public static string? NonEmpty(this JsonNode? node, params ReadOnlySpan<string> path) =>
        node.Text(path) is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    public static bool? Flag(this JsonNode? node, params ReadOnlySpan<string> path)
    {
        if (node.At(path) is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.Number() is { } d && d != 0,
            JsonValueKind.String => value.GetValue<string>().Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on" => true,
                "false" or "0" or "no" or "off" => false,
                _ => null,
            },
            _ => null,
        };
    }

    public static JsonObject? Object(this JsonNode? node, params ReadOnlySpan<string> path) => node.At(path) as JsonObject;

    public static JsonArray? Array(this JsonNode? node, params ReadOnlySpan<string> path) => node.At(path) as JsonArray;

    /// <summary>
    /// Merges <paramref name="update"/> into <paramref name="target"/>: objects merge key by key,
    /// everything else (arrays included) replaces. Push protocols (Bambu, Creality, Elegoo) send
    /// only what changed, so the driver keeps one merged document and reads it like a full report.
    /// </summary>
    public static void MergeInto(JsonObject target, JsonObject update)
    {
        foreach ((string key, JsonNode? value) in update.ToArray())
        {
            if (value is JsonObject incoming && target[key] is JsonObject existing)
            {
                MergeInto(existing, incoming);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>The last segment of a path, for printers that report "/usr/data/gcodes/benchy.gcode".</summary>
    public static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string trimmed = path.Trim().TrimEnd('/', '\\');
        int slash = trimmed.LastIndexOfAny(['/', '\\']);
        string name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return name.Length == 0 ? null : name;
    }
}
