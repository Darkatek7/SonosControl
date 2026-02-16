using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;
using Xunit.Sdk;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SonosControl.Tests;

public class SonosControlServiceTests
{
    private static DateTimeOffset LocalInstant(int year, int month, int day, int hour, int minute, int second)
    {
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    private static Task<(SonosSettings settings, DaySchedule? schedule, DateTimeOffset startTime)> InvokeWait(SonosControlService svc, IUnitOfWork uow, CancellationToken token)
    {
        var method = typeof(SonosControlService).GetMethod("WaitUntilStartTime", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<(SonosSettings, DaySchedule?, DateTimeOffset)>)method.Invoke(svc, new object[] { uow, token })!;
        return task;
    }

    private static async Task<(SonosSettings settings, DaySchedule? schedule, DateTimeOffset startTime)> InvokeWaitDeterministic(
        SonosControlService svc,
        IUnitOfWork uow,
        ManualTimeProvider timeProvider,
        TimeSpan maxVirtualAdvance,
        TimeSpan step,
        TimeSpan realTimeout)
    {
        if (maxVirtualAdvance <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxVirtualAdvance));
        }

        if (step <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(step));
        }

        using var cts = new CancellationTokenSource(realTimeout);
        var waitTask = InvokeWait(svc, uow, cts.Token);
        var advanced = TimeSpan.Zero;
        var firstScheduleWait = Stopwatch.StartNew();

        // Ensure WaitUntilStartTime has actually reached its first delay point
        // before we begin advancing virtual time.
        while (!waitTask.IsCompleted && timeProvider.NextScheduledTime is null && firstScheduleWait.Elapsed < realTimeout)
        {
            await Task.Yield();
        }

        var noScheduleWait = Stopwatch.StartNew();
        while (!waitTask.IsCompleted && advanced < maxVirtualAdvance)
        {
            var nextScheduledTime = timeProvider.NextScheduledTime;
            if (nextScheduledTime is null)
            {
                if (noScheduleWait.Elapsed >= realTimeout)
                {
                    break;
                }

                await Task.Yield();
                continue;
            }

            noScheduleWait.Restart();

            var deltaToNext = nextScheduledTime.Value - timeProvider.LocalNow;
            if (deltaToNext < TimeSpan.Zero)
            {
                deltaToNext = TimeSpan.Zero;
            }

            var remainingBudget = maxVirtualAdvance - advanced;
            var delta = deltaToNext < remainingBudget ? deltaToNext : remainingBudget;
            if (step > TimeSpan.Zero && step < delta)
            {
                delta = step;
            }

            timeProvider.Advance(delta);
            advanced += delta;
            await Task.Yield();
        }

        if (waitTask.IsCompletedSuccessfully)
        {
            return await waitTask;
        }

        if (waitTask.IsCanceled)
        {
            throw new XunitException(
                $"WaitUntilStartTime was canceled before completion. " +
                $"Advanced {advanced} of {maxVirtualAdvance}.");
        }

        if (waitTask.IsFaulted)
        {
            return await waitTask;
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);
        throw new XunitException(
            $"WaitUntilStartTime did not complete within virtual-time budget. " +
            $"Advanced {advanced} of {maxVirtualAdvance}.");
    }

    private IServiceScopeFactory CreateMockScopeFactory(IUnitOfWork uow)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(IUnitOfWork))).Returns(uow);
        serviceProvider.Setup(x => x.GetService(typeof(INotificationService))).Returns(Mock.Of<INotificationService>());

        var serviceScope = new Mock<IServiceScope>();
        serviceScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(x => x.CreateScope()).Returns(serviceScope.Object);

        return scopeFactory.Object;
    }

    [Fact]
    public async Task WaitUntilStartTime_WaitsUntilSettingStart()
    {
        var initial = LocalInstant(2024, 1, 1, 8, 0, 0);
        var timeProvider = new ManualTimeProvider(initial);

        var start = new TimeOnly(8, 0, 0, 500);
        var settings = new SonosSettings
        {
            StartTime = start,
            ActiveDays = Enum.GetValues<DayOfWeek>().ToList()
        };
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var svc = new SonosControlService(scopeFactory, timeProvider, timeProvider.DelayAsync);
        var expectedStart = new DateTimeOffset(initial.Date.Add(start.ToTimeSpan()), initial.Offset);
        var result = await InvokeWaitDeterministic(
            svc,
            uow.Object,
            timeProvider,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(3));

        Assert.Same(settings, result.settings);
        Assert.Null(result.schedule);
        Assert.Equal(expectedStart, timeProvider.LocalNow);
    }

    [Fact]
    public async Task WaitUntilStartTime_UsesDailySchedule()
    {
        var schedule = new DaySchedule
        {
            StartTime = TimeOnly.FromDateTime(DateTime.Now.AddMilliseconds(200))
        };
        var settings = new SonosSettings
        {
            StartTime = TimeOnly.FromDateTime(DateTime.Now.AddHours(1)),
            DailySchedules = new Dictionary<DayOfWeek, DaySchedule>
            {
                [DateTime.Now.DayOfWeek] = schedule
            },
            ActiveDays = Enum.GetValues<DayOfWeek>().ToList()
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var svc = new SonosControlService(scopeFactory);

        var sw = Stopwatch.StartNew();
        var result = await InvokeWait(svc, uow.Object, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 150, $"Elapsed {sw.ElapsedMilliseconds}ms");
        Assert.Same(schedule, result.schedule);
    }

    [Fact]
    public async Task WaitUntilStartTime_WhenStartTimeAlreadyPassed_WaitsForNextDay_DefaultSettings()
    {
        var startTime = new TimeOnly(7, 30);
        var initial = LocalInstant(2024, 1, 1, 8, 0, 0);
        var timeProvider = new ManualTimeProvider(initial);

        var settings = new SonosSettings
        {
            StartTime = startTime,
            ActiveDays = Enum.GetValues<DayOfWeek>().ToList()
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var svc = new SonosControlService(scopeFactory, timeProvider, timeProvider.DelayAsync);
        var now = timeProvider.LocalNow;
        var nextRun = new DateTimeOffset(now.Date.AddDays(1).Add(startTime.ToTimeSpan()), now.Offset);
        var maxVirtualAdvance = (nextRun - now) + TimeSpan.FromMinutes(1);
        var result = await InvokeWaitDeterministic(
            svc,
            uow.Object,
            timeProvider,
            maxVirtualAdvance,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(3));

        Assert.Same(settings, result.settings);
        Assert.Null(result.schedule);
        Assert.Equal(nextRun, timeProvider.LocalNow);
        var expectedDay = (DayOfWeek)(((int)initial.DayOfWeek + 1) % 7);
        Assert.Equal(expectedDay, timeProvider.LocalNow.DayOfWeek);
    }

    [Fact]
    public async Task WaitUntilStartTime_WhenTodayScheduleHasPassed_UsesNextDaySchedule()
    {
        var initial = LocalInstant(2024, 1, 1, 23, 55, 0);
        var timeProvider = new ManualTimeProvider(initial);

        var today = initial.DayOfWeek;
        var tomorrow = (DayOfWeek)(((int)today + 1) % 7);

        var todaySchedule = new DaySchedule
        {
            StartTime = new TimeOnly(23, 50)
        };

        var tomorrowSchedule = new DaySchedule
        {
            StartTime = new TimeOnly(0, 1),
            SpotifyUrl = "spotify:track:example"
        };

        var settings = new SonosSettings
        {
            StartTime = new TimeOnly(6, 0),
            DailySchedules = new Dictionary<DayOfWeek, DaySchedule>
            {
                [today] = todaySchedule,
                [tomorrow] = tomorrowSchedule
            },
            ActiveDays = Enum.GetValues<DayOfWeek>().ToList()
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var svc = new SonosControlService(scopeFactory, timeProvider, timeProvider.DelayAsync);

        var now = timeProvider.LocalNow;
        var nextRun = new DateTimeOffset(now.Date.AddDays(1).Add(tomorrowSchedule.StartTime.ToTimeSpan()), now.Offset);
        var maxVirtualAdvance = (nextRun - now) + TimeSpan.FromMinutes(1);
        var result = await InvokeWaitDeterministic(
            svc,
            uow.Object,
            timeProvider,
            maxVirtualAdvance,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3));

        Assert.Same(settings, result.settings);
        Assert.Same(tomorrowSchedule, result.schedule);
        Assert.Equal(nextRun, timeProvider.LocalNow);
        Assert.Equal(tomorrow, timeProvider.LocalNow.DayOfWeek);
    }

    [Fact]
    public async Task WaitUntilStartTime_WhenTodayScheduleHasPassed_CompletesWithinBudget()
    {
        var initial = LocalInstant(2024, 1, 1, 23, 55, 0);
        var timeProvider = new ManualTimeProvider(initial);

        var today = initial.DayOfWeek;
        var tomorrow = (DayOfWeek)(((int)today + 1) % 7);

        var settings = new SonosSettings
        {
            StartTime = new TimeOnly(6, 0),
            DailySchedules = new Dictionary<DayOfWeek, DaySchedule>
            {
                [today] = new DaySchedule { StartTime = new TimeOnly(23, 50) },
                [tomorrow] = new DaySchedule { StartTime = new TimeOnly(0, 1), SpotifyUrl = "spotify:track:example" }
            },
            ActiveDays = Enum.GetValues<DayOfWeek>().ToList()
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var svc = new SonosControlService(scopeFactory, timeProvider, timeProvider.DelayAsync);

        var expectedStart = new DateTimeOffset(initial.Date.AddDays(1).AddHours(0).AddMinutes(1), initial.Offset);
        var maxVirtualAdvance = TimeSpan.FromMinutes(8);
        var result = await InvokeWaitDeterministic(
            svc,
            uow.Object,
            timeProvider,
            maxVirtualAdvance,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3));

        Assert.Equal(expectedStart, result.startTime);
        Assert.Equal(expectedStart, timeProvider.LocalNow);
    }

    [Fact]
    public async Task WaitUntilStartTime_UsesHolidayScheduleForToday()
    {
        var initial = LocalInstant(2024, 12, 25, 5, 0, 0);
        var timeProvider = new ManualTimeProvider(initial);

        var holidaySchedule = new HolidaySchedule
        {
            Date = DateOnly.FromDateTime(initial.Date),
            StartTime = new TimeOnly(5, 5),
            SpotifyUrl = "spotify:track:winter"
        };

        var settings = new SonosSettings
        {
            StartTime = new TimeOnly(7, 0),
            HolidaySchedules = new List<HolidaySchedule> { holidaySchedule },
            ActiveDays = Enum.GetValues<DayOfWeek>().ToList()
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var svc = new SonosControlService(scopeFactory, timeProvider, timeProvider.DelayAsync);
        var expectedStart = new DateTimeOffset(initial.Date.Add(holidaySchedule.StartTime.ToTimeSpan()), initial.Offset);
        var result = await InvokeWaitDeterministic(
            svc,
            uow.Object,
            timeProvider,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(3));

        Assert.Same(holidaySchedule, result.schedule);
        Assert.Equal(expectedStart, timeProvider.LocalNow);
    }

    [Fact]
    public async Task WaitUntilStartTime_WhenHolidayIsNextDay_SelectsHolidayStart()
    {
        var initial = LocalInstant(2024, 12, 24, 23, 30, 0);
        var timeProvider = new ManualTimeProvider(initial);

        var holidayDate = DateOnly.FromDateTime(initial.AddDays(1).Date);
        var holidaySchedule = new HolidaySchedule
        {
            Date = holidayDate,
            StartTime = new TimeOnly(6, 15),
            SpotifyUrl = "spotify:track:holiday-next-day"
        };

        var settings = new SonosSettings
        {
            StartTime = new TimeOnly(7, 0),
            HolidaySchedules = new List<HolidaySchedule> { holidaySchedule },
            ActiveDays = Enum.GetValues<DayOfWeek>().ToList()
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var svc = new SonosControlService(scopeFactory, timeProvider, timeProvider.DelayAsync);
        var expectedStart = new DateTimeOffset(initial.Date.AddDays(1).Add(holidaySchedule.StartTime.ToTimeSpan()), initial.Offset);
        var result = await InvokeWaitDeterministic(
            svc,
            uow.Object,
            timeProvider,
            TimeSpan.FromHours(8),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(3));

        Assert.Same(holidaySchedule, result.schedule);
        Assert.Equal(expectedStart, timeProvider.LocalNow);
    }

    [Fact]
    public async Task WaitUntilStartTime_SkipsHolidaySchedulesMarkedDontPlay()
    {
        var initial = LocalInstant(2024, 6, 1, 5, 0, 0);
        var timeProvider = new ManualTimeProvider(initial);

        var today = initial.DayOfWeek;
        var tomorrow = (DayOfWeek)(((int)today + 1) % 7);

        var skipHoliday = new HolidaySchedule
        {
            Date = DateOnly.FromDateTime(initial.Date),
            StartTime = new TimeOnly(5, 30),
            StopTime = new TimeOnly(6, 30),
            SkipPlayback = true
        };

        var tomorrowSchedule = new DaySchedule
        {
            StartTime = new TimeOnly(6, 0),
            StationUrl = "station:morning"
        };

        var settings = new SonosSettings
        {
            StartTime = new TimeOnly(7, 0),
            DailySchedules = new Dictionary<DayOfWeek, DaySchedule>
            {
                [tomorrow] = tomorrowSchedule
            },
            HolidaySchedules = new List<HolidaySchedule> { skipHoliday },
            ActiveDays = Enum.GetValues<DayOfWeek>().ToList()
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var svc = new SonosControlService(scopeFactory, timeProvider, timeProvider.DelayAsync);
        var expectedStart = new DateTimeOffset(initial.Date.AddDays(1).Add(tomorrowSchedule.StartTime.ToTimeSpan()), initial.Offset);
        var result = await InvokeWaitDeterministic(
            svc,
            uow.Object,
            timeProvider,
            TimeSpan.FromHours(26),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(3));

        Assert.Same(tomorrowSchedule, result.schedule);
        Assert.Equal(expectedStart, timeProvider.LocalNow);
    }

    [Fact]
    public async Task StartSpeaker_DoesNotTriggerPlaybackWhenHolidayScheduleIsSkip()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(new SonosSettings());

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var svc = new SonosControlService(scopeFactory);
        var method = typeof(SonosControlService).GetMethod("StartSpeaker", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var schedule = new HolidaySchedule
        {
            SkipPlayback = true,
            StartTime = new TimeOnly(6, 0),
            StopTime = new TimeOnly(8, 0)
        };

        var settings = new SonosSettings
        {
            ActiveDays = Enum.GetValues<DayOfWeek>().ToList()
        };

        var task = (Task)method.Invoke(svc, new object[] { uow.Object, new List<SonosSpeaker>(), settings, schedule, CancellationToken.None })!;
        await task;

        sonosRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WaitUntilStartTime_SkipsInactiveDays()
    {
        var initial = LocalInstant(2024, 1, 1, 8, 0, 0); // Monday
        var timeProvider = new ManualTimeProvider(initial);

        // Monday is inactive
        var activeDays = new List<DayOfWeek> { DayOfWeek.Tuesday };
        var settings = new SonosSettings
        {
            StartTime = new TimeOnly(9, 0),
            ActiveDays = activeDays,
            DailySchedules = new Dictionary<DayOfWeek, DaySchedule>
            {
                [DayOfWeek.Monday] = new DaySchedule { StartTime = new TimeOnly(9, 0) },
                [DayOfWeek.Tuesday] = new DaySchedule { StartTime = new TimeOnly(9, 0) }
            }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var svc = new SonosControlService(scopeFactory, timeProvider, timeProvider.DelayAsync);
        var expectedStart = new DateTimeOffset(initial.Date.AddDays(1).AddHours(9), initial.Offset);
        var result = await InvokeWaitDeterministic(
            svc,
            uow.Object,
            timeProvider,
            TimeSpan.FromHours(26),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(3));

        Assert.Same(settings.DailySchedules[DayOfWeek.Tuesday], result.schedule);
        Assert.Equal(expectedStart, timeProvider.LocalNow);
    }


    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _current;
        private readonly SortedList<DateTimeOffset, List<TaskCompletionSource<bool>>> _scheduled = new();
        private readonly object _lock = new();

        public ManualTimeProvider(DateTimeOffset current)
        {
            _current = current;
        }

        public DateTimeOffset LocalNow
        {
            get
            {
                lock (_lock)
                {
                    return _current;
                }
            }
        }

        public int ScheduledCount
        {
            get
            {
                lock (_lock)
                {
                    return _scheduled.Count;
                }
            }
        }

        public DateTimeOffset? NextScheduledTime
        {
            get
            {
                lock (_lock)
                {
                    if (_scheduled.Count == 0)
                    {
                        return null;
                    }

                    return _scheduled.Keys[0];
                }
            }
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _current;
            }
        }

        public void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delta));

            lock (_lock)
            {
                _current = _current.Add(delta);
                CompleteDueTimers();
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            if (delay <= TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            DateTimeOffset target;

            lock (_lock)
            {
                target = _current.Add(delay);
                if (!_scheduled.TryGetValue(target, out var waiters))
                {
                    waiters = new List<TaskCompletionSource<bool>>();
                    _scheduled.Add(target, waiters);
                }
                waiters.Add(tcs);
                CompleteDueTimers();
            }

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    lock (_lock)
                    {
                        if (_scheduled.TryGetValue(target, out var list))
                        {
                            list.Remove(tcs);
                            if (list.Count == 0)
                                _scheduled.Remove(target);
                        }
                    }

                    tcs.TrySetCanceled(cancellationToken);
                });
            }

            return tcs.Task;
        }

        private void CompleteDueTimers()
        {
            while (_scheduled.Count > 0)
            {
                var key = _scheduled.Keys[0];
                if (key > _current)
                    break;

                var waiters = _scheduled.Values[0];
                _scheduled.RemoveAt(0);

                foreach (var waiter in waiters)
                {
                    waiter.TrySetResult(true);
                }
            }
        }
    }
}
