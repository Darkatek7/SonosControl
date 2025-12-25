using System;
using System.Net.Http;
using System.Threading.Tasks;
﻿using SonosControl.DAL.Interfaces;

namespace SonosControl.DAL.Repos
{
    public class HolidayRepo : IHolidayRepo
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public HolidayRepo(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        public async Task<bool> IsHoliday()
        {
            // Request url
            // https://openholidaysapi.org/PublicHolidays?countryIsoCode=AT&validFrom=2022-01-01&validTo=2022-12-31&subdivisionIsoCode=AT-8&languageIsoCode=DE

            HttpClient client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("accept", "text/json");
            HttpResponseMessage response = await client.GetAsync($"https://openholidaysapi.org/PublicHolidays?countryIsoCode=AT&validFrom={DateTime.Now.ToString("yyyy-MM-dd")}&validTo={DateTime.Now.ToString("yyyy-MM-dd")}&subdivisionIsoCode=AT-8&languageIsoCode=DE");
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();

            if (responseBody.Length >= 3)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}
