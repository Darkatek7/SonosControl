using System.Globalization;

namespace SonosControl.Web.Services;

public sealed class ConfiguredTimeZoneService
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("en-GB");

    public ConfiguredTimeZoneService(TimeZoneInfo timeZone)
    {
        TimeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
    }

    public ConfiguredTimeZoneService(
        IConfiguration configuration,
        ILogger<ConfiguredTimeZoneService> logger)
    {
        var configuredId = configuration["Automation:TimeZone"];
        var id = string.IsNullOrWhiteSpace(configuredId) ? "Europe/Vienna" : configuredId.Trim();

        try
        {
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            TimeZone = TimeZoneInfo.Local;
            logger.LogWarning(
                exception,
                "Configured timezone {TimeZoneId} is unavailable; UI timestamps use {FallbackTimeZoneId}.",
                id,
                TimeZone.Id);
        }
    }

    public TimeZoneInfo TimeZone { get; }

    public DateTimeOffset Now => Convert(DateTimeOffset.UtcNow);

    public DateTimeOffset Convert(DateTimeOffset instant)
        => TimeZoneInfo.ConvertTime(instant, TimeZone);

    public DateTime ConvertUtc(DateTime instant)
    {
        var utc = instant.Kind switch
        {
            DateTimeKind.Utc => instant,
            DateTimeKind.Local => instant.ToUniversalTime(),
            _ => DateTime.SpecifyKind(instant, DateTimeKind.Utc)
        };

        return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZone);
    }

    public DateTimeOffset FromLocal(DateTime localDateTime)
    {
        var local = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        while (TimeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return new DateTimeOffset(local, TimeZone.GetUtcOffset(local));
    }

    public string Format(DateTime instant, string format = "dd/MM/yyyy HH:mm")
        => ConvertUtc(instant).ToString(format, DisplayCulture);

    public string Format(DateTimeOffset instant, string format = "dd/MM/yyyy HH:mm")
        => Convert(instant).ToString(format, DisplayCulture);
}
