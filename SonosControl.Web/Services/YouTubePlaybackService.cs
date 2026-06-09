using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public sealed class YouTubePlaybackOptions
{
    public string? PublicBaseUrl { get; set; }
    public string? ArtifactDirectory { get; set; }
    public int SessionTtlMinutes { get; set; } = 30;
}

public sealed record ResolvedYouTubeSource(
    string OriginalUrl,
    string VideoUrl,
    string DirectAudioUrl,
    string Title,
    bool IsPlaylist);

public sealed record TranscodedAudioStream(Stream Stream, Func<ValueTask> DisposeAsyncAction);

public interface IYouTubeToolRunner
{
    Task<ResolvedYouTubeSource> ResolveAsync(string sourceUrl, CancellationToken cancellationToken);
    Task<string> MaterializeAudioAsync(ResolvedYouTubeSource source, string artifactDirectory, CancellationToken cancellationToken);
    Task<TranscodedAudioStream> OpenTranscodedStreamAsync(ResolvedYouTubeSource source, CancellationToken cancellationToken);
}

public sealed class YouTubeToolRunner : IYouTubeToolRunner
{
    public async Task<ResolvedYouTubeSource> ResolveAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeYouTubeUrl(sourceUrl);
        var isPlaylist = IsPlaylistUrl(normalizedUrl);
        if (isPlaylist)
        {
            normalizedUrl = await ResolveFirstPlaylistItemUrlAsync(normalizedUrl, cancellationToken);
        }

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

        return new ResolvedYouTubeSource(
            sourceUrl.Trim(),
            string.IsNullOrWhiteSpace(webpageUrl) ? normalizedUrl : webpageUrl.Trim(),
            directAudioUrl.Trim(),
            string.IsNullOrWhiteSpace(title) ? normalizedUrl : title.Trim(),
            isPlaylist);
    }

    public async Task<string> MaterializeAudioAsync(ResolvedYouTubeSource source, string artifactDirectory, CancellationToken cancellationToken)
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
            try
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
            catch
            {
            }

            throw;
        }

        return outputPath;
    }

    public async Task<TranscodedAudioStream> OpenTranscodedStreamAsync(ResolvedYouTubeSource source, CancellationToken cancellationToken)
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

    private static bool IsPlaylistUrl(string url)
        => url.Contains("list=", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeYouTubeUrl(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new InvalidOperationException("A YouTube URL is required.");
        }

        var trimmed = sourceUrl.Trim();
        if (trimmed.StartsWith("https://youtu.be/", StringComparison.OrdinalIgnoreCase))
        {
            var id = trimmed["https://youtu.be/".Length..].Split('?', '#')[0];
            return $"https://www.youtube.com/watch?v={id}";
        }

        if (trimmed.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        throw new InvalidOperationException("Only YouTube video and playlist URLs are supported.");
    }

    private static async Task<string> ResolveFirstPlaylistItemUrlAsync(string playlistUrl, CancellationToken cancellationToken)
    {
        var json = await RunProcessCaptureStdoutAsync(
            "yt-dlp",
            args =>
            {
                args.Add("--flat-playlist");
                args.Add("--dump-single-json");
                args.Add("--no-warnings");
                args.Add("--playlist-items");
                args.Add("1");
                args.Add(playlistUrl);
            },
            cancellationToken);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("entries", out var entriesElement) || entriesElement.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("The YouTube playlist did not contain any playable entries.");
        }

        var entry = entriesElement[0];
        var id = entry.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var url = entry.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
        var webpageUrl = entry.TryGetProperty("webpage_url", out var webpageUrlElement) ? webpageUrlElement.GetString() : null;

        if (!string.IsNullOrWhiteSpace(webpageUrl))
        {
            return webpageUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return url.Trim();
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            return $"https://www.youtube.com/watch?v={id.Trim()}";
        }

        throw new InvalidOperationException("The YouTube playlist did not contain a usable first item.");
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
}

