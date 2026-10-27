using SonosControl.DAL.Models;

namespace SonosControl.DAL.Interfaces;

public interface ISonosEventSubscriptionClient
{
    Task<SonosEventSubscription> SubscribeAsync(string ip, SonosEventService service, Uri callback, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Renews a subscription. Returns null when the speaker no longer knows the SID (HTTP 412).</summary>
    Task<SonosEventSubscription?> RenewAsync(string ip, SonosEventService service, string sid, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Ends a subscription. Returns false instead of throwing, since an expired subscription needs no cleanup.</summary>
    Task<bool> UnsubscribeAsync(string ip, SonosEventService service, string sid, CancellationToken cancellationToken = default);
}
