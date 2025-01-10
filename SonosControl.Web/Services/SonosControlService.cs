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

                await WaitUntilStartTime(settings!.StartTime, settings!.StopTime);

                await StartSpeaker(settings!.IP_Adress);

                await StopSpeaker(settings!.IP_Adress, settings!.StopTime);
            }
        }

        private async Task StartSpeaker(string ip)
        {
            DayOfWeek day = DateTime.Now.DayOfWeek;

            if ((day == DayOfWeek.Saturday) || (day == DayOfWeek.Sunday)) //await _uow.IHolidayRepo.IsHoliday() || 
            {
                //if (await _uow.IHolidayRepo.IsHoliday())
                //{
                //    Console.WriteLine(DateTime.Now.ToString("g") + ": Today is a holiday!");
                //}
                //else 
                if ((day == DayOfWeek.Saturday))
                {
                    Console.WriteLine(DateTime.Now.ToString("g") + ": Today is a Saturday!");
                }
                else if ((day == DayOfWeek.Sunday))
                {
                    Console.WriteLine(DateTime.Now.ToString("g") + ": Today is a Sunday!");
                }
                return;
            }

            await _uow.ISonosConnectorRepo.StartPlaying(ip);
            _uow.ISonosConnectorRepo.SetVolume(_settings!.IP_Adress, 15);
            Console.WriteLine(DateTime.Now.ToString("g") + ": Started Playing");
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
