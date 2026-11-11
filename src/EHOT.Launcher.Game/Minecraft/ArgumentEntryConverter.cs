using System.Text.Json;
using System.Text.Json.Serialization;

namespace EHOT.Launcher.Game.Minecraft;

/// <summary>Ручной конвертер <see cref="ArgumentEntry"/>: строка либо объект с rules.</summary>
public sealed class ArgumentEntryConverter : JsonConverter<ArgumentEntry>
{
    public override ArgumentEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new ArgumentEntry { Value = reader.GetString() };

            case JsonTokenType.StartObject:
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;
                if (root.TryGetProperty("value", out var value))
                {
                    var entry = new ArgumentEntry
                    {
                        Rules = root.TryGetProperty("rules", out var rules)
                            ? rules.Deserialize<List<RuleDto>>(options) ?? []
                            : []
                    };

                    if (value.ValueKind == JsonValueKind.String)
                    {
                        return new ArgumentEntry { Value = value.GetString(), Rules = entry.Rules };
                    }

                    if (value.ValueKind == JsonValueKind.Array)
                    {
                        return new ArgumentEntry { Values = value.EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToList(), Rules = entry.Rules };
                    }

                    return entry;
                }

                return new ArgumentEntry();
            }

            default:
                throw new JsonException($"Неожиданный токен аргумента: {reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, ArgumentEntry value, JsonSerializerOptions options)
    {
        if (value.Value is not null)
        {
            writer.WriteStringValue(value.Value);
        }
        else
        {
            writer.WriteStartArray();
            foreach (var v in value.Values)
            {
                writer.WriteStringValue(v);
            }

            writer.WriteEndArray();
        }
    }
}
