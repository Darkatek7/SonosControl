using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SonosControl.DAL.Interfaces;
using Microsoft.Extensions.Logging;

namespace SonosControl.Web.Services
{
    public class DiscordNotificationService : INotificationService
    {
        private readonly ISettingsRepo _settingsRepo;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<DiscordNotificationService> _logger;

        public DiscordNotificationService(ISettingsRepo settingsRepo, IHttpClientFactory httpClientFactory, ILogger<DiscordNotificationService> logger)
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
                if (string.IsNullOrWhiteSpace(settings?.DiscordWebhookUrl))
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
                    content = fullMessage
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var client = _httpClientFactory.CreateClient("DiscordWebhook");
                var response = await client.PostAsync(settings.DiscordWebhookUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to send Discord notification. Status Code: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending Discord notification.");
            }
        }
    }
}
