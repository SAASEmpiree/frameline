using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FrameLine.Core.Mcp;

/// JSON helpers over System.Text.Json.Nodes. Output is deterministic (object keys sorted, ordinal),
/// slashes and non-ASCII characters are not escaped, like the Swift JSONSerialization options.
public static class McpJson
{
    private static readonly JsonWriterOptions CompactWriter = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private static readonly JsonWriterOptions PrettyWriter = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
    };

    public static byte[] Bytes(JsonNode? node) => Serialize(node, CompactWriter);

    public static string String(JsonNode? node) => Encoding.UTF8.GetString(Bytes(node));

    public static string Pretty(JsonNode? node) => Encoding.UTF8.GetString(Serialize(node, PrettyWriter));

    private static byte[] Serialize(JsonNode? node, JsonWriterOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options)) WriteSorted(writer, node);
        return stream.ToArray();
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    WriteSorted(writer, pair.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) WriteSorted(writer, item);
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    /// Parses a request body. Throws JsonException on malformed JSON, including duplicate keys
    /// (which System.Text.Json.Nodes only reports lazily, so the tree is walked here).
    public static JsonNode? Parse(ReadOnlySpan<byte> utf8)
    {
        var node = JsonNode.Parse(utf8, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        try { Materialize(node); }
        catch (ArgumentException e) { throw new JsonException(e.Message, e); }
        return node;
    }

    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj) Materialize(pair.Value);
                break;
            case JsonArray array:
                foreach (var item in array) Materialize(item);
                break;
        }
    }

    public static JsonValueKind Kind(JsonNode? node) => node is null ? JsonValueKind.Null : node.GetValueKind();

    /// A JSON number as Int, refusing booleans, fractions, non-finite and huge values.
    public static int? Int(JsonNode? node)
    {
        if (Double(node) is not double d) return null;
        if (d != Math.Round(d) || Math.Abs(d) >= 1e12) return null;
        if (d > int.MaxValue || d < int.MinValue) return null;
        return (int)d;
    }

    public static long? Long(JsonNode? node)
    {
        if (Double(node) is not double d) return null;
        if (d != Math.Round(d) || Math.Abs(d) >= 1e12) return null;
        return (long)d;
    }

    public static double? Double(JsonNode? node)
    {
        if (Kind(node) != JsonValueKind.Number) return null;
        // ToJsonString works whatever CLR type backs the value (parsed element or created int/double).
        if (!double.TryParse(node!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return null;
        return double.IsFinite(d) ? d : null;
    }

    public static bool? Bool(JsonNode? node) => Kind(node) switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static string? Str(JsonNode? node) =>
        Kind(node) == JsonValueKind.String ? node!.GetValue<string>() : null;

    /// Reads one property from an object, null when absent or not an object.
    public static JsonNode? Get(JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;
}
