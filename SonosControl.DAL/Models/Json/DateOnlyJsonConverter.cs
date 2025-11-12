using Newtonsoft.Json;
using System.Globalization;

namespace SonosControl.DAL.Models.Json
{
    public sealed class DateOnlyJsonConverter : JsonConverter<DateOnly>
    {
        private const string Format = "yyyy-MM-dd";

        public override void WriteJson(JsonWriter writer, DateOnly value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToString(Format, CultureInfo.InvariantCulture));
        }

        public override DateOnly ReadJson(JsonReader reader, Type objectType, DateOnly existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.Date:
                    if (reader.Value is DateTime dateTime)
                    {
                        return DateOnly.FromDateTime(dateTime);
                    }
                    break;
                case JsonToken.String:
                    var text = (reader.Value as string)?.Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (DateOnly.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        {
                            return parsed;
                        }

                        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsedDateTime))
                        {
                            return DateOnly.FromDateTime(parsedDateTime);
                        }
                    }
                    break;
                case JsonToken.Null:
                    return default;
            }

            throw new JsonSerializationException($"Unable to convert value '{reader.Value}' to {nameof(DateOnly)}.");
        }
    }
}
