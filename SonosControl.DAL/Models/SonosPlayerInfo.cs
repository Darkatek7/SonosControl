using System;

namespace SonosControl.DAL.Models
{
    public class SonosPlayerInfo
    {
        public string IpAddress { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? Uuid { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Name)
                ? IpAddress
                : $"{Name} ({IpAddress})";
        }
    }
}