public sealed class YouTubePlaybackService : IYouTubePlaybackService
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IYouTubeToolRunner _toolRunner;
    private readonly ILogger<YouTubePlaybackService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _publicBaseUrl;
    private readonly string _artifactDirectory;
    private readonly TimeSpan _sessionTtl;

    public YouTubePlaybackService(
        IYouTubeToolRunner toolRunner,
        IOptions<YouTubePlaybackOptions> options,
        IWebHostEnvironment environment,
        ILogger<YouTubePlaybackService> logger,
        TimeProvider? timeProvider = null)
    {
        _toolRunner = toolRunner;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var settings = options.Value;
        _publicBaseUrl = (settings.PublicBaseUrl ?? string.Empty).Trim().TrimEnd('/');

        _artifactDirectory = string.IsNullOrWhiteSpace(settings.ArtifactDirectory)
            ? Path.Combine(environment.ContentRootPath, "artifacts", "youtube-audio")
            : settings.ArtifactDirectory;
        _sessionTtl = TimeSpan.FromMinutes(Math.Max(5, settings.SessionTtlMinutes));
    }

    public async Task<YouTubePlaybackSession> PreparePlaybackAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        var resolved = await _toolRunner.ResolveAsync(sourceUrl, cancellationToken);
        var sessionId = Guid.NewGuid().ToString("N");
        var session = new SessionState
        {
            SessionId = sessionId,
            Resolved = resolved,
            LastAccessUtc = _timeProvider.GetUtcNow(),
            ExpiresUtc = _timeProvider.GetUtcNow().Add(_sessionTtl),
            PreferTempFile = resolved.IsPlaylist
        };

        _sessions[sessionId] = session;

        _logger.LogInformation("Prepared YouTube playback session {SessionId} for {Title}. Playlist: {IsPlaylist}", sessionId, resolved.Title, resolved.IsPlaylist);

        return new YouTubePlaybackSession
        {
            SessionId = sessionId,
            StreamUrl = $"{GetRequiredPublicBaseUrl()}/api/youtube-audio/{sessionId}",
            Title = resolved.Title,
            UsesTempFile = session.PreferTempFile
        };
    }

    public async Task<YouTubePlaybackOpenResult?> OpenPlaybackAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        session.LastAccessUtc = _timeProvider.GetUtcNow();
        session.ExpiresUtc = _timeProvider.GetUtcNow().Add(_sessionTtl);

        if (!string.IsNullOrWhiteSpace(session.TempFilePath) && File.Exists(session.TempFilePath))
        {
            return new YouTubePlaybackOpenResult
            {
                FilePath = session.TempFilePath,
                ContentType = "audio/mpeg"
            };
        }

        if (session.PreferTempFile)
        {
            session.TempFilePath = await _toolRunner.MaterializeAudioAsync(session.Resolved, _artifactDirectory, cancellationToken);
            return new YouTubePlaybackOpenResult
            {
                FilePath = session.TempFilePath,
                ContentType = "audio/mpeg"
            };
        }

        try
        {
            var transcoded = await _toolRunner.OpenTranscodedStreamAsync(session.Resolved, cancellationToken);
            return new YouTubePlaybackOpenResult
            {
                Stream = transcoded.Stream,
                ContentType = "audio/mpeg",
                DisposeAsyncAction = transcoded.DisposeAsyncAction
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live YouTube stream failed for session {SessionId}. Falling back to temp file.", sessionId);
            session.TempFilePath = await _toolRunner.MaterializeAudioAsync(session.Resolved, _artifactDirectory, cancellationToken);
            session.PreferTempFile = true;
            return new YouTubePlaybackOpenResult
            {
                FilePath = session.TempFilePath,
                ContentType = "audio/mpeg"
            };
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
                TryDeleteTempFile(removed.TempFilePath);
                _logger.LogInformation("Removed expired YouTube playback session {SessionId}.", removed.SessionId);
            }
        }

        return Task.CompletedTask;
    }

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

    private string GetRequiredPublicBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(_publicBaseUrl))
        {
            return _publicBaseUrl;
        }

        throw new InvalidOperationException("Playback:PublicBaseUrl must be configured for YouTube playback so Sonos can reach the app.");
    }

    private sealed class SessionState
    {
        public string SessionId { get; init; } = string.Empty;
        public ResolvedYouTubeSource Resolved { get; init; } = null!;
        public bool PreferTempFile { get; set; }
        public string? TempFilePath { get; set; }
        public DateTimeOffset LastAccessUtc { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
    }
}
