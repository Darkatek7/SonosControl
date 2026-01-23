using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SonosControl.DAL.Interfaces;
using Microsoft.Extensions.Logging;

namespace SonosControl.Web.Services
{
    public class AggregateNotificationService : INotificationService
    {
        private readonly IEnumerable<INotifier> _notifiers;
        private readonly ILogger<AggregateNotificationService> _logger;

        public AggregateNotificationService(IEnumerable<INotifier> notifiers, ILogger<AggregateNotificationService> logger)
        {
            _notifiers = notifiers;
            _logger = logger;
        }

        public async Task SendNotificationAsync(string message, string? performedBy = null)
        {
            var tasks = _notifiers.Select(async notifier =>
            {
                try
                {
                    await notifier.SendNotificationAsync(message, performedBy);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending notification via {NotifierType}.", notifier.GetType().Name);
                }
            });

            await Task.WhenAll(tasks);
        }
    }
}
