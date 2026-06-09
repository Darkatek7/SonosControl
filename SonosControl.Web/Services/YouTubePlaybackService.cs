using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public sealed class YouTubePlaybackOptions
{
    public string? PublicBaseUrl { get; set; }
    public string? ArtifactDirectory { get; set; }
    public int SessionTtlMinutes { get; set; } = 10;
    public int QueueTopUpThreshold { get; set; } = 2;
    public int QueueTopUpBatchSize { get; set; } = 5;
    public int MaxAutoQueueItemsPerSession { get; set; } = 100;
}

public sealed record ResolvedYouTubeSourceItem(
    string VideoUrl,
    string DirectAudioUrl,
    string Title);

public sealed record ResolvedYouTubeQueue(
    string OriginalUrl,
    string Title,
    IReadOnlyList<ResolvedYouTubeSourceItem> Items,
    YouTubePlaybackMode PlaybackMode,
    bool IsPlaylist);

public sealed record TranscodedAudioStream(Stream Stream, Func<ValueTask> DisposeAsyncAction);

public interface IYouTubeToolRunner
{
    Task<ResolvedYouTubeQueue> ResolveQueueAsync(
        string sourceUrl,
        YouTubePlaybackMode? playbackMode,
        int preferredQueueLength,
        CancellationToken cancellationToken);
    Task<string> MaterializeAudioAsync(ResolvedYouTubeSourceItem source, string artifactDirectory, CancellationToken cancellationToken);
    Task<TranscodedAudioStream> OpenTranscodedStreamAsync(ResolvedYouTubeSourceItem source, CancellationToken cancellationToken);
}

public sealed class YouTubeToolRunner : IYouTubeToolRunner
{
    private const int MaxPlaylistItems = 250;

    public async Task<ResolvedYouTubeQueue> ResolveQueueAsync(
        string sourceUrl,
        YouTubePlaybackMode? playbackMode,
        int preferredQueueLength,
        CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeYouTubeUrl(sourceUrl);
        var effectiveMode = DeterminePlaybackMode(normalizedUrl, playbackMode);

        return effectiveMode switch
        {
            YouTubePlaybackMode.Single => await ResolveSingleQueueAsync(sourceUrl, normalizedUrl, cancellationToken),
            YouTubePlaybackMode.PlaylistOrdered => await ResolvePlaylistQueueAsync(sourceUrl, normalizedUrl, effectiveMode, preferredQueueLength, cancellationToken),
            YouTubePlaybackMode.PlaylistShuffle => await ResolvePlaylistQueueAsync(sourceUrl, normalizedUrl, effectiveMode, preferredQueueLength, cancellationToken),
            _ => await ResolveRelatedQueueAsync(sourceUrl, normalizedUrl, Math.Max(1, preferredQueueLength), cancellationToken)
        };
    }

    public async Task<string> MaterializeAudioAsync(ResolvedYouTubeSourceItem source, string artifactDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(artifactDirectory);
        var fileName = $"{Guid.NewGuid():N}.mp3";
        var outputPath = Path.Combine(artifactDirectory, fileName);

        try
        {
            await RunProcessCaptureStdoutAsync(
                "ffmpeg",
                args =>
                {
                    args.Add("-hide_banner");
                    args.Add("-loglevel");
                    args.Add("error");
                    args.Add("-y");
                    args.Add("-i");
                    args.Add(source.DirectAudioUrl);
                    args.Add("-vn");
                    args.Add("-acodec");
                    args.Add("libmp3lame");
                    args.Add("-b:a");
                    args.Add("192k");
                    args.Add("-f");
                    args.Add("mp3");
                    args.Add(outputPath);
                },
                cancellationToken,
                allowEmptyStdout: true);
        }
        catch
        {
            TryDeleteFile(outputPath);
            throw;
        }

        return outputPath;
    }

