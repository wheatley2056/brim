using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brim.Core.Plugins;

/// <summary>JSON-Einstellungen des Plugin-Protokolls: camelCase, Aufzählungen als Text, Tupel als Arrays.</summary>
public static class PluginJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 48,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                new PaddingConverter(),
                new GradientConverter(),
            },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Innenabstand als <c>[links, oben, rechts, unten]</c>; eine Zahl gilt für alle Seiten.</summary>
    private sealed class PaddingConverter : JsonConverter<(double Left, double Top, double Right, double Bottom)>
    {
        public override (double Left, double Top, double Right, double Bottom) Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                var all = reader.GetDouble();
                return (all, all, all, all);
            }
            var values = JsonSerializer.Deserialize<double[]>(ref reader, options) ?? [];
            return values.Length switch
            {
                0 => default,
                1 => (values[0], values[0], values[0], values[0]),
                2 => (values[0], values[1], values[0], values[1]),
                4 => (values[0], values[1], values[2], values[3]),
                _ => throw new JsonException("padding braucht 1, 2 oder 4 Werte"),
            };
        }

        public override void Write(Utf8JsonWriter writer, (double Left, double Top, double Right, double Bottom) value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.Left);
            writer.WriteNumberValue(value.Top);
            writer.WriteNumberValue(value.Right);
            writer.WriteNumberValue(value.Bottom);
            writer.WriteEndArray();
        }
    }

    /// <summary>Farbverlauf als <c>["#von", "#bis"]</c>.</summary>
    private sealed class GradientConverter : JsonConverter<(string From, string To)>
    {
        public override (string From, string To) Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var values = JsonSerializer.Deserialize<string[]>(ref reader, options) ?? [];
            return values.Length == 2 ? (values[0], values[1]) : throw new JsonException("placeholder braucht zwei Farben");
        }

        public override void Write(Utf8JsonWriter writer, (string From, string To) value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(value.From);
            writer.WriteStringValue(value.To);
            writer.WriteEndArray();
        }
    }
}
