using SonosControl.DAL.Interfaces;

namespace SonosControl.DAL.Repos
{
    public class HolidayRepo : IHolidayRepo
    {
        public async Task GetHolidays()
        {
            // Request url
            // https://openholidaysapi.org/PublicHolidays?countryIsoCode=AT&validFrom=2022-01-01&validTo=2022-12-31&subdivisionIsoCode=AT-8&languageIsoCode=DE

            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Add("accept", "text/json");
            HttpResponseMessage response = await client.GetAsync($"https://openholidaysapi.org/PublicHolidays?countryIsoCode=AT&validFrom={DateTime.Now.ToString("yyyy-MM-dd")}&validTo={DateTime.Now.ToString("yyyy-MM-dd")}&subdivisionIsoCode=AT-8&languageIsoCode=DE");
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();
        }
    }
}