    public async Task<TranscodedAudioStream> OpenTranscodedStreamAsync(ResolvedYouTubeSourceItem source, CancellationToken cancellationToken)
    {
        var startInfo = CreateProcessStartInfo("ffmpeg", args =>
        {
            args.Add("-hide_banner");
            args.Add("-loglevel");
            args.Add("error");
            args.Add("-i");
            args.Add(source.DirectAudioUrl);
            args.Add("-vn");
            args.Add("-acodec");
            args.Add("libmp3lame");
            args.Add("-b:a");
            args.Add("192k");
            args.Add("-f");
            args.Add("mp3");
            args.Add("pipe:1");
        });

        var process = StartProcess(startInfo, "ffmpeg");
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.Yield();

        return new TranscodedAudioStream(
            process.StandardOutput.BaseStream,
            async () =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                try
                {
                    await stderrTask;
                }
                catch
                {
                }

                process.Dispose();
            });
    }

    private static async Task<ResolvedYouTubeQueue> ResolveSingleQueueAsync(string originalUrl, string normalizedUrl, CancellationToken cancellationToken)
    {
        var item = await ResolveSingleItemAsync(normalizedUrl, cancellationToken);
        return new ResolvedYouTubeQueue(originalUrl.Trim(), item.Title, new[] { item }, YouTubePlaybackMode.Single, false);
    }

    private static async Task<ResolvedYouTubeQueue> ResolvePlaylistQueueAsync(
        string originalUrl,
        string normalizedUrl,
        YouTubePlaybackMode playbackMode,
        int preferredQueueLength,
        CancellationToken cancellationToken)
    {
        var playlist = await ResolvePlaylistEntriesAsync(normalizedUrl, Math.Min(Math.Max(1, preferredQueueLength), MaxPlaylistItems), cancellationToken);
        if (playlist.Urls.Count == 0)
        {
            throw new InvalidOperationException("The YouTube playlist did not contain any playable entries.");
        }

        var items = await ResolvePlayableItemsAsync(playlist.Urls, cancellationToken);
        if (playbackMode == YouTubePlaybackMode.PlaylistShuffle)
        {
            items = Shuffle(items, $"{originalUrl}|initial");
        }

        var title = string.IsNullOrWhiteSpace(playlist.Title) ? items[0].Title : playlist.Title!;
        return new ResolvedYouTubeQueue(originalUrl.Trim(), title, items, playbackMode, true);
    }

    private static async Task<ResolvedYouTubeQueue> ResolveRelatedQueueAsync(
        string originalUrl,
        string normalizedUrl,
        int preferredQueueLength,
        CancellationToken cancellationToken)
    {
        var seedItem = await ResolveSingleItemAsync(normalizedUrl, cancellationToken);
        var queueUrls = new List<string> { seedItem.VideoUrl };

        var radioUrl = BuildRadioUrl(seedItem.VideoUrl);
        if (!string.IsNullOrWhiteSpace(radioUrl))
        {
            try
            {
                var radioQueue = await ResolvePlaylistEntriesAsync(radioUrl, preferredQueueLength + 1, cancellationToken);
                queueUrls.AddRange(radioQueue.Urls);
            }
            catch
            {
            }
        }

        var orderedUrls = queueUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(preferredQueueLength + 1)
            .ToList();

        var items = new List<ResolvedYouTubeSourceItem> { seedItem };
        foreach (var url in orderedUrls.Skip(1))
        {
            try
            {
                items.Add(await ResolveSingleItemAsync(url, cancellationToken));
            }
            catch
            {
            }
        }

        return new ResolvedYouTubeQueue(originalUrl.Trim(), seedItem.Title, items, YouTubePlaybackMode.AutoQueueRelated, items.Count > 1);
    }

    private static List<ResolvedYouTubeSourceItem> Shuffle(IReadOnlyList<ResolvedYouTubeSourceItem> items, string seed)
    {
        var shuffled = items.ToList();
        var random = new Random(StringComparer.OrdinalIgnoreCase.GetHashCode(seed));

        for (var index = shuffled.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (shuffled[index], shuffled[swapIndex]) = (shuffled[swapIndex], shuffled[index]);
        }

        return shuffled;
    }

    private static async Task<List<ResolvedYouTubeSourceItem>> ResolvePlayableItemsAsync(IEnumerable<string> urls, CancellationToken cancellationToken)
    {
        var items = new List<ResolvedYouTubeSourceItem>();
        foreach (var url in urls)
        {
            try
            {
                items.Add(await ResolveSingleItemAsync(url, cancellationToken));
            }
            catch
            {
                if (items.Count == 0)
                {
                    throw;
                }
            }
        }

        if (items.Count == 0)
        {
            throw new InvalidOperationException("No playable YouTube items could be resolved.");
        }

        return items;
    }

    private static async Task<ResolvedYouTubeSourceItem> ResolveSingleItemAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeYouTubeUrl(sourceUrl);
        var json = await RunProcessCaptureStdoutAsync(
            "yt-dlp",
            args =>
            {
                args.Add("-f");
                args.Add("bestaudio/best");
                args.Add("--dump-single-json");
                args.Add("--no-warnings");
                args.Add("--no-playlist");
                args.Add(normalizedUrl);
            },
            cancellationToken);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var directAudioUrl = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
        var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
        var webpageUrl = root.TryGetProperty("webpage_url", out var webpageUrlElement) ? webpageUrlElement.GetString() : null;

        if (string.IsNullOrWhiteSpace(directAudioUrl))
        {
            throw new InvalidOperationException("yt-dlp did not return a playable audio URL for the requested YouTube item.");
        }

        return new ResolvedYouTubeSourceItem(
            string.IsNullOrWhiteSpace(webpageUrl) ? normalizedUrl : webpageUrl.Trim(),
            directAudioUrl.Trim(),
            string.IsNullOrWhiteSpace(title) ? normalizedUrl : title.Trim());
    }

    private static async Task<(string? Title, List<string> Urls)> ResolvePlaylistEntriesAsync(string playlistUrl, int maxItems, CancellationToken cancellationToken)
    {
        var json = await RunProcessCaptureStdoutAsync(
            "yt-dlp",
            args =>
            {
                args.Add("--flat-playlist");
                args.Add("--dump-single-json");
                args.Add("--no-warnings");
                args.Add("--playlist-end");
                args.Add(maxItems.ToString());
                args.Add(playlistUrl);
            },
            cancellationToken);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString()?.Trim() : null;
        var urls = new List<string>();

        if (!root.TryGetProperty("entries", out var entriesElement) || entriesElement.ValueKind != JsonValueKind.Array)
        {
            return (title, urls);
        }

        foreach (var entry in entriesElement.EnumerateArray())
        {
            var url = entry.TryGetProperty("webpage_url", out var webpageUrlElement) ? webpageUrlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) && entry.TryGetProperty("url", out var urlElement))
            {
                url = urlElement.GetString();
            }

            if (string.IsNullOrWhiteSpace(url) && entry.TryGetProperty("id", out var idElement))
            {
                var id = idElement.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    url = $"https://www.youtube.com/watch?v={id.Trim()}";
                }
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                urls.Add(NormalizeYouTubeUrl(url.Trim()));
            }
        }

        return (title, urls);
    }

    private static YouTubePlaybackMode DeterminePlaybackMode(string normalizedUrl, YouTubePlaybackMode? requestedMode)
    {
        if (requestedMode.HasValue)
        {
            return requestedMode.Value;
        }

        return HasPlaylistContext(normalizedUrl)
            ? YouTubePlaybackMode.PlaylistOrdered
            : YouTubePlaybackMode.AutoQueueRelated;
    }

    private static bool HasPlaylistContext(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Contains("list=", StringComparison.OrdinalIgnoreCase);
        }

        return uri.Query.Contains("list=", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.Contains("/playlist", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeYouTubeUrl(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new InvalidOperationException("A YouTube URL is required.");
        }

        var trimmed = sourceUrl.Trim();
        if (trimmed.StartsWith("https://youtu.be/", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(trimmed);
            var id = uri.AbsolutePath.Trim('/').Split('/')[0];
            var query = ParseQueryParameters(uri.Query);
            query["v"] = id;
            return BuildWatchUrl(query);
        }

        if (trimmed.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var query = ParseQueryParameters(uri.Query);
                if (query.TryGetValue("v", out var videoId) && !string.IsNullOrWhiteSpace(videoId))
                {
                    query["v"] = videoId.Trim();
                    return BuildWatchUrl(query);
                }

                if (uri.AbsolutePath.Contains("/playlist", StringComparison.OrdinalIgnoreCase) && query.Count > 0)
                {
                    var queryString = string.Join("&", query.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
                    return $"https://www.youtube.com/playlist?{queryString}";
                }
            }

            return trimmed;
        }

        throw new InvalidOperationException("Only YouTube video and playlist URLs are supported.");
    }

    private static Dictionary<string, string> ParseQueryParameters(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .ToDictionary(parts => parts[0], parts => Uri.UnescapeDataString(parts[1]), StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildWatchUrl(Dictionary<string, string> query)
    {
        var orderedKeys = new[] { "v", "list", "start_radio" };
        var queryParts = new List<string>();

        foreach (var key in orderedKeys)
        {
            if (query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                queryParts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }

        foreach (var pair in query.Where(pair => !orderedKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                queryParts.Add($"{pair.Key}={Uri.EscapeDataString(pair.Value)}");
            }
        }

        return queryParts.Count == 0
            ? "https://www.youtube.com/watch"
            : $"https://www.youtube.com/watch?{string.Join("&", queryParts)}";
    }

    private static string? BuildRadioUrl(string videoUrl)
    {
        if (!Uri.TryCreate(videoUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = ParseQueryParameters(uri.Query);
        if (!query.TryGetValue("v", out var videoId) || string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        query["list"] = $"RD{videoId.Trim()}";
        query["start_radio"] = "1";
        return BuildWatchUrl(query);
    }

    private static async Task<string> RunProcessCaptureStdoutAsync(
        string fileName,
        Action<Collection<string>> configureArguments,
        CancellationToken cancellationToken,
        bool allowEmptyStdout = false)
    {
        var startInfo = CreateProcessStartInfo(fileName, configureArguments);
        using var process = StartProcess(startInfo, fileName);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed: {stderr.Trim()}");
        }

        if (!allowEmptyStdout && string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException($"{fileName} did not return any output.");
        }

        return stdout;
    }

    private static ProcessStartInfo CreateProcessStartInfo(string fileName, Action<Collection<string>> configureArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        configureArguments(startInfo.ArgumentList);
        return startInfo;
    }

    private static Process StartProcess(ProcessStartInfo startInfo, string toolName)
    {
        try
        {
            return Process.Start(startInfo)
                   ?? throw new InvalidOperationException($"Failed to start {toolName}.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"{toolName} is required for YouTube playback but is not available.", ex);
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

public sealed class YouTubePlaybackService : IYouTubePlaybackService
{
    private const int DefaultRelatedQueueLength = 10;
    private const int QueueInspectionWindow = 100;

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IYouTubeToolRunner _toolRunner;
    private readonly ILogger<YouTubePlaybackService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _publicBaseUrl;
    private readonly string _artifactDirectory;
    private readonly TimeSpan _sessionTtl;
    private readonly int _queueTopUpThreshold;
    private readonly int _queueTopUpBatchSize;
    private readonly int _maxAutoQueueItemsPerSession;

    public YouTubePlaybackService(
        IYouTubeToolRunner toolRunner,
        IOptions<YouTubePlaybackOptions> options,
        IWebHostEnvironment environment,
        IServiceScopeFactory scopeFactory,
        ILogger<YouTubePlaybackService> logger,
        TimeProvider? timeProvider = null)
    {
        _toolRunner = toolRunner;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var settings = options.Value;
        _publicBaseUrl = (settings.PublicBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        _artifactDirectory = string.IsNullOrWhiteSpace(settings.ArtifactDirectory)
            ? Path.Combine(environment.ContentRootPath, "artifacts", "youtube-audio")
            : settings.ArtifactDirectory;
        _sessionTtl = TimeSpan.FromMinutes(Math.Max(5, settings.SessionTtlMinutes));
        _queueTopUpThreshold = Math.Max(1, settings.QueueTopUpThreshold);
        _queueTopUpBatchSize = Math.Max(1, settings.QueueTopUpBatchSize);
        _maxAutoQueueItemsPerSession = Math.Max(_queueTopUpBatchSize, settings.MaxAutoQueueItemsPerSession);
    }

    public async Task<YouTubePlaybackSession> PreparePlaybackAsync(
        string sourceUrl,
        YouTubePlaybackMode? playbackMode = null,
        int? preferredQueueLength = null,
        CancellationToken cancellationToken = default)
    {
        var initialQueueLength = preferredQueueLength ?? DefaultRelatedQueueLength;
        var resolved = await _toolRunner.ResolveQueueAsync(
            sourceUrl,
            playbackMode,
            initialQueueLength,
            cancellationToken);

        if (resolved.Items.Count == 0)
        {
            throw new InvalidOperationException("YouTube playback did not resolve any playable items.");
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var now = _timeProvider.GetUtcNow();
        var preferTempFiles = resolved.Items.Count > 1 || resolved.IsPlaylist;
        var shuffleSeed = $"{sourceUrl.Trim()}|{sessionId}";
        var items = resolved.Items
            .Select((item, index) => new SessionItemState
            {
                Index = index,
                Resolved = item,
                PreferTempFile = preferTempFiles
            })
            .ToList();

        var session = new SessionState
        {
            SessionId = sessionId,
            SourceUrl = sourceUrl.Trim(),
            Title = resolved.Title,
            PlaybackMode = resolved.PlaybackMode,
            PreferredQueueLength = initialQueueLength,
            AutoExtendEnabled = resolved.PlaybackMode != YouTubePlaybackMode.Single,
            ContinuationUrl = items.Last().Resolved.VideoUrl,
            ShuffleSeed = shuffleSeed,
            LastAccessUtc = now,
            ExpiresUtc = now.Add(_sessionTtl),
            Status = SessionLifecycleState.Pending
        };

        session.Items.AddRange(items);
        foreach (var item in items)
        {
            session.KnownVideoUrls.Add(item.Resolved.VideoUrl);
        }

        _sessions[sessionId] = session;

        var queueItems = items.Select(item => ToQueueItem(sessionId, item)).ToList();
        _logger.LogInformation(
            "Prepared YouTube playback session {SessionId} with {ItemCount} item(s) using mode {PlaybackMode}.",
            sessionId,
            queueItems.Count,
            resolved.PlaybackMode);

        return new YouTubePlaybackSession
        {
            SessionId = sessionId,
            StreamUrl = queueItems[0].StreamUrl,
            Title = resolved.Title,
            UsesTempFile = queueItems.Any(item => item.UsesTempFile),
            PlaybackMode = resolved.PlaybackMode,
            QueueItems = queueItems
        };
    }

    public Task ActivateSessionAsync(string sessionId, string speakerIp, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            var now = _timeProvider.GetUtcNow();
            session.SpeakerIp = speakerIp.Trim();
            session.Status = SessionLifecycleState.Active;
            session.LastAccessUtc = now;
            session.ExpiresUtc = now.Add(_sessionTtl);
            _logger.LogInformation("Activated YouTube playback session {SessionId} on speaker {SpeakerIp}.", sessionId, speakerIp);
        }

        return Task.CompletedTask;
    }

    public async Task<YouTubePlaybackOpenResult?> OpenPlaybackAsync(string sessionId, int itemIndex = 0, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        var item = session.Items.FirstOrDefault(candidate => candidate.Index == itemIndex);
        if (item is null)
        {
            return null;
        }

        session.LastAccessUtc = _timeProvider.GetUtcNow();
        if (session.Status == SessionLifecycleState.Active || session.Status == SessionLifecycleState.Pending)
        {
            session.ExpiresUtc = _timeProvider.GetUtcNow().Add(_sessionTtl);
        }

        if (!string.IsNullOrWhiteSpace(item.TempFilePath) && File.Exists(item.TempFilePath))
        {
            return new YouTubePlaybackOpenResult
            {
                FilePath = item.TempFilePath,
                ContentType = "audio/mpeg"
            };
        }

        if (item.PreferTempFile)
        {
            item.TempFilePath = await _toolRunner.MaterializeAudioAsync(item.Resolved, _artifactDirectory, cancellationToken);
            return new YouTubePlaybackOpenResult
            {
                FilePath = item.TempFilePath,
                ContentType = "audio/mpeg"
            };
        }

        try
        {
            var transcoded = await _toolRunner.OpenTranscodedStreamAsync(item.Resolved, cancellationToken);
            return new YouTubePlaybackOpenResult
            {
                Stream = transcoded.Stream,
                ContentType = "audio/mpeg",
                DisposeAsyncAction = transcoded.DisposeAsyncAction
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live YouTube stream failed for session {SessionId} item {ItemIndex}. Falling back to temp file.", sessionId, itemIndex);
            item.TempFilePath = await _toolRunner.MaterializeAudioAsync(item.Resolved, _artifactDirectory, cancellationToken);
            item.PreferTempFile = true;
            return new YouTubePlaybackOpenResult
            {
                FilePath = item.TempFilePath,
                ContentType = "audio/mpeg"
            };
        }
    }

    public async Task MaintainSessionsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var session in _sessions.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (session.Status is SessionLifecycleState.Pending or SessionLifecycleState.Expired or SessionLifecycleState.Failed)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(session.SpeakerIp))
            {
                continue;
            }

            try
            {
                await MaintainSessionAsync(session, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to maintain YouTube playback session {SessionId}.", session.SessionId);
            }
        }
    }

    public Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _sessions.ToArray())
        {
            if (pair.Value.ExpiresUtc > now)
            {
                continue;
            }

            if (_sessions.TryRemove(pair.Key, out var removed))
            {
                removed.Status = SessionLifecycleState.Expired;
                foreach (var item in removed.Items)
                {
                    TryDeleteTempFile(item.TempFilePath);
                }

                _logger.LogInformation("Removed expired YouTube playback session {SessionId}.", removed.SessionId);
            }
        }

        return Task.CompletedTask;
    }

    private async Task MaintainSessionAsync(SessionState session, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var repo = uow.ISonosConnectorRepo;
        var currentStation = await repo.GetCurrentStationAsync(session.SpeakerIp!, cancellationToken);

        if (!IsQueueTransport(currentStation))
        {
            MarkSessionInactive(session, SessionLifecycleState.Stopping, "speaker left queue transport");
            return;
        }

        var currentTrackNumber = await repo.GetCurrentTrackNumberAsync(session.SpeakerIp!, cancellationToken) ?? 1;
        var startIndex = Math.Max(0, currentTrackNumber - 1);
        var queue = await repo.GetQueue(session.SpeakerIp!, startIndex, QueueInspectionWindow, cancellationToken);
        var sessionQueueItems = queue.Items
            .Where(item => item.ResourceUri?.Contains(GetSessionUrlPrefix(session.SessionId), StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (sessionQueueItems.Count == 0)
        {
            MarkSessionInactive(session, SessionLifecycleState.Stopping, "session queue items no longer present");
            return;
        }

        if (!session.AutoExtendEnabled)
        {
            session.ExpiresUtc = _timeProvider.GetUtcNow().Add(_sessionTtl);
            return;
        }

        var remainingItems = sessionQueueItems.Count;
        if (remainingItems > _queueTopUpThreshold)
        {
            session.ExpiresUtc = _timeProvider.GetUtcNow().Add(_sessionTtl);
            return;
        }

        var appendedItems = await AppendMoreItemsAsync(session, repo, cancellationToken);
        if (appendedItems > 0)
        {
            session.ExpiresUtc = _timeProvider.GetUtcNow().Add(_sessionTtl);
            _logger.LogInformation(
                "Extended YouTube playback session {SessionId} on speaker {SpeakerIp} by {AppendedItemCount} item(s).",
                session.SessionId,
                session.SpeakerIp,
                appendedItems);
            return;
        }

        if (remainingItems == 0)
        {
            MarkSessionInactive(session, SessionLifecycleState.Completed, "queue exhausted with no additional items");
            return;
        }

        session.ExpiresUtc = _timeProvider.GetUtcNow().Add(_sessionTtl);
    }

    private async Task<int> AppendMoreItemsAsync(SessionState session, ISonosConnectorRepo repo, CancellationToken cancellationToken)
    {
        if (session.Items.Count >= _maxAutoQueueItemsPerSession)
        {
            if (session.Status == SessionLifecycleState.Active)
            {
                session.Status = SessionLifecycleState.Completed;
                session.ExpiresUtc = _timeProvider.GetUtcNow().Add(_sessionTtl);
            }

            return 0;
        }

        var remainingCapacity = _maxAutoQueueItemsPerSession - session.Items.Count;
        var targetAppendCount = Math.Min(_queueTopUpBatchSize, remainingCapacity);
        if (targetAppendCount <= 0)
        {
            return 0;
        }

        var additionalItems = await ResolveAdditionalItemsAsync(session, targetAppendCount, cancellationToken);
        if (additionalItems.Count == 0)
        {
            return 0;
        }

        var nextIndex = session.Items.Count;
        foreach (var resolved in additionalItems)
        {
            var state = new SessionItemState
            {
                Index = nextIndex++,
                Resolved = resolved,
                PreferTempFile = session.PlaybackMode != YouTubePlaybackMode.Single
            };

            session.Items.Add(state);
            session.KnownVideoUrls.Add(resolved.VideoUrl);
            session.ContinuationUrl = resolved.VideoUrl;

            var queueItem = ToQueueItem(session.SessionId, state);
            await repo.AddUriToQueue(session.SpeakerIp!, queueItem.StreamUrl, CreateYouTubeQueueMetadata(queueItem.Title, queueItem.StreamUrl), false, cancellationToken);
        }

        return additionalItems.Count;
    }

    private async Task<List<ResolvedYouTubeSourceItem>> ResolveAdditionalItemsAsync(SessionState session, int targetAppendCount, CancellationToken cancellationToken)
    {
        var targetResolveCount = Math.Min(_maxAutoQueueItemsPerSession, session.Items.Count + Math.Max(targetAppendCount, session.PreferredQueueLength));

        return session.PlaybackMode switch
        {
            YouTubePlaybackMode.PlaylistOrdered => await ResolveUnseenPlaylistItemsAsync(session, targetResolveCount, ordered: true, cancellationToken),
            YouTubePlaybackMode.PlaylistShuffle => await ResolveUnseenPlaylistItemsAsync(session, targetResolveCount, ordered: false, cancellationToken),
            YouTubePlaybackMode.AutoQueueRelated => await ResolveRelatedContinuationItemsAsync(session, targetAppendCount, cancellationToken),
            _ => new List<ResolvedYouTubeSourceItem>()
        };
    }

    private async Task<List<ResolvedYouTubeSourceItem>> ResolveUnseenPlaylistItemsAsync(
        SessionState session,
        int targetResolveCount,
        bool ordered,
        CancellationToken cancellationToken)
    {
        var resolved = await _toolRunner.ResolveQueueAsync(session.SourceUrl, YouTubePlaybackMode.PlaylistOrdered, targetResolveCount, cancellationToken);
        var unseen = resolved.Items
            .Where(item => !session.KnownVideoUrls.Contains(item.VideoUrl))
            .ToList();

        if (!ordered && unseen.Count > 0)
        {
            unseen = ShuffleItems(unseen, $"{session.ShuffleSeed}|{session.Items.Count}");
        }

        return unseen.Take(_queueTopUpBatchSize).ToList();
    }

    private async Task<List<ResolvedYouTubeSourceItem>> ResolveRelatedContinuationItemsAsync(SessionState session, int targetAppendCount, CancellationToken cancellationToken)
    {
        var continuationUrl = session.ContinuationUrl ?? session.Items.LastOrDefault()?.Resolved.VideoUrl ?? session.SourceUrl;
        var resolved = await _toolRunner.ResolveQueueAsync(continuationUrl, YouTubePlaybackMode.AutoQueueRelated, Math.Max(targetAppendCount, session.PreferredQueueLength), cancellationToken);
        return resolved.Items
            .Where(item => !session.KnownVideoUrls.Contains(item.VideoUrl))
            .Take(targetAppendCount)
            .ToList();
    }

    private static List<ResolvedYouTubeSourceItem> ShuffleItems(IReadOnlyList<ResolvedYouTubeSourceItem> items, string seed)
    {
        var shuffled = items.ToList();
        var random = new Random(StringComparer.OrdinalIgnoreCase.GetHashCode(seed));

        for (var index = shuffled.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (shuffled[index], shuffled[swapIndex]) = (shuffled[swapIndex], shuffled[index]);
        }

        return shuffled;
    }

    private void MarkSessionInactive(SessionState session, SessionLifecycleState newState, string reason)
    {
        if (session.Status == newState)
        {
            return;
        }

        session.Status = newState;
        session.ExpiresUtc = _timeProvider.GetUtcNow().Add(_sessionTtl);
        _logger.LogInformation("Marked YouTube playback session {SessionId} as {State}: {Reason}", session.SessionId, newState, reason);
    }

    private static bool IsQueueTransport(string currentStation)
        => !string.IsNullOrWhiteSpace(currentStation)
           && !currentStation.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
           && currentStation.Contains("x-rincon-queue:", StringComparison.OrdinalIgnoreCase);

    private YouTubePlaybackQueueItem ToQueueItem(string sessionId, SessionItemState item)
    {
        return new YouTubePlaybackQueueItem
        {
            Index = item.Index,
            Title = item.Resolved.Title,
            StreamUrl = $"{GetRequiredPublicBaseUrl()}/api/youtube-audio/{sessionId}/{item.Index}",
            UsesTempFile = item.PreferTempFile
        };
    }

    private static string CreateYouTubeQueueMetadata(string title, string uri)
    {
        var safeTitle = SecurityElement.Escape(string.IsNullOrWhiteSpace(title) ? "YouTube Audio" : title.Trim()) ?? "YouTube Audio";
        var safeUri = SecurityElement.Escape(uri.Trim()) ?? string.Empty;

        return $@"<DIDL-Lite xmlns:dc=""http://purl.org/dc/elements/1.1/""
                               xmlns:upnp=""urn:schemas-upnp-org:metadata-1-0/upnp/""
                               xmlns=""urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"">
                    <item id=""0"" parentID=""-1"" restricted=""true"">
                        <dc:title>{safeTitle}</dc:title>
                        <upnp:class>object.item.audioItem.musicTrack</upnp:class>
                        <res protocolInfo=""http-get:*:audio/mpeg:*"">{safeUri}</res>
                    </item>
                 </DIDL-Lite>";
    }

    private string GetRequiredPublicBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(_publicBaseUrl))
        {
            return _publicBaseUrl;
        }

        throw new InvalidOperationException("Playback:PublicBaseUrl must be configured for YouTube playback so Sonos can reach the app.");
    }

    private static string GetSessionUrlPrefix(string sessionId)
        => $"/api/youtube-audio/{sessionId}/";

    private static void TryDeleteTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private enum SessionLifecycleState
    {
        Pending = 0,
        Active = 1,
        Stopping = 2,
        Completed = 3,
        Failed = 4,
        Expired = 5
    }

    private sealed class SessionState
    {
        public string SessionId { get; init; } = string.Empty;
        public string SourceUrl { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public YouTubePlaybackMode PlaybackMode { get; init; }
        public int PreferredQueueLength { get; init; }
        public bool AutoExtendEnabled { get; init; }
        public string? SpeakerIp { get; set; }
        public string? ContinuationUrl { get; set; }
        public string ShuffleSeed { get; init; } = string.Empty;
        public SessionLifecycleState Status { get; set; }
        public List<SessionItemState> Items { get; } = new();
        public HashSet<string> KnownVideoUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset LastAccessUtc { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
    }

    private sealed class SessionItemState
    {
        public int Index { get; init; }
        public ResolvedYouTubeSourceItem Resolved { get; init; } = null!;
        public bool PreferTempFile { get; set; }
        public string? TempFilePath { get; set; }
    }
}
