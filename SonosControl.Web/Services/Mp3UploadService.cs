using System.Globalization;
using Microsoft.Extensions.Options;

namespace SonosControl.Web.Services;

public sealed class Mp3UploadOptions
{
    public int RetentionMinutes { get; set; } = 60;
    public int CleanupIntervalMinutes { get; set; } = 1;
    public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024;
    public string? ArtifactDirectory { get; set; }
}

public sealed record UploadedMp3(string Id, string Title, string StreamUrl, DateTimeOffset ExpiresAtUtc);

public sealed class Mp3UploadService
{
    public const int MaxRetentionMinutes = 7 * 24 * 60;
    private readonly string _directory;
    private readonly string _publicBaseUrl;
    private readonly TimeProvider _clock;
    private readonly ILogger<Mp3UploadService> _logger;

    public long MaxFileSizeBytes { get; }
    public int DefaultRetentionMinutes { get; }
    public TimeSpan CleanupInterval { get; }

    public Mp3UploadService(IWebHostEnvironment environment, IOptions<Mp3UploadOptions> options,
        IOptions<YouTubePlaybackOptions> playbackOptions, TimeProvider clock, ILogger<Mp3UploadService> logger)
    {
        var settings = options.Value;
        _directory = Path.GetFullPath(string.IsNullOrWhiteSpace(settings.ArtifactDirectory)
            ? Path.Combine(environment.ContentRootPath, "artifacts", "mp3-uploads")
            : settings.ArtifactDirectory);
        _publicBaseUrl = (playbackOptions.Value.PublicBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        _clock = clock;
        _logger = logger;
        MaxFileSizeBytes = Math.Clamp(settings.MaxFileSizeBytes, 1, 1024L * 1024 * 1024);
        DefaultRetentionMinutes = Math.Clamp(settings.RetentionMinutes, 1, MaxRetentionMinutes);
        CleanupInterval = TimeSpan.FromMinutes(Math.Clamp(settings.CleanupIntervalMinutes, 1, 60));
    }

    public async Task<UploadedMp3> SaveAsync(Stream source, string fileName, int retentionMinutes,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".mp3", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose an MP3 file.");
        }
        if (retentionMinutes is < 1 or > MaxRetentionMinutes)
        {
            throw new InvalidOperationException($"Delete after must be between 1 and {MaxRetentionMinutes} minutes.");
        }
        if (!Uri.TryCreate(_publicBaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != "http" && baseUri.Scheme != "https") || baseUri.IsLoopback
            || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new InvalidOperationException("Configure Playback:PublicBaseUrl with the LAN URL that your Sonos speakers can reach.");
        }

        Directory.CreateDirectory(_directory);
        var stagingPath = Path.Combine(_directory, $"{Guid.NewGuid():N}.upload");
        try
        {
            await using (var target = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxFileSizeBytes)
                    {
                        throw new InvalidOperationException("The MP3 exceeds the upload size limit.");
                    }
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                target.Position = 0;
                var header = new byte[3];
                if (total < 3 || await target.ReadAsync(header, cancellationToken) != 3
                    || !(header.AsSpan().SequenceEqual("ID3"u8)
                        || (header[0] == 0xff && (header[1] & 0xe0) == 0xe0
                            && (header[1] & 0x06) == 0x02 && (header[1] & 0x18) != 0x08
                            && (header[2] & 0xf0) is > 0 and < 0xf0 && (header[2] & 0x0c) != 0x0c)))
                {
                    throw new InvalidOperationException("The file does not have an MP3 header.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var expiry = _clock.GetUtcNow().AddMinutes(retentionMinutes);
            // Persist the deadline in the generated name so cleanup survives app restarts.
            var id = $"{expiry.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}";
            File.Move(stagingPath, Path.Combine(_directory, $"{id}.mp3"));
            return new UploadedMp3(id, Path.GetFileNameWithoutExtension(fileName.Replace('\\', '/')),
                $"{_publicBaseUrl}/api/mp3-audio/{id}.mp3", expiry);
        }
        finally
        {
            DeleteFile(stagingPath);
        }
    }

    public FileStream? OpenRead(string id)
    {
        if (!TryGetExpiry(id, out var expiry) || expiry <= _clock.GetUtcNow())
        {
            return null;
        }
        try
        {
            return new FileStream(Path.Combine(_directory, $"{id}.mp3"), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void Delete(string id)
    {
        if (TryGetExpiry(id, out _))
        {
            DeleteFile(Path.Combine(_directory, $"{id}.mp3"));
        }
    }

    public void CleanupExpiredFiles(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory)) return;
        var now = _clock.GetUtcNow();
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetExtension(path) == ".mp3"
                && TryGetExpiry(Path.GetFileNameWithoutExtension(path), out var expiry) && expiry <= now)
            {
                DeleteFile(path);
            }
            else if (Path.GetExtension(path) == ".upload"
                && Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)
                && File.GetLastWriteTimeUtc(path) < now.UtcDateTime.AddDays(-1))
            {
                DeleteFile(path);
            }
        }
    }

    private static bool TryGetExpiry(string id, out DateTimeOffset expiry)
    {
        expiry = default;
        var parts = id.Split('-');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[1], "N", out _)
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            || milliseconds < 0 || milliseconds > 253402300799999)
        {
            return false;
        }
        expiry = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return true;
    }

    private void DeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete MP3 upload {Path}; cleanup will retry.", path);
        }
    }
}
