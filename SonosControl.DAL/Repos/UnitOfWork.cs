using System.Net.Http;
using SonosControl.DAL.Interfaces;

namespace SonosControl.DAL.Repos
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly ISettingsRepo _settingsRepo;
        private readonly IHolidayRepo _holidayRepo;
        private readonly ISonosConnectorRepo _sonosConnectorRepo;

        public UnitOfWork(IHttpClientFactory httpClientFactory, ISettingsRepo settingsRepo)
        {
            _settingsRepo = settingsRepo;
            _holidayRepo = new HolidayRepo(); // Assuming HolidayRepo doesn't have constructor dependencies
            _sonosConnectorRepo = new SonosConnectorRepo(httpClientFactory, settingsRepo);
        }

        public ISettingsRepo ISettingsRepo => _settingsRepo;
        public IHolidayRepo IHolidayRepo => _holidayRepo;
        public ISonosConnectorRepo ISonosConnectorRepo => _sonosConnectorRepo;
    }
}

