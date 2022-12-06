namespace SonosControl.DAL.Interfaces
{
    public interface IHolidayRepo
    {
        Task<bool> IsHoliday();
    }
}