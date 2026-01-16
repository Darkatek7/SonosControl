using System.Threading.Tasks;

namespace SonosControl.DAL.Interfaces
{
    public interface INotificationService
    {
        Task SendNotificationAsync(string message, string? performedBy = null);
    }
}
