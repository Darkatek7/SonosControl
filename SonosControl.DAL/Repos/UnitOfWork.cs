using SonosControl.DAL.Interfaces;

namespace SonosControl.DAL.Repos
{
    public class UnitOfWork : IUnitOfWork
    {
        private SettingsRepo? settingsRepo;
        private SonosConnectorRepo? sonosConnectorRepo;

        public ISettingsRepo ISettingsRepo => settingsRepo ?? new SettingsRepo();
        public ISonosConnectorRepo ISonosConnectorRepo => sonosConnectorRepo ?? new SonosConnectorRepo();
    }
}
