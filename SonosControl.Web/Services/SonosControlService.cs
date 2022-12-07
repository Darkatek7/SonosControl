using SonosControl.DAL.Interfaces;

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

                await StartSpeaker(settings!.IP_Adress, settings!.StartTime);

                await StopSpeaker(settings!.IP_Adress, settings!.StopTime);
            }
        }

        private async Task StartSpeaker(string ip, TimeOnly startTime)
        {
            DayOfWeek day = DateTime.Now.DayOfWeek;

            if (await _uow.IHolidayRepo.IsHoliday() || (day == DayOfWeek.Saturday) || (day == DayOfWeek.Sunday))
            {
                return;
            }

            TimeOnly timeNow = TimeOnly.FromDateTime(DateTime.Now);
            var timeDifference = startTime - timeNow;

            if (startTime <= timeNow)
            {
                await _uow.ISonosConnectorRepo.StartPlaying(ip);
            }
            else
            {
                var delayInMs = int.Parse(timeDifference.TotalMilliseconds.ToString().Substring(0, timeDifference.TotalMilliseconds.ToString().IndexOf(",") + 1).Replace(",", ""));

                Task.Delay(delayInMs).Wait();
                await _uow.ISonosConnectorRepo.StartPlaying(ip);
            }
        }

        private async Task StopSpeaker(string ip, TimeOnly stopTime)
        {
            TimeOnly timeNow = TimeOnly.FromDateTime(DateTime.Now);
            var timeDifference = stopTime - timeNow;

            if (stopTime <= timeNow)
            {
                await _uow.ISonosConnectorRepo.StopPlaying(ip);
            }
            else
            {
                var delayInMs = int.Parse(timeDifference.TotalMilliseconds.ToString().Substring(0, timeDifference.TotalMilliseconds.ToString().IndexOf(",") + 1).Replace(",", ""));

                Task.Delay(delayInMs).Wait();
                await _uow.ISonosConnectorRepo.StopPlaying(ip);
            }
        }
    }
}
