using System.Net.Http;
using SonosControl.DAL.Interfaces;

namespace SonosControl.DAL.Repos
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private SettingsRepo? settingsRepo;
        private SonosConnectorRepo? sonosConnectorRepo;
        private HolidayRepo? holidayRepo;

        public UnitOfWork(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public ISettingsRepo ISettingsRepo => settingsRepo ?? new SettingsRepo();
        public IHolidayRepo IHolidayRepo => holidayRepo ?? new HolidayRepo();
        public ISonosConnectorRepo ISonosConnectorRepo => sonosConnectorRepo ?? new SonosConnectorRepo(_httpClientFactory);
    }
}
