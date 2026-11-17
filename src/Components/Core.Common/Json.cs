using System.Text.Json;
using System.Text.Json.Serialization;

namespace TravelVault.Core.Common;

/// <summary>One JSON setup for storage and sync, so a record looks the same on disk and on the wire.</summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new HybridTimestampConverter());
        return options;
    }
}

/// <summary>Writes a <see cref="HybridTimestamp"/> as its sortable text form.</summary>
public sealed class HybridTimestampConverter : JsonConverter<HybridTimestamp>
{
    public override HybridTimestamp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        HybridTimestamp.TryParse(reader.GetString(), out var value) ? value : HybridTimestamp.Zero;

    public override void Write(Utf8JsonWriter writer, HybridTimestamp value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
