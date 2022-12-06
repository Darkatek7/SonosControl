using System.Net;

namespace SonosControl.DAL.Models
{
    public class SonosSettings
    {
        public int Volume { get; set; } = 10;
        public TimeOnly StartTime { get; set; } = new TimeOnly(6, 0);
        public TimeOnly StopTime { get; set; } = new TimeOnly(18, 0);
        public string IP_Adress { get; set; } = "10.0.0.0";
    }
}
