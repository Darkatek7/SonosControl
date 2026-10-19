using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class ManualAutoStopServiceTests
{
    private const string SpeakerIp = "192.0.2.10";

    [Fact]
    public async Task SuccessfulManualStartBeforeCutoffArmsAtCutoff()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 7, 0, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(8, 0), ReleaseTime = new(12, 0) }, 30);
        using var fixture = new Fixture(settings, time);

        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);

        Assert.NotNull(await fixture.Service.GetNextStopAsync(SpeakerIp));
        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal(new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc), deadline.StopAtUtc);
        Assert.Equal("Daily auto-stop", deadline.Reason);
        Assert.Contains("2026-10-19", deadline.RuleKey);
    }

    [Fact]
    public async Task SuccessfulManualStartAfterCutoffUsesDurationBoundedByRelease()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 8, 30, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(8, 0), ReleaseTime = new(10, 0) }, 120);
        using var fixture = new Fixture(settings, time);
        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal(new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc), deadline.StopAtUtc);
    }

    [Fact]
    public async Task SuccessfulManualStartAt2310KeepsThirtyMinuteDuration()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 23, 10, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(22, 0), ReleaseTime = new(6, 0) }, 30);
        using var fixture = new Fixture(settings, time);

        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal(new DateTime(2026, 10, 19, 23, 40, 0, DateTimeKind.Utc), deadline.StopAtUtc);
    }

    [Fact]
    public async Task OvernightEarlyMorningUsesPreviousWeekdayRule()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero));
        var settings = Settings(
            new ManualAutoStopDay { Enabled = true, StopTime = new(22, 0), ReleaseTime = new(6, 0) },
            120,
            DayOfWeek.Monday);
        settings.Speakers[0].ManualAutoStopDays[DayOfWeek.Tuesday] = new ManualAutoStopDay
        {
            Enabled = true,
            StopTime = new(23, 0),
            ReleaseTime = new(5, 0)
        };
        using var fixture = new Fixture(settings, time);

        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal(new DateTime(2026, 10, 20, 4, 0, 0, DateTimeKind.Utc), deadline.StopAtUtc);
        Assert.Contains("2026-10-19", deadline.RuleKey);
    }

    [Fact]
    public async Task UntrackedExternalPlaybackDoesNotShowRollingDeadline()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 7, 30, 0, TimeSpan.Zero));
        using var fixture = new Fixture(Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(8, 0), ReleaseTime = new(12, 0) }, 30), time);

        Assert.Null(await fixture.Service.GetNextStopAsync(SpeakerIp));
    }

    [Fact]
    public async Task UntrackedExternalPlaybackWithNoDailyRuleOrDurationHasNoDeadline()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 7, 30, 0, TimeSpan.Zero));
        using var fixture = new Fixture(Settings(null, null), time);

        Assert.Null(await fixture.Service.GetNextStopAsync(SpeakerIp));
        Assert.Empty(await fixture.ReadDeadlinesAsync());
    }

    [Fact]
    public async Task SuccessfulAppStartWithoutEnabledRulesPersistsKnownSession()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 7, 30, 0, TimeSpan.Zero));
        using var fixture = new Fixture(Settings(null, null), time);

        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal("App playback session", deadline.Reason);
        Assert.Null(await fixture.Service.GetNextStopAsync(SpeakerIp));
    }

    [Fact]
    public async Task NoEnabledRuleKeepsKnownSessionWithoutArmingDurationOrPausing()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 7, 30, 0, TimeSpan.Zero));
        using var fixture = new Fixture(Settings(null, 30), time);

        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);
        time.Now = time.Now.AddMinutes(31);
        await fixture.Service.ProcessDueAsync();

        Assert.Null(await fixture.Service.GetNextStopAsync(SpeakerIp));
        Assert.Single(await fixture.ReadDeadlinesAsync());
        fixture.Connector.Verify(connector => connector.PausePlaying(SpeakerIp, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AmbiguousDaylightSavingCutoffUsesEarlierOccurrence()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(2, 30), ReleaseTime = new(4, 0) }, 60, DayOfWeek.Sunday);
        using var fixture = new Fixture(settings, time, "Europe/Vienna");

        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), deadline.StopAtUtc);
    }

    [Fact]
    public async Task InvalidDaylightSavingCutoffAdvancesToFirstValidLocalTime()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(2, 30), ReleaseTime = new(4, 0) }, 60, DayOfWeek.Sunday);
        using var fixture = new Fixture(settings, time, "Europe/Vienna");

        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), deadline.StopAtUtc);
    }

    [Fact]
    public async Task SuccessfulScheduleOccurrencePersistsItsEffectiveEnd()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
        var settings = Settings(null, null);
        var window = ScheduledWindow(new(9, 0), new(11, 0));
        settings.ScheduleWindows.Add(window);
        using var fixture = new Fixture(settings, time);

        var generations = await fixture.Service.RegisterScheduleOccurrenceAsync([SpeakerIp], window);

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal(new DateTime(2026, 10, 19, 11, 0, 0, DateTimeKind.Utc), deadline.StopAtUtc);
        Assert.Equal($"schedule:{window.Id}", deadline.RuleKey);
        Assert.Equal(1, generations[SpeakerIp]);
    }

    [Fact]
    public async Task StaleSchedulerGenerationCannotConsumeDeadlineAfterManualRestart()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(18, 0), ReleaseTime = new(20, 0) }, 30);
        var window = ScheduledWindow(new(9, 0), new(10, 0));
        settings.ScheduleWindows.Add(window);
        using var fixture = new Fixture(settings, time);
        var scheduledGeneration = await fixture.Service.RegisterScheduleOccurrenceAsync([SpeakerIp], window);

        time.Now = new DateTimeOffset(2026, 10, 19, 10, 30, 0, TimeSpan.Zero);
        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);
        time.Now = new DateTimeOffset(2026, 10, 19, 11, 1, 0, TimeSpan.Zero);

        var acknowledged = await fixture.Service.TryAcknowledgeScheduleStopAsync(
            [SpeakerIp], window.Id, new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero), scheduledGeneration);

        Assert.Empty(acknowledged);
        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal("Daily auto-stop", deadline.Reason);
        Assert.Equal(new DateTime(2026, 10, 19, 18, 0, 0, DateTimeKind.Utc), deadline.StopAtUtc);
    }

    [Fact]
    public async Task SchedulerAcknowledgementConsumesDueGenerationBeforeWorkerCanPauseIt()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
        var settings = Settings(null, null);
        var window = ScheduledWindow(new(9, 0), new(11, 0));
        settings.ScheduleWindows.Add(window);
        using var fixture = new Fixture(settings, time);
        var generations = await fixture.Service.RegisterScheduleOccurrenceAsync([SpeakerIp], window);
        time.Now = new DateTimeOffset(2026, 10, 19, 11, 1, 0, TimeSpan.Zero);

        var acknowledged = await fixture.Service.TryAcknowledgeScheduleStopAsync(
            [SpeakerIp], window.Id, new DateTimeOffset(2026, 10, 19, 11, 0, 0, TimeSpan.Zero), generations);
        await fixture.Service.ProcessDueAsync();

        Assert.Contains(SpeakerIp, acknowledged);
        Assert.Empty(await fixture.ReadDeadlinesAsync());
        fixture.Connector.Verify(connector => connector.PausePlaying(SpeakerIp, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScheduleStartedAfterDailyCutoffKeepsReleasedNightRuleAcrossScheduleStopAndManualRestart()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 22, 40, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(22, 0), ReleaseTime = new(6, 0) }, 30);
        var window = ScheduledWindow(new(22, 30), new(23, 30));
        settings.ScheduleWindows.Add(window);
        using var fixture = new Fixture(settings, time);
        var generations = await fixture.Service.RegisterScheduleOccurrenceAsync([SpeakerIp], window);

        time.Now = new DateTimeOffset(2026, 10, 19, 23, 31, 0, TimeSpan.Zero);
        var acknowledged = await fixture.Service.TryAcknowledgeScheduleStopAsync(
            [SpeakerIp], window.Id, new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.Zero), generations);
        Assert.Contains(SpeakerIp, acknowledged);

        time.Now = time.Now.AddMinutes(1);
        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal("App playback session", deadline.Reason);
        Assert.Equal("daily:2026-10-19:22:00-06:00", deadline.RuleKey);
    }

    [Fact]
    public async Task StoppedScheduleReleaseMarkerDoesNotArmNextDayForExternalPlayback()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 22, 40, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(22, 0), ReleaseTime = new(6, 0) }, 30);
        var window = ScheduledWindow(new(22, 30), new(23, 30));
        settings.ScheduleWindows.Add(window);
        using var fixture = new Fixture(settings, time);
        var generations = await fixture.Service.RegisterScheduleOccurrenceAsync([SpeakerIp], window);

        time.Now = new DateTimeOffset(2026, 10, 19, 23, 31, 0, TimeSpan.Zero);
        var acknowledged = await fixture.Service.TryAcknowledgeScheduleStopAsync(
            [SpeakerIp], window.Id, new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.Zero), generations);
        Assert.Contains(SpeakerIp, acknowledged);
        time.Now = new DateTimeOffset(2026, 10, 20, 6, 1, 0, TimeSpan.Zero);

        await fixture.Service.ProcessDueAsync();

        Assert.Empty(await fixture.ReadDeadlinesAsync());
        Assert.Null(await fixture.Service.GetNextStopAsync(SpeakerIp));
        fixture.Connector.Verify(connector => connector.PausePlaying(SpeakerIp, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScheduleBeforeDailyCutoffDoesNotReleaseThatNightRule()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 21, 10, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(22, 0), ReleaseTime = new(6, 0) }, 30);
        var window = ScheduledWindow(new(21, 0), new(23, 0));
        settings.ScheduleWindows.Add(window);
        using var fixture = new Fixture(settings, time);

        await fixture.Service.RegisterScheduleOccurrenceAsync([SpeakerIp], window);

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal("schedule:schedule-a", deadline.RuleKey);
    }

    [Fact]
    public async Task ScheduleThatStartedBeforeCutoffDoesNotReleaseUpcomingNightRule()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 21, 0, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(22, 0), ReleaseTime = new(6, 0) }, 30);
        var window = ScheduledWindow(new(21, 0), new(23, 0));
        settings.ScheduleWindows.Add(window);
        using var fixture = new Fixture(settings, time);
        await fixture.SeedDeadlineAsync(new ManualPlaybackDeadline
        {
            SpeakerIp = SpeakerIp,
            StartedAtUtc = time.Now.UtcDateTime,
            StopAtUtc = new DateTime(2026, 10, 19, 22, 0, 0, DateTimeKind.Utc),
            Reason = "Daily auto-stop",
            RuleKey = "daily:2026-10-19:22:00-06:00",
            Generation = 1
        });

        time.Now = new DateTimeOffset(2026, 10, 19, 22, 0, 0, TimeSpan.Zero);
        await fixture.Service.ProcessDueAsync();

        Assert.Empty(await fixture.ReadDeadlinesAsync());
        fixture.Connector.Verify(connector => connector.PausePlaying(SpeakerIp, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RestartDuringLongScheduleUsesOccurrenceStartForNightRuleEligibility()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 22, 10, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(22, 0), ReleaseTime = new(6, 0) }, 30);
        var window = ScheduledWindow(new(9, 0), new(23, 0));
        settings.ScheduleWindows.Add(window);
        using var fixture = new Fixture(settings, time);

        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);
        var firstDeadline = await fixture.ReadDeadlineAsync();
        Assert.Equal("Schedule: Schedule", firstDeadline.Reason);
        Assert.Equal(new DateTime(2026, 10, 19, 23, 0, 0, DateTimeKind.Utc), firstDeadline.StopAtUtc);
        time.Now = new DateTimeOffset(2026, 10, 19, 23, 10, 0, TimeSpan.Zero);
        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal("Daily auto-stop", deadline.Reason);
        Assert.Equal(new DateTime(2026, 10, 19, 23, 40, 0, DateTimeKind.Utc), deadline.StopAtUtc);
        Assert.Equal("daily:2026-10-19:22:00-06:00", deadline.RuleKey);
    }

    [Fact]
    public async Task DisabledPlaybackCannotWinScheduleEligibility()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
        var settings = Settings(null, 30);
        var disabled = ScheduledWindow(new(9, 0), new(11, 0));
        disabled.Playback!.Enabled = false;
        settings.ScheduleWindows.Add(disabled);
        using var fixture = new Fixture(settings, time);

        Assert.Empty(await fixture.Service.RegisterScheduleOccurrenceAsync([SpeakerIp], disabled));
        Assert.Empty(await fixture.ReadDeadlinesAsync());
    }

    [Fact]
    public async Task StoppedScheduleReleaseMarkerExpiresWithoutPausingPlayback()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        using var fixture = new Fixture(Settings(null, null), time);
        await fixture.SeedDeadlineAsync(new ManualPlaybackDeadline
        {
            SpeakerIp = SpeakerIp,
            StartedAtUtc = new DateTime(2026, 10, 19, 22, 0, 0, DateTimeKind.Utc),
            StopAtUtc = new DateTime(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc),
            Reason = "Daily rule released",
            RuleKey = null,
            Generation = 2
        });

        await fixture.Service.ProcessDueAsync();

        Assert.Empty(await fixture.ReadDeadlinesAsync());
        fixture.Connector.Verify(connector => connector.PausePlaying(SpeakerIp, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NewlyEnabledRuleArmsForPreviouslyReleasedSessionOnNextReconciliation()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 7, 30, 0, TimeSpan.Zero));
        var settings = Settings(null, null);
        using var fixture = new Fixture(settings, time);
        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);
        settings.Speakers[0].ManualAutoStopDays[DayOfWeek.Monday] =
            new ManualAutoStopDay { Enabled = true, StopTime = new(8, 0), ReleaseTime = new(12, 0) };

        await fixture.Service.ProcessDueAsync();

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal("Daily auto-stop", deadline.Reason);
        Assert.Equal(new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc), deadline.StopAtUtc);
    }

    [Fact]
    public async Task AStartedScheduleCancelsTheDailyRestrictionAndKeepsScheduleDeadline()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(8, 0), ReleaseTime = new(12, 0) }, 30);
        var window = ScheduledWindow(new(9, 0), new(11, 0));
        settings.ScheduleWindows.Add(window);
        using var fixture = new Fixture(settings, time);
        await fixture.SeedDeadlineAsync(new ManualPlaybackDeadline
        {
            SpeakerIp = SpeakerIp,
            StartedAtUtc = new DateTime(2026, 10, 19, 7, 0, 0, DateTimeKind.Utc),
            StopAtUtc = new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc),
            Reason = "Daily auto-stop",
            RuleKey = "daily:2026-10-19:08:00-12:00",
            Generation = 3
        });

        await fixture.Service.ProcessDueAsync();

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal("Schedule: Schedule", deadline.Reason);
        Assert.Equal(new DateTime(2026, 10, 19, 11, 0, 0, DateTimeKind.Utc), deadline.StopAtUtc);
        Assert.Contains("|released:daily:2026-10-19:08:00-12:00", deadline.RuleKey);
        fixture.Connector.Verify(connector => connector.PausePlaying(SpeakerIp, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActiveScheduleWithoutArmedRowReportsScheduleEnd()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
        var settings = Settings(null, null);
        settings.ScheduleWindows.Add(ScheduledWindow(new(9, 0), new(11, 0)));
        using var fixture = new Fixture(settings, time);

        var next = await fixture.Service.GetNextStopAsync(SpeakerIp);

        Assert.Equal(new DateTimeOffset(2026, 10, 19, 11, 0, 0, TimeSpan.Zero), next!.Value.StopAtUtc);
        Assert.Equal("Schedule: Schedule", next.Value.Reason);
        Assert.Empty(await fixture.ReadDeadlinesAsync());
    }

    [Fact]
    public async Task IdleRoomOutsideScheduleAndWithoutSessionHasNoFallbackStop()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(22, 0), ReleaseTime = new(6, 0) }, 30);
        settings.ScheduleWindows.Add(ScheduledWindow(new(9, 0), new(11, 0)));
        using var fixture = new Fixture(settings, time);

        Assert.Null(await fixture.Service.GetNextStopAsync(SpeakerIp));
    }

    [Fact]
    public async Task AppSessionPastReleaseReportsNextDailyCutoff()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 11, 0, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(8, 0), ReleaseTime = new(10, 0) }, 30);
        using var fixture = new Fixture(settings, time);
        await fixture.SeedDeadlineAsync(new ManualPlaybackDeadline
        {
            SpeakerIp = SpeakerIp,
            StartedAtUtc = new DateTime(2026, 10, 19, 8, 30, 0, DateTimeKind.Utc),
            StopAtUtc = new DateTime(2026, 10, 20, 11, 0, 0, DateTimeKind.Utc),
            Reason = "App playback session",
            RuleKey = "daily:2026-10-19:08:00-10:00",
            Generation = 2
        });

        var next = await fixture.Service.GetNextStopAsync(SpeakerIp);

        Assert.Equal(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero), next!.Value.StopAtUtc);
        Assert.Equal("Daily auto-stop", next.Value.Reason);
    }

    [Fact]
    public async Task FailedScheduleStopBacksOffInsteadOfRetryingEveryPoll()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 11, 0, 1, TimeSpan.Zero));
        var settings = Settings(null, null);
        settings.ScheduleWindows.Add(ScheduledWindow(new(9, 0), new(11, 0)));
        using var fixture = new Fixture(settings, time);
        await fixture.SeedDeadlineAsync(new ManualPlaybackDeadline
        {
            SpeakerIp = SpeakerIp,
            StartedAtUtc = new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc),
            StopAtUtc = new DateTime(2026, 10, 19, 11, 0, 0, DateTimeKind.Utc),
            Reason = "Schedule: Schedule",
            RuleKey = "schedule:schedule-a",
            Generation = 4
        });
        fixture.Connector.Setup(c => c.PausePlaying(SpeakerIp, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Offline"));

        await fixture.Service.ProcessDueAsync();
        time.Now = time.Now.AddSeconds(5);
        await fixture.Service.ProcessDueAsync();
        fixture.Connector.Verify(c => c.PausePlaying(SpeakerIp, It.IsAny<CancellationToken>()), Times.Once);

        time.Now = time.Now.AddSeconds(30);
        await fixture.Service.ProcessDueAsync();
        fixture.Connector.Verify(c => c.PausePlaying(SpeakerIp, It.IsAny<CancellationToken>()), Times.Exactly(2));
        var row = await fixture.ReadDeadlineAsync();
        Assert.Equal(4, row.Generation);
        Assert.Equal(new DateTime(2026, 10, 19, 11, 0, 0, DateTimeKind.Utc), row.StopAtUtc);
    }

    [Fact]
    public async Task SlowSpeakerStopDoesNotBlockOtherRoomsAndNewerSessionSurvives()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 10, 19, 7, 0, 0, TimeSpan.Zero));
        var settings = Settings(new ManualAutoStopDay { Enabled = true, StopTime = new(8, 0), ReleaseTime = new(12, 0) }, 30);
        settings.Speakers.Add(new SonosSpeaker { Name = "Other", IpAddress = "192.0.2.11" });
        using var fixture = new Fixture(settings, time);
        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]);
        var original = await fixture.ReadDeadlineAsync();
        time.Now = new DateTimeOffset(2026, 10, 19, 8, 30, 0, TimeSpan.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connector.Setup(c => c.PausePlaying(SpeakerIp, It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.SetResult(); await release.Task; });

        var processing = fixture.Service.ProcessDueAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Service.RegisterSuccessfulStartAsync(["192.0.2.11"]).WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Service.RegisterSuccessfulStartAsync([SpeakerIp]).WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(5));

        var rows = await fixture.ReadDeadlinesAsync();
        var survivor = Assert.Single(rows, row => row.SpeakerIp == SpeakerIp);
        Assert.True(survivor.Generation > original.Generation);
        Assert.Equal(new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc), survivor.StopAtUtc);
        Assert.Contains(rows, row => row.SpeakerIp == "192.0.2.11");
    }

    private static SonosSettings Settings(ManualAutoStopDay? rule, int? minutes, DayOfWeek day = DayOfWeek.Monday)
    {
        var settings = new SonosSettings
        {
            Speakers =
            [
                new SonosSpeaker
                {
                    Name = "Test", IpAddress = SpeakerIp, ManualAutoStopMinutes = minutes,
                    ManualAutoStopDays = rule is null ? new() : new() { [day] = rule }
                }
            ]
        };
        return settings;
    }

    private static ScheduleWindow ScheduledWindow(TimeOnly start, TimeOnly stop)
        => new()
        {
            Id = "schedule-a",
            Name = "Schedule",
            IsEnabled = true,
            StartTime = start,
            StopTime = stop,
            Playback = new Scene { SpeakerIps = [SpeakerIp] }
        };

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ApplicationDbContext _db;

        public Fixture(SonosSettings settings, TestTimeProvider time, string timeZone = "UTC")
        {
            var root = new ServiceCollection();
            var databaseName = Guid.NewGuid().ToString("N");
            root.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(databaseName));
            SettingsRepo = new Mock<ISettingsRepo>();
            SettingsRepo.Setup(repository => repository.GetSettings()).ReturnsAsync(settings);
            Connector = new Mock<ISonosConnectorRepo>();
            Connector.Setup(connector => connector.GetAllSpeakersInGroup(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string ip, CancellationToken _) => new[] { ip });
            root.AddSingleton(SettingsRepo.Object);
            root.AddSingleton(Connector.Object);
            _provider = root.BuildServiceProvider();
            _db = _provider.GetRequiredService<ApplicationDbContext>();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Automation:TimeZone"] = timeZone
            }).Build();
            Service = new ManualAutoStopService(_provider.GetRequiredService<IServiceScopeFactory>(),
                new PlaybackCommandCoordinator(time), time, configuration, NullLogger<ManualAutoStopService>.Instance);
        }

        public Mock<ISettingsRepo> SettingsRepo { get; }
        public Mock<ISonosConnectorRepo> Connector { get; }
        public ManualAutoStopService Service { get; }
        public Task SeedDeadlineAsync(ManualPlaybackDeadline deadline) => _db.ManualPlaybackDeadlines.AddAsync(deadline).AsTask().ContinueWith(async _ => await _db.SaveChangesAsync()).Unwrap();
        public async Task<ManualPlaybackDeadline> ReadDeadlineAsync()
            => await _db.ManualPlaybackDeadlines.AsNoTracking().SingleAsync();
        public async Task<List<ManualPlaybackDeadline>> ReadDeadlinesAsync()
            => await _db.ManualPlaybackDeadlines.AsNoTracking().ToListAsync();
        public void Dispose()
        {
            _db.Dispose();
            _provider.Dispose();
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = current;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
