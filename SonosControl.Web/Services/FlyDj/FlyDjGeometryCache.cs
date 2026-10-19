namespace SonosControl.Web.Services.FlyDj;

/// <summary>Keeps the brain geometry (large, rarely changing) in memory between browser requests.</summary>
public sealed class FlyDjGeometryCache(IFlyDjClient client, TimeProvider time)
{
    public static readonly TimeSpan RevalidateAfter = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _content;
    private string? _etag;
    private DateTimeOffset _checkedUtc;

    /// <summary>Returns the current geometry, revalidating against the sidecar with its ETag at most every few minutes.</summary>
    public async Task<(byte[] Content, string ETag)> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            if (_content is not null && _etag is not null && now - _checkedUtc < RevalidateAfter)
                return (_content, _etag);
            var result = await client.GetGeometryAsync(_content is null ? null : _etag, cancellationToken);
            if (!result.NotModified || _content is null)
            {
                if (result.Content is null)
                    throw new FlyDjUnavailableException("The FlyDJ service returned no geometry.");
                _content = result.Content;
                _etag = result.ETag ?? $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(result.Content))[..16]}\"";
            }
            _checkedUtc = now;
            return (_content, _etag!);
        }
        finally { _gate.Release(); }
    }
}
