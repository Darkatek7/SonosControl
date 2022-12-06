using SonosControl.DAL.Interfaces;

namespace SonosControl.DAL.Repos
{
    public class UnitOfWork : IUnitOfWork
    {
        private SettingsRepo? settingsRepo;
        private SonosConnectorRepo? sonosConnectorRepo;
        private HolidayRepo? holidayRepo;

        public ISettingsRepo ISettingsRepo => settingsRepo ?? new SettingsRepo();
        public IHolidayRepo IHolidayRepo => holidayRepo ?? new HolidayRepo();
        public ISonosConnectorRepo ISonosConnectorRepo => sonosConnectorRepo ?? new SonosConnectorRepo();
    }
}
