using Newtonsoft.Json;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.DAL.Repos
{
    public class SettingsRepo : ISettingsRepo
    {
        public async Task WriteSettings(SonosSettings? settings)
        {
            if (!Directory.Exists("./Data"))
                Directory.CreateDirectory("./Data");

            var jsonString = JsonConvert.SerializeObject(settings);
            await File.WriteAllTextAsync("./Data/config.json", jsonString);
        }

        public async Task<SonosSettings?> GetSettings()
        {
            if (!File.Exists("./Data/config.json"))
                return new();

            var jsonString = await File.ReadAllTextAsync("./Data/config.json");
            return JsonConvert.DeserializeObject<SonosSettings?>(jsonString);
        }
    }
}
