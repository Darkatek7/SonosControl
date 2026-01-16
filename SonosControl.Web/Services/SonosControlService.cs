using System.Linq;
using System.Threading;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using Microsoft.Extensions.DependencyInjection;

namespace SonosControl.Web.Services
{
    public class SonosControlService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public SonosControlService(IServiceScopeFactory scopeFactory, TimeProvider? timeProvider = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _scopeFactory = scopeFactory;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _delay = delay ?? TaskDelay;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Create a new scope for each execution cycle
                using (var scope = _scopeFactory.CreateScope())
                {
                    var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

                    // Continuously evaluate settings until start time is reached
                    var (settings, schedule) = await WaitUntilStartTime(uow, stoppingToken);

                    if (settings == null || settings.Speakers == null || !settings.Speakers.Any())
                    {
                        Console.WriteLine($"{_timeProvider.GetLocalNow():g}: No speakers configured. Waiting...");
                        await _delay(TimeSpan.FromSeconds(30), stoppingToken);
                        continue;
                    }

                    var stop = schedule?.StopTime ?? settings.StopTime;
                    var speakers = settings.Speakers.ToList();

                    try
                    {
                        await StartSpeaker(uow, speakers, settings, schedule, stoppingToken);
                        await notificationService.SendNotificationAsync($"Automation started playback on {speakers.Count} speakers.");
                    }
                    catch (Exception ex)
                    {
                        await notificationService.SendNotificationAsync($"Automation failed to start playback: {ex.Message}");
                    }

                    await StopSpeaker(uow, speakers, stop, schedule, stoppingToken);
                    await notificationService.SendNotificationAsync($"Automation stopped playback.");
                }
            }
        }

        private async Task StartSpeaker(IUnitOfWork uow, IEnumerable<SonosSpeaker> speakers, SonosSettings settings, DaySchedule? schedule, CancellationToken cancellationToken)
        {
            var now = _timeProvider.GetLocalNow();
            DayOfWeek today = now.DayOfWeek;

            if (schedule != null && ShouldSkipPlayback(schedule))
            {
                if (schedule is HolidaySchedule holiday)
                {
                    Console.WriteLine($"{now:g}: Holiday override for {holiday.Date:yyyy-MM-dd} skips playback.");
                }
                return;
            }

            if (schedule == null && (settings == null || !settings.ActiveDays.Contains(today)))
            {
                Console.WriteLine($"{now:g}: Today ({today}) is not an active day.");
                return;
            }

            string masterIp = speakers.First().IpAddress;
            bool isSynced = schedule?.IsSyncedPlayback ?? true;

            var targetSpeakers = new List<string>();

            // Ungroup all speakers first to ensure a clean slate
            await Task.WhenAll(speakers.Select(async speaker =>
            {
                await uow.ISonosConnectorRepo.UngroupSpeaker(speaker.IpAddress, cancellationToken);
                // Set volume for each speaker
                int volume = speaker.StartupVolume ?? settings.Volume;
                await uow.ISonosConnectorRepo.SetSpeakerVolume(speaker.IpAddress, volume, cancellationToken);
            }));

            if (isSynced)
            {
                // In synced mode, we group everyone to the master, and only command the master
                targetSpeakers.Add(masterIp);

                var slaveIps = speakers.Skip(1).Select(s => s.IpAddress);
                if (slaveIps.Any())
                {
                    await uow.ISonosConnectorRepo.CreateGroup(masterIp, slaveIps, cancellationToken);
                }
            }
            else
            {
                // In independent mode, we target all speakers individually
                targetSpeakers.AddRange(speakers.Select(s => s.IpAddress));
            }

            Func<string, Task> playAction = null;

            if (schedule != null)
            {
                if (schedule.PlayRandomSpotify)
                {
                    var url = GetRandomSpotifyUrl(settings);
                    if (url != null)
                        playAction = (ip) => uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(ip, url);
                    else
                        playAction = (ip) => uow.ISonosConnectorRepo.StartPlaying(ip);
                }
                else if (schedule.PlayRandomYouTubeMusic)
                {
                    var url = GetRandomYouTubeMusicUrl(settings);
                    if (url != null)
                        playAction = (ip) => uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, url, settings.AutoPlayStationUrl);
                    else
                        playAction = (ip) => uow.ISonosConnectorRepo.StartPlaying(ip);
                }
                else if (schedule.PlayRandomStation)
                {
                    var url = GetRandomStationUrl(settings);
                    if (url != null)
                        playAction = (ip) => uow.ISonosConnectorRepo.SetTuneInStationAsync(ip, url);
                    else
                        playAction = (ip) => uow.ISonosConnectorRepo.StartPlaying(ip);
                }
                else if (!string.IsNullOrEmpty(schedule.SpotifyUrl))
                    playAction = (ip) => uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(ip, schedule.SpotifyUrl);
                else if (!string.IsNullOrEmpty(schedule.YouTubeMusicUrl))
                    playAction = (ip) => uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, schedule.YouTubeMusicUrl, settings.AutoPlayStationUrl);
                else if (!string.IsNullOrEmpty(schedule.StationUrl))
                    playAction = (ip) => uow.ISonosConnectorRepo.SetTuneInStationAsync(ip, schedule.StationUrl);
                else
                    playAction = (ip) => uow.ISonosConnectorRepo.StartPlaying(ip);
            }
            else
            {
                if (settings.AutoPlayRandomSpotify)
                {
                    var url = GetRandomSpotifyUrl(settings);
                    if (url != null)
                        playAction = (ip) => uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(ip, url);
                    else
                        playAction = (ip) => uow.ISonosConnectorRepo.StartPlaying(ip);
                }
                else if (settings.AutoPlayRandomYouTubeMusic)
                {
                    var url = GetRandomYouTubeMusicUrl(settings);
                    if (url != null)
                        playAction = (ip) => uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, url, settings.AutoPlayStationUrl);
                    else
                        playAction = (ip) => uow.ISonosConnectorRepo.StartPlaying(ip);
                }
                else if (settings.AutoPlayRandomStation)
                {
                    var url = GetRandomStationUrl(settings);
                    if (url != null)
                        playAction = (ip) => uow.ISonosConnectorRepo.SetTuneInStationAsync(ip, url);
                    else
                        playAction = (ip) => uow.ISonosConnectorRepo.StartPlaying(ip);
                }
                else if (!string.IsNullOrEmpty(settings!.AutoPlaySpotifyUrl))
                    playAction = (ip) => uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(ip, settings.AutoPlaySpotifyUrl);
                else if (!string.IsNullOrEmpty(settings!.AutoPlayYouTubeMusicUrl))
                    playAction = (ip) => uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, settings.AutoPlayYouTubeMusicUrl, settings.AutoPlayStationUrl);
                else if (!string.IsNullOrEmpty(settings!.AutoPlayStationUrl))
                    playAction = (ip) => uow.ISonosConnectorRepo.SetTuneInStationAsync(ip, settings.AutoPlayStationUrl);
                else
                    playAction = (ip) => uow.ISonosConnectorRepo.StartPlaying(ip);
            }

            if (playAction != null)
            {
                await Task.WhenAll(targetSpeakers.Select(ip => playAction(ip)));
            }

            Console.WriteLine($"{now:g}: Started Playing");
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

        private async Task<(SonosSettings settings, DaySchedule? schedule)> WaitUntilStartTime(IUnitOfWork uow, CancellationToken token)
        {
            TimeOnly? previousStart = null;
            DayOfWeek? previousDay = null;
            DateTimeOffset? previousTarget = null;

            while (!token.IsCancellationRequested)
            {
                var settings = await uow.ISettingsRepo.GetSettings();
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

                if (todaySchedule != null && ShouldSkipPlayback(todaySchedule))
                {
                    todaySchedule = null;
                }

                if (todaySchedule != null && previousDay == now.DayOfWeek && previousStart == todayStart && todayStart <= currentTime)
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

                // Fix: Avoid rounding issues that cause delays larger than remaining time for small durations
                // Poll settings at most once per minute to pick up schedule changes
                var maxDelay = TimeSpan.FromMinutes(1);
                var delay = remaining > maxDelay ? maxDelay : remaining;

                // Ensure we don't pass a zero or negative delay if something drifted slightly,
                // but ManualTimeProvider handles zero correctly.
                // However, we want to respect the cancellation token and yield.
                if (delay <= TimeSpan.Zero)
                    delay = TimeSpan.FromTicks(1);

                await _delay(delay, token);
            }

            token.ThrowIfCancellationRequested();
            return default;
        }

        private static DaySchedule? GetScheduleForDate(SonosSettings settings, DateOnly date)
        {
            var day = date.DayOfWeek;

            // Master switch: if day is not active, no playback (even if holiday).
            // Default to true (Active) if ActiveDays is null for backward compatibility.
            if (settings.ActiveDays != null && !settings.ActiveDays.Contains(day))
                return null;

            if (settings.HolidaySchedules != null)
            {
                var holiday = settings.HolidaySchedules.FirstOrDefault(h => h.Date == date);
                if (holiday != null)
                    return holiday;
            }

            if (settings.DailySchedules != null && settings.DailySchedules.TryGetValue(day, out var schedule))
                return schedule;

            return null;
        }

        private (DateTimeOffset target, DaySchedule? schedule, TimeOnly start, DayOfWeek day) DetermineNextStart(SonosSettings settings, DateTimeOffset now)
        {
            var todayDate = DateOnly.FromDateTime(now.LocalDateTime);
            var currentTime = TimeOnly.FromDateTime(now.LocalDateTime);

            for (int offset = 0; offset <= 14; offset++)
            {
                var candidateDate = todayDate.AddDays(offset);
                var schedule = GetScheduleForDate(settings, candidateDate);

                if (schedule != null && ShouldSkipPlayback(schedule))
                    continue;

                // If no schedule found (and not holiday override), check if day is active.
                // If not active, skip it entirely instead of falling back to default settings.
                // Also respect backward compatibility (null ActiveDays = all active).
                if (schedule == null && settings.ActiveDays != null && !settings.ActiveDays.Contains(candidateDate.DayOfWeek))
                    continue;

                var candidateStart = schedule?.StartTime ?? settings.StartTime;
                var candidateDateTime = new DateTimeOffset(candidateDate.ToDateTime(candidateStart), now.Offset);

                if (offset == 0 && candidateStart < currentTime)
                    continue;

                return (candidateDateTime, schedule, candidateStart, candidateDate.DayOfWeek);
            }

            var fallbackDate = todayDate.AddDays(1);
            var fallbackStart = settings.StartTime;
            var fallbackDateTime = new DateTimeOffset(fallbackDate.ToDateTime(fallbackStart), now.Offset);
            return (fallbackDateTime, null, fallbackStart, fallbackDate.DayOfWeek);
        }

        private static Task TaskDelay(TimeSpan delay, CancellationToken token)
        {
            if (delay <= TimeSpan.Zero)
                return Task.CompletedTask;

            return Task.Delay(delay, token);
        }

        private static bool HasPlaybackTarget(DaySchedule schedule)
        {
            return schedule.PlayRandomStation
                   || schedule.PlayRandomSpotify
                   || schedule.PlayRandomYouTubeMusic
                   || !string.IsNullOrWhiteSpace(schedule.StationUrl)
                   || !string.IsNullOrWhiteSpace(schedule.SpotifyUrl)
                   || !string.IsNullOrWhiteSpace(schedule.YouTubeMusicUrl);
        }

        private static bool ShouldSkipPlayback(DaySchedule schedule)
        {
            if (schedule is HolidaySchedule holiday)
            {
                if (holiday.SkipPlayback)
                    return true;

                return !HasPlaybackTarget(holiday);
            }

            return false;
        }

        private async Task StopSpeaker(IUnitOfWork uow, IEnumerable<SonosSpeaker> speakers, TimeOnly stopTime, DaySchedule? schedule, CancellationToken cancellationToken)
        {
            var now = _timeProvider.GetLocalNow();
            TimeOnly timeNow = TimeOnly.FromDateTime(now.LocalDateTime);
            var timeDifference = stopTime - timeNow;
            bool isSynced = schedule?.IsSyncedPlayback ?? true;

            var targetSpeakers = new List<string>();
            if (isSynced)
            {
                targetSpeakers.AddRange(speakers.Select(s => s.IpAddress));
            }
            else
            {
                targetSpeakers.Add(speakers.First().IpAddress);
            }

            if (stopTime <= timeNow)
            {
                await Task.WhenAll(targetSpeakers.Select(ip => uow.ISonosConnectorRepo.StopPlaying(ip)));
                Console.WriteLine($"{now:g}: Paused Playing");
            }
            else
            {
                string delayInMs;
                if (timeDifference.TotalMilliseconds > 0)
                {
                     // Convert using TimeSpan to avoid manual ms calculations
                     TimeSpan t = timeDifference; // TimeOnly subtraction returns TimeSpan
                     delayInMs = string.Format("{0:D2}h:{1:D2}m:{2:D2}s:{3:D3}ms",
                        t.Hours,
                        t.Minutes,
                        t.Seconds,
                        t.Milliseconds);
                } else {
                     delayInMs = "00ms";
                }

                Console.WriteLine($"{now:g}: Pausing in " + delayInMs);

                // Use _delay instead of Task.Delay
                var delaySpan = timeDifference;
                if (delaySpan < TimeSpan.Zero) delaySpan = TimeSpan.Zero;
                await _delay(delaySpan, cancellationToken);

                await Task.WhenAll(targetSpeakers.Select(ip => uow.ISonosConnectorRepo.StopPlaying(ip)));
                Console.WriteLine($"{_timeProvider.GetLocalNow():g}: Paused Playing");
            }

            if (isSynced)
            {
                await Task.WhenAll(speakers.Select(speaker => uow.ISonosConnectorRepo.UngroupSpeaker(speaker.IpAddress, cancellationToken)));
            }
        }
    }
}
