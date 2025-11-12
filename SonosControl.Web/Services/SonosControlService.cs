using System.Linq;
using System.Threading;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services
{
    public class SonosControlService : BackgroundService
    {
        private IUnitOfWork _uow;
        private readonly TimeProvider _timeProvider;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public SonosControlService(IUnitOfWork uow, TimeProvider? timeProvider = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _uow = uow;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _delay = delay ?? TaskDelay;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Continuously evaluate settings until start time is reached
                var (settings, schedule) = await WaitUntilStartTime(stoppingToken);

                var stop = schedule?.StopTime ?? settings.StopTime;

                await StartSpeaker(settings.IP_Adress, settings, schedule);

                await StopSpeaker(settings.IP_Adress, stop);
            }
        }

        private async Task StartSpeaker(string ip, SonosSettings settings, DaySchedule? schedule)
        {
            DayOfWeek today = DateTime.Now.DayOfWeek;

            if (schedule == null && (settings == null || !settings.ActiveDays.Contains(today)))
            {
                Console.WriteLine($"{DateTime.Now:g}: Today ({today}) is not an active day.");
                return;
            }

            if (schedule != null)
            {
                if (schedule.PlayRandomSpotify)
                {
                    var url = GetRandomSpotifyUrl(settings);
                    if (url != null)
                        await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(ip, url);
                    else
                        await _uow.ISonosConnectorRepo.StartPlaying(ip);
                }
                else if (schedule.PlayRandomYouTubeMusic)
                {
                    var url = GetRandomYouTubeMusicUrl(settings);
                    if (url != null)
                        await _uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, url, settings.AutoPlayStationUrl);
                    else
                        await _uow.ISonosConnectorRepo.StartPlaying(ip);
                }
                else if (schedule.PlayRandomStation)
                {
                    var url = GetRandomStationUrl(settings);
                    if (url != null)
                        await _uow.ISonosConnectorRepo.SetTuneInStationAsync(ip, url);
                    else
                        await _uow.ISonosConnectorRepo.StartPlaying(ip);
                }
                else if (!string.IsNullOrEmpty(schedule.SpotifyUrl))
                    await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(ip, schedule.SpotifyUrl);
                else if (!string.IsNullOrEmpty(schedule.YouTubeMusicUrl))
                    await _uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, schedule.YouTubeMusicUrl, settings.AutoPlayStationUrl);
                else if (!string.IsNullOrEmpty(schedule.StationUrl))
                    await _uow.ISonosConnectorRepo.SetTuneInStationAsync(ip, schedule.StationUrl);
                else
                    await _uow.ISonosConnectorRepo.StartPlaying(ip);
            }
            else
            {
                if (settings.AutoPlayRandomSpotify)
                {
                    var url = GetRandomSpotifyUrl(settings);
                    if (url != null)
                        await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(ip, url);
                    else
                        await _uow.ISonosConnectorRepo.StartPlaying(ip);
                }
                else if (settings.AutoPlayRandomYouTubeMusic)
                {
                    var url = GetRandomYouTubeMusicUrl(settings);
                    if (url != null)
                        await _uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, url, settings.AutoPlayStationUrl);
                    else
                        await _uow.ISonosConnectorRepo.StartPlaying(ip);
                }
                else if (settings.AutoPlayRandomStation)
                {
                    var url = GetRandomStationUrl(settings);
                    if (url != null)
                        await _uow.ISonosConnectorRepo.SetTuneInStationAsync(ip, url);
                    else
                        await _uow.ISonosConnectorRepo.StartPlaying(ip);
                }
                else if (!string.IsNullOrEmpty(settings!.AutoPlaySpotifyUrl))
                    await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(ip, settings.AutoPlaySpotifyUrl);
                else if (!string.IsNullOrEmpty(settings!.AutoPlayYouTubeMusicUrl))
                    await _uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, settings.AutoPlayYouTubeMusicUrl, settings.AutoPlayStationUrl);
                else if (!string.IsNullOrEmpty(settings!.AutoPlayStationUrl))
                    await _uow.ISonosConnectorRepo.SetTuneInStationAsync(ip, settings.AutoPlayStationUrl);
                else
                    await _uow.ISonosConnectorRepo.StartPlaying(ip);
            }

            Console.WriteLine($"{DateTime.Now:g}: Started Playing");
        }


        private string? GetRandomStationUrl(SonosSettings settings)
        {
            if (settings.Stations == null || settings.Stations.Count == 0)
                return null;

            var index = Random.Shared.Next(settings.Stations.Count);
            return settings.Stations[index].Url;
        }

        private string? GetRandomSpotifyUrl(SonosSettings settings)
        {
            if (settings.SpotifyTracks == null || settings.SpotifyTracks.Count == 0)
                return null;

            var index = Random.Shared.Next(settings.SpotifyTracks.Count);
            return settings.SpotifyTracks[index].Url;
        }

        private string? GetRandomYouTubeMusicUrl(SonosSettings settings)
        {
            if (settings.YouTubeMusicCollections == null || settings.YouTubeMusicCollections.Count == 0)
                return null;

            var index = Random.Shared.Next(settings.YouTubeMusicCollections.Count);
            return settings.YouTubeMusicCollections[index].Url;
        }

        private async Task<(SonosSettings settings, DaySchedule? schedule)> WaitUntilStartTime(CancellationToken token)
        {
            TimeOnly? previousStart = null;
            DayOfWeek? previousDay = null;
            DateTimeOffset? previousTarget = null;

            while (!token.IsCancellationRequested)
            {
                var settings = await _uow.ISettingsRepo.GetSettings();
                if (settings is null)
                {
                    await _delay(TimeSpan.FromSeconds(1), token);
                    continue;
                }
                var now = _timeProvider.GetLocalNow();

                var currentTime = TimeOnly.FromDateTime(now.LocalDateTime);
                var todayDate = DateOnly.FromDateTime(now.LocalDateTime);
                var todaySchedule = GetScheduleForDate(settings, todayDate);
                var todayStart = todaySchedule?.StartTime ?? settings.StartTime;

                if (previousDay == now.DayOfWeek && previousStart == todayStart && todayStart <= currentTime)
                    return (settings, todaySchedule);

                var (target, schedule, start, startDay) = DetermineNextStart(settings, now);

                if (target <= now)
                    return (settings, schedule);

                var remaining = target - now;

                if (previousStart != start || previousDay != startDay || previousTarget != target)
                {
                    string delayInMs;
                    if (remaining.Days > 0)
                    {
                        delayInMs = string.Format("{0}d:{1:D2}h:{2:D2}m:{3:D2}s:{4:D3}ms",
                            remaining.Days,
                            remaining.Hours,
                            remaining.Minutes,
                            remaining.Seconds,
                            remaining.Milliseconds);
                    }
                    else
                    {
                        delayInMs = string.Format("{0:D2}h:{1:D2}m:{2:D2}s:{3:D3}ms",
                            remaining.Hours,
                            remaining.Minutes,
                            remaining.Seconds,
                            remaining.Milliseconds);
                    }

                    Console.WriteLine(now.LocalDateTime.ToString("g") + ": Starting in " + delayInMs);
                    previousStart = start;
                    previousDay = startDay;
                    previousTarget = target;
                }

                var delayMs = Math.Max(1d, Math.Min(remaining.TotalMilliseconds, 60_000d));
                // Poll settings at most once per minute to pick up schedule changes
                await _delay(TimeSpan.FromMilliseconds(delayMs), token);
            }

            token.ThrowIfCancellationRequested();
            return default;
        }

        private static DaySchedule? GetScheduleForDate(SonosSettings settings, DateOnly date)
        {
            if (settings.HolidaySchedules != null)
            {
                var holiday = settings.HolidaySchedules.FirstOrDefault(h => h.Date == date);
                if (holiday != null)
                    return holiday;
            }

            var day = date.DayOfWeek;
            if (settings.DailySchedules != null && settings.DailySchedules.TryGetValue(day, out var schedule))
                return schedule;

            return null;
        }

        private (DateTimeOffset target, DaySchedule? schedule, TimeOnly start, DayOfWeek day) DetermineNextStart(SonosSettings settings, DateTimeOffset now)
        {
            var today = now.DayOfWeek;
            var todayDate = DateOnly.FromDateTime(now.LocalDateTime);
            var todaySchedule = GetScheduleForDate(settings, todayDate);
            var start = todaySchedule?.StartTime ?? settings.StartTime;
            var startDateTime = new DateTimeOffset(now.Date.Add(start.ToTimeSpan()), now.Offset);
            var currentTime = TimeOnly.FromDateTime(now.LocalDateTime);

            if (start < currentTime)
            {
                for (int offset = 1; offset <= 14; offset++)
                {
                    var candidateDate = todayDate.AddDays(offset);
                    var schedule = GetScheduleForDate(settings, candidateDate);
                    var nextStart = schedule?.StartTime ?? settings.StartTime;
                    var nextDate = new DateTimeOffset(candidateDate.ToDateTime(nextStart), now.Offset);
                    return (nextDate, schedule, nextStart, candidateDate.DayOfWeek);
                }
            }

            if (start == currentTime)
                return (startDateTime, todaySchedule, start, today);

            return (startDateTime, todaySchedule, start, today);
        }

        private static Task TaskDelay(TimeSpan delay, CancellationToken token)
        {
            if (delay <= TimeSpan.Zero)
                return Task.CompletedTask;

            return Task.Delay(delay, token);
        }

        private async Task StopSpeaker(string ip, TimeOnly stopTime)
        {
            TimeOnly timeNow = TimeOnly.FromDateTime(DateTime.Now);
            var timeDifference = stopTime - timeNow;

            if (stopTime <= timeNow)
            {
                await _uow.ISonosConnectorRepo.StopPlaying(ip);
                Console.WriteLine(DateTime.Now.ToString("g") + ": Paused Playing");
            }
            else
            {
                var ms = (int)timeDifference.TotalMilliseconds;
                TimeSpan t = TimeSpan.FromMilliseconds(ms);
                string delayInMs = string.Format("{0:D2}h:{1:D2}m:{2:D2}s:{3:D3}ms",
                        t.Hours,
                        t.Minutes,
                        t.Seconds,
                        t.Milliseconds);

                Console.WriteLine(DateTime.Now.ToString("g") + ": Pausing in " + delayInMs);
                await Task.Delay(ms);

                await _uow.ISonosConnectorRepo.StopPlaying(ip);
                Console.WriteLine(DateTime.Now.ToString("g") + ": Paused Playing");
            }
        }
    }
}
