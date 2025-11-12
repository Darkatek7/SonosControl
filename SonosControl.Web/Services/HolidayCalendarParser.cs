using System.Globalization;
using System.IO;

namespace SonosControl.Web.Services
{
    public static class HolidayCalendarParser
    {
        public sealed record HolidayEvent(DateOnly Date, string? Name);

        private static readonly string[] DateTimeFormats =
        {
            "yyyyMMdd'T'HHmmss'Z'",
            "yyyyMMdd'T'HHmmss",
            "yyyyMMdd"
        };

        public static async Task<IReadOnlyList<HolidayEvent>> ParseEventsAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            var content = await reader.ReadToEndAsync();
            return ParseEvents(content);
        }

        public static IReadOnlyList<HolidayEvent> ParseEvents(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return Array.Empty<HolidayEvent>();

            var unfolded = UnfoldLines(content);
            var events = new List<HolidayEvent>();
            Dictionary<string, string>? current = null;

            foreach (var line in unfolded)
            {
                if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    current = new(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    if (current != null && current.TryGetValue("DTSTART", out var startValue) && TryParseDate(startValue, out var date))
                    {
                        current.TryGetValue("SUMMARY", out var summary);
                        events.Add(new HolidayEvent(date, NormalizeText(summary)));
                    }

                    current = null;
                    continue;
                }

                if (current == null)
                    continue;

                var separatorIndex = line.IndexOf(':');
                if (separatorIndex < 0)
                    continue;

                var name = line[..separatorIndex];
                var value = line[(separatorIndex + 1)..];

                var parameterIndex = name.IndexOf(';');
                if (parameterIndex >= 0)
                {
                    name = name[..parameterIndex];
                }

                if (!current.ContainsKey(name))
                {
                    current[name] = value;
                }
            }

            return events;
        }

        private static IEnumerable<string> UnfoldLines(string content)
        {
            using var reader = new StringReader(content);
            string? current = null;
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith(' ') || line.StartsWith('\t'))
                {
                    current += line.TrimStart();
                    continue;
                }

                if (current != null)
                {
                    yield return current;
                }

                current = line;
            }

            if (current != null)
            {
                yield return current;
            }
        }

        private static bool TryParseDate(string value, out DateOnly date)
        {
            var trimmed = value.Trim();

            if (DateOnly.TryParseExact(trimmed, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return true;
            }

            foreach (var format in DateTimeFormats)
            {
                if (DateTime.TryParseExact(trimmed, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedUtc))
                {
                    date = DateOnly.FromDateTime(parsedUtc);
                    return true;
                }

                if (DateTime.TryParseExact(trimmed, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedLocal))
                {
                    date = DateOnly.FromDateTime(parsedLocal);
                    return true;
                }
            }

            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                date = DateOnly.FromDateTime(parsed);
                return true;
            }

            date = default;
            return false;
        }

        private static string? NormalizeText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value
                .Replace("\\n", " ", StringComparison.Ordinal)
                .Replace("\\,", ",", StringComparison.Ordinal)
                .Trim();
        }
    }
}
