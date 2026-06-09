using System.Net.Http;
using SonosControl.DAL.Interfaces;

namespace SonosControl.DAL.Repos
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly ISettingsRepo _settingsRepo;
        private readonly IHolidayRepo _holidayRepo;
        private readonly ISonosConnectorRepo _sonosConnectorRepo;

        public UnitOfWork(IHttpClientFactory httpClientFactory, ISettingsRepo settingsRepo, IYouTubePlaybackService youTubePlaybackService)
        {
            _settingsRepo = settingsRepo;
            _holidayRepo = new HolidayRepo(httpClientFactory);
            _sonosConnectorRepo = new SonosConnectorRepo(httpClientFactory, settingsRepo, youTubePlaybackService);
        }

        public ISettingsRepo ISettingsRepo => _settingsRepo;
        public IHolidayRepo IHolidayRepo => _holidayRepo;
        public ISonosConnectorRepo ISonosConnectorRepo => _sonosConnectorRepo;
    }
}
