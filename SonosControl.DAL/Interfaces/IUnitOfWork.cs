namespace SonosControl.DAL.Interfaces
{
    public interface IUnitOfWork
    {
        ISettingsRepo ISettingsRepo { get; }
        ISonosConnectorRepo ISonosConnectorRepo { get; }
    }
}
