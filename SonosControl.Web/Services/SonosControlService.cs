using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services
{
    public class SonosControlService : BackgroundService
    {
        private IUnitOfWork _uow;

        public SonosControlService(IUnitOfWork uow)
        {
            _uow = uow;
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
                if (!string.IsNullOrEmpty(schedule.SpotifyUrl))
                    await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(ip, schedule.SpotifyUrl);
                else if (!string.IsNullOrEmpty(schedule.StationUrl))
                    await _uow.ISonosConnectorRepo.SetTuneInStationAsync(ip, schedule.StationUrl);
                else
                    await _uow.ISonosConnectorRepo.StartPlaying(ip);
            }
            else
            {
                if (!string.IsNullOrEmpty(settings!.AutoPlaySpotifyUrl))
                    await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(ip, settings.AutoPlaySpotifyUrl);
                else if (!string.IsNullOrEmpty(settings!.AutoPlayStationUrl))
                    await _uow.ISonosConnectorRepo.SetTuneInStationAsync(ip, settings.AutoPlayStationUrl);
                else
                    await _uow.ISonosConnectorRepo.StartPlaying(ip);
            }

            Console.WriteLine($"{DateTime.Now:g}: Started Playing");
        }


        private async Task<(SonosSettings settings, DaySchedule? schedule)> WaitUntilStartTime(CancellationToken token)
        {
            TimeOnly? previousStart = null;

            while (!token.IsCancellationRequested)
            {
                var settings = await _uow.ISettingsRepo.GetSettings();
                var today = DateTime.Now.DayOfWeek;

                DaySchedule? schedule = null;
                if (settings!.DailySchedules != null && settings.DailySchedules.TryGetValue(today, out var sched))
                    schedule = sched;

                var start = schedule?.StartTime ?? settings.StartTime;
                var now = TimeOnly.FromDateTime(DateTime.Now);

                if (start <= now)
                    return (settings, schedule);

                if (previousStart != start)
                {
                    var totalMs = (int)(start - now).TotalMilliseconds;
                    TimeSpan t = TimeSpan.FromMilliseconds(totalMs);
                    string delayInMs = string.Format("{0:D2}h:{1:D2}m:{2:D2}s:{3:D3}ms",
                            t.Hours,
                            t.Minutes,
                            t.Seconds,
                            t.Milliseconds);

                    Console.WriteLine(DateTime.Now.ToString("g") + ": Starting in " + delayInMs);
                    previousStart = start;
                }

                var msRemaining = (int)(start - now).TotalMilliseconds;
                // Poll settings at most once per minute to pick up schedule changes
                var delay = Math.Max(1, Math.Min(msRemaining, 60_000));
                await Task.Delay(delay, token);
            }

            token.ThrowIfCancellationRequested();
            return default;
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
