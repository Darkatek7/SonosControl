using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SonosControl.DAL.Interfaces;
using Microsoft.Extensions.Logging;

namespace SonosControl.Web.Services
{
    public class TeamsNotificationService : INotifier
    {
        private readonly ISettingsRepo _settingsRepo;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TeamsNotificationService> _logger;

        public TeamsNotificationService(ISettingsRepo settingsRepo, IHttpClientFactory httpClientFactory, ILogger<TeamsNotificationService> logger)
        {
            _settingsRepo = settingsRepo;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task SendNotificationAsync(string message, string? performedBy = null)
        {
            try
            {
                var settings = await _settingsRepo.GetSettings();
                if (string.IsNullOrWhiteSpace(settings?.TeamsWebhookUrl))
                {
                    return;
                }

                var fullMessage = message;
                if (!string.IsNullOrWhiteSpace(performedBy))
                {
                    fullMessage = $"**[{performedBy}]** {message}";
                }

                var payload = new
                {
                    text = fullMessage
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var client = _httpClientFactory.CreateClient("TeamsWebhook");
                var response = await client.PostAsync(settings.TeamsWebhookUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to send Teams notification. Status Code: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending Teams notification.");
            }
        }
    }
}
