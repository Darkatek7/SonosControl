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
                var settings = await _uow.ISettingsRepo.GetSettings();
                var today = DateTime.Now.DayOfWeek;

                DaySchedule? schedule = null;
                if (settings!.DailySchedules != null && settings.DailySchedules.TryGetValue(today, out var sched))
                    schedule = sched;

                TimeOnly start = schedule?.StartTime ?? settings!.StartTime;
                TimeOnly stop = schedule?.StopTime ?? settings!.StopTime;

                await WaitUntilStartTime(start, stop);

                await StartSpeaker(settings!.IP_Adress, schedule);

                await StopSpeaker(settings!.IP_Adress, stop);
            }
        }

        private async Task StartSpeaker(string ip, DaySchedule? schedule)
        {
            var settings = await _uow.ISettingsRepo.GetSettings();
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


        private async Task WaitUntilStartTime(TimeOnly start, TimeOnly stop)
        {
            TimeOnly timeNow = TimeOnly.FromDateTime(DateTime.Now);
            var timeDifference = start - timeNow;

            if (timeNow >= stop || start >= timeNow)
            {
                var ms = (int)timeDifference.TotalMilliseconds;
                TimeSpan t = TimeSpan.FromMilliseconds(ms);
                string delayInMs = string.Format("{0:D2}h:{1:D2}m:{2:D2}s:{3:D3}ms",
                        t.Hours,
                        t.Minutes,
                        t.Seconds,
                        t.Milliseconds);

                Console.WriteLine(DateTime.Now.ToString("g") + ": Starting in " + delayInMs);
                Task.Delay(ms).Wait();
            }
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
                Task.Delay(ms).Wait();

                await _uow.ISonosConnectorRepo.StopPlaying(ip);
                Console.WriteLine(DateTime.Now.ToString("g") + ": Paused Playing");
            }
        }
    }
}
