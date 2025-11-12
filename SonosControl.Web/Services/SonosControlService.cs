using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
                var target = ResolvePlaybackTarget(settings);

                await StartSpeaker(target.CoordinatorIp, settings, schedule, target.MemberIps, target.Group, stoppingToken);

                await StopSpeaker(target.CoordinatorIp, stop, stoppingToken);
            }
        }

        private async Task StartSpeaker(string coordinatorIp, SonosSettings settings, DaySchedule? schedule, IReadOnlyList<string> memberIps, SonosGroup? group, CancellationToken cancellationToken)
        {
            DayOfWeek today = DateTime.Now.DayOfWeek;

            if (schedule == null && (settings == null || !settings.ActiveDays.Contains(today)))
            {
                Console.WriteLine($"{DateTime.Now:g}: Today ({today}) is not an active day.");
                return;
            }

            await PrepareGroupAsync(coordinatorIp, memberIps, group, cancellationToken);

            if (schedule != null)
            {
                if (schedule.PlayRandomSpotify)
                {
                    var url = GetRandomSpotifyUrl(settings);
                    if (url != null)
                        await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(coordinatorIp, url, null, cancellationToken);
                    else
                        await _uow.ISonosConnectorRepo.StartPlaying(coordinatorIp);
                }
                else if (schedule.PlayRandomYouTubeMusic)
                {
                    var url = GetRandomYouTubeMusicUrl(settings);
                    if (url != null)
                        await _uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(coordinatorIp, url, settings.AutoPlayStationUrl, cancellationToken);
                    else
                        await _uow.ISonosConnectorRepo.StartPlaying(coordinatorIp);
                }
                else if (schedule.PlayRandomStation)
                {
                    var url = GetRandomStationUrl(settings);
                    if (url != null)
                        await _uow.ISonosConnectorRepo.SetTuneInStationAsync(coordinatorIp, url, cancellationToken);
                    else
                        await _uow.ISonosConnectorRepo.StartPlaying(coordinatorIp);
                }
                else if (!string.IsNullOrEmpty(schedule.SpotifyUrl))
                    await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(coordinatorIp, schedule.SpotifyUrl, null, cancellationToken);
                else if (!string.IsNullOrEmpty(schedule.YouTubeMusicUrl))
                    await _uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(coordinatorIp, schedule.YouTubeMusicUrl, settings.AutoPlayStationUrl, cancellationToken);
                else if (!string.IsNullOrEmpty(schedule.StationUrl))
                    await _uow.ISonosConnectorRepo.SetTuneInStationAsync(coordinatorIp, schedule.StationUrl, cancellationToken);
                else
                    await _uow.ISonosConnectorRepo.StartPlaying(coordinatorIp);
            }
            else
            {
                if (settings.AutoPlayRandomSpotify)
                {
                    var url = GetRandomSpotifyUrl(settings);
                    if (url != null)
                        await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(coordinatorIp, url, null, cancellationToken);
                    else
                        await _uow.ISonosConnectorRepo.StartPlaying(coordinatorIp);
                }
                else if (settings.AutoPlayRandomYouTubeMusic)
                {
                    var url = GetRandomYouTubeMusicUrl(settings);
                    if (url != null)
                        await _uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(coordinatorIp, url, settings.AutoPlayStationUrl, cancellationToken);
                    else
                        await _uow.ISonosConnectorRepo.StartPlaying(coordinatorIp);
                }
                else if (settings.AutoPlayRandomStation)
                {
                    var url = GetRandomStationUrl(settings);
                    if (url != null)
                        await _uow.ISonosConnectorRepo.SetTuneInStationAsync(coordinatorIp, url, cancellationToken);
                    else
                        await _uow.ISonosConnectorRepo.StartPlaying(coordinatorIp);
                }
                else if (!string.IsNullOrEmpty(settings!.AutoPlaySpotifyUrl))
                    await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(coordinatorIp, settings.AutoPlaySpotifyUrl, null, cancellationToken);
                else if (!string.IsNullOrEmpty(settings!.AutoPlayYouTubeMusicUrl))
                    await _uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(coordinatorIp, settings.AutoPlayYouTubeMusicUrl, settings.AutoPlayStationUrl, cancellationToken);
                else if (!string.IsNullOrEmpty(settings!.AutoPlayStationUrl))
                    await _uow.ISonosConnectorRepo.SetTuneInStationAsync(coordinatorIp, settings.AutoPlayStationUrl, cancellationToken);
                else
                    await _uow.ISonosConnectorRepo.StartPlaying(coordinatorIp);
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
                var todaySchedule = GetScheduleForDay(settings, now.DayOfWeek);
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

        private static DaySchedule? GetScheduleForDay(SonosSettings settings, DayOfWeek day)
        {
            if (settings.DailySchedules != null && settings.DailySchedules.TryGetValue(day, out var schedule))
                return schedule;

            return null;
        }

        private (DateTimeOffset target, DaySchedule? schedule, TimeOnly start, DayOfWeek day) DetermineNextStart(SonosSettings settings, DateTimeOffset now)
        {
            var today = now.DayOfWeek;
            var todaySchedule = GetScheduleForDay(settings, today);
            var start = todaySchedule?.StartTime ?? settings.StartTime;
            var startDateTime = new DateTimeOffset(now.Date.Add(start.ToTimeSpan()), now.Offset);
            var currentTime = TimeOnly.FromDateTime(now.LocalDateTime);

            if (start < currentTime)
            {
                for (int offset = 1; offset <= 7; offset++)
                {
                    var day = (DayOfWeek)(((int)today + offset) % 7);
                    var schedule = GetScheduleForDay(settings, day);
                    var nextStart = schedule?.StartTime ?? settings.StartTime;
                    var nextDate = new DateTimeOffset(now.Date.AddDays(offset).Add(nextStart.ToTimeSpan()), now.Offset);
                    return (nextDate, schedule, nextStart, day);
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

        private async Task StopSpeaker(string coordinatorIp, TimeOnly stopTime, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(coordinatorIp))
                return;

            TimeOnly timeNow = TimeOnly.FromDateTime(DateTime.Now);
            var timeDifference = stopTime - timeNow;

            if (stopTime <= timeNow)
            {
                await _uow.ISonosConnectorRepo.StopPlaying(coordinatorIp);
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
                try
                {
                    await Task.Delay(ms, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                    return;

                await _uow.ISonosConnectorRepo.StopPlaying(coordinatorIp);
                Console.WriteLine(DateTime.Now.ToString("g") + ": Paused Playing");
            }
        }

        private (string CoordinatorIp, IReadOnlyList<string> MemberIps, SonosGroup? Group) ResolvePlaybackTarget(SonosSettings settings)
        {
            if (settings.Groups != null && !string.IsNullOrWhiteSpace(settings.ActiveGroupId))
            {
                var group = settings.Groups
                    .FirstOrDefault(g => string.Equals(g.Id, settings.ActiveGroupId, StringComparison.OrdinalIgnoreCase));

                if (group != null && !string.IsNullOrWhiteSpace(group.CoordinatorIp))
                {
                    var members = group.MemberIps?
                        .Where(ip => !string.IsNullOrWhiteSpace(ip))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(ip => !string.Equals(ip, group.CoordinatorIp, StringComparison.OrdinalIgnoreCase))
                        .ToList() ?? new List<string>();

                    return (group.CoordinatorIp, members, group);
                }
            }

            return (settings.IP_Adress, Array.Empty<string>(), null);
        }

        private async Task PrepareGroupAsync(string coordinatorIp, IReadOnlyList<string> memberIps, SonosGroup? group, CancellationToken cancellationToken)
        {
            if (group is null || string.IsNullOrWhiteSpace(coordinatorIp))
                return;

            try
            {
                await _uow.ISonosConnectorRepo.SetGroupCoordinatorAsync(coordinatorIp, cancellationToken);
            }
            catch (Exception ex) when (IsNetworkException(ex))
            {
                Console.WriteLine($"{DateTime.Now:g}: Failed to prepare coordinator {coordinatorIp}: {ex.Message}");
            }

            foreach (var member in memberIps.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(member, coordinatorIp, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    await _uow.ISonosConnectorRepo.JoinGroupAsync(coordinatorIp, member, cancellationToken);
                }
                catch (Exception ex) when (IsNetworkException(ex))
                {
                    Console.WriteLine($"{DateTime.Now:g}: Skipped member {member}: {ex.Message}");
                }
            }
        }

        private static bool IsNetworkException(Exception ex)
        {
            return ex is HttpRequestException || ex is TaskCanceledException || ex is IOException;
        }
    }
}
