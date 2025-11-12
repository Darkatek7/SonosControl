using System.Linq;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services
{
    public class HolidayCalendarSyncService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<HolidayCalendarSyncService> _logger;
        private readonly TimeProvider _timeProvider;

        public HolidayCalendarSyncService(
            IHttpClientFactory httpClientFactory,
            IUnitOfWork unitOfWork,
            ILogger<HolidayCalendarSyncService> logger,
            TimeProvider? timeProvider = null)
        {
            _httpClientFactory = httpClientFactory;
            _unitOfWork = unitOfWork;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public async Task<int> SyncAsync(Uri calendarUri, int maxRetries = 3, CancellationToken cancellationToken = default)
        {
            if (calendarUri is null)
                throw new ArgumentNullException(nameof(calendarUri));

            if (maxRetries < 1)
                maxRetries = 1;

            Exception? lastError = null;

            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var events = await DownloadCalendarAsync(calendarUri, cancellationToken);
                    var applied = await ApplyEventsAsync(events, cancellationToken);
                    _logger.LogInformation("Holiday calendar sync succeeded on attempt {Attempt} at {Timestamp} with {Applied} updates.", attempt, _timeProvider.GetLocalNow(), applied);
                    return applied;
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    lastError = ex;
                    var delay = TimeSpan.FromSeconds(Math.Min(30, attempt * 5));
                    _logger.LogWarning(ex, "Holiday calendar sync attempt {Attempt} of {MaxAttempts} failed. Retrying in {Delay}.", attempt, maxRetries, delay);
                    await Task.Delay(delay, cancellationToken);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    break;
                }
            }

            _logger.LogError(lastError, "Holiday calendar sync failed after {MaxAttempts} attempts.", maxRetries);
            throw new InvalidOperationException("Unable to synchronize holiday calendar.", lastError);
        }

        private async Task<IReadOnlyList<HolidayCalendarParser.HolidayEvent>> DownloadCalendarAsync(Uri calendarUri, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(calendarUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await HolidayCalendarParser.ParseEventsAsync(stream, cancellationToken);
        }

        private async Task<int> ApplyEventsAsync(IReadOnlyList<HolidayCalendarParser.HolidayEvent> events, CancellationToken cancellationToken)
        {
            if (events.Count == 0)
                return 0;

            var settings = await _unitOfWork.ISettingsRepo.GetSettings();
            if (settings == null)
                return 0;

            settings.HolidaySchedules ??= new();

            var updates = 0;

            foreach (var calendarEvent in events)
            {
                var existing = settings.HolidaySchedules.FirstOrDefault(h => h.Date == calendarEvent.Date);
                if (existing == null)
                {
                    settings.HolidaySchedules.Add(new HolidaySchedule
                    {
                        Date = calendarEvent.Date,
                        Name = calendarEvent.Name,
                        StartTime = settings.StartTime,
                        StopTime = settings.StopTime
                    });
                    updates++;
                }
                else if (!string.IsNullOrWhiteSpace(calendarEvent.Name) && !string.Equals(existing.Name, calendarEvent.Name, StringComparison.Ordinal))
                {
                    existing.Name = calendarEvent.Name;
                    updates++;
                }
            }

            if (updates > 0)
            {
                await _unitOfWork.ISettingsRepo.WriteSettings(settings);
            }

            return updates;
        }
    }
}
