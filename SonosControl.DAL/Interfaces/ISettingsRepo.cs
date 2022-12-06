using SonosControl.DAL.Models;

namespace SonosControl.DAL.Interfaces
{
    public interface ISettingsRepo
    {
        Task<SonosSettings?> GetSettings();
        Task WriteSettings(SonosSettings? settings);
    }
}