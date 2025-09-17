using System.Diagnostics;
using System.Reflection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SonosControl.Tests;

public class SonosControlServiceTests
{
    private static Task<(SonosSettings settings, DaySchedule? schedule)> InvokeWait(SonosControlService svc, CancellationToken token)
    {
        var method = typeof(SonosControlService).GetMethod("WaitUntilStartTime", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<(SonosSettings, DaySchedule?)>)method.Invoke(svc, new object[] { token })!;
        return task;
    }

    [Fact]
    public async Task WaitUntilStartTime_WaitsUntilSettingStart()
    {
        var initial = new DateTimeOffset(2024, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(initial);

        var start = TimeOnly.FromDateTime(initial.AddMilliseconds(500).DateTime);
        var settings = new SonosSettings
        {
            StartTime = start
        };
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var svc = new SonosControlService(uow.Object, timeProvider, timeProvider.DelayAsync);

        var waitTask = InvokeWait(svc, CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromMilliseconds(300));
        Assert.False(waitTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(200));

        var result = await waitTask;

        Assert.Same(settings, result.settings);
        Assert.Null(result.schedule);
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
            }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var svc = new SonosControlService(uow.Object);

        var sw = Stopwatch.StartNew();
        var result = await InvokeWait(svc, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 150, $"Elapsed {sw.ElapsedMilliseconds}ms");
        Assert.Same(schedule, result.schedule);
    }

    [Fact]
    public async Task WaitUntilStartTime_WhenStartTimeAlreadyPassed_WaitsForNextDay_DefaultSettings()
    {
        var startTime = new TimeOnly(7, 30);
        var initial = new DateTimeOffset(2024, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(initial);

        var settings = new SonosSettings
        {
            StartTime = startTime
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var svc = new SonosControlService(uow.Object, timeProvider, timeProvider.DelayAsync);

        var waitTask = InvokeWait(svc, CancellationToken.None);

        await Task.Delay(50);
        Assert.False(waitTask.IsCompleted);

        var now = timeProvider.LocalNow;
        var nextRun = new DateTimeOffset(now.Date.AddDays(1).Add(startTime.ToTimeSpan()), now.Offset);
        var advanceAlmostToStart = nextRun - now - TimeSpan.FromSeconds(1);
        Assert.True(advanceAlmostToStart > TimeSpan.Zero);

        timeProvider.Advance(advanceAlmostToStart);
        Assert.False(waitTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        var result = await waitTask;

        Assert.Same(settings, result.settings);
        Assert.Null(result.schedule);
        Assert.Equal(nextRun, timeProvider.LocalNow);
        var expectedDay = (DayOfWeek)(((int)initial.DayOfWeek + 1) % 7);
        Assert.Equal(expectedDay, timeProvider.LocalNow.DayOfWeek);
    }

    [Fact]
    public async Task WaitUntilStartTime_WhenTodayScheduleHasPassed_UsesNextDaySchedule()
    {
        var initial = new DateTimeOffset(2024, 1, 1, 23, 55, 0, TimeSpan.Zero);
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
            }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var svc = new SonosControlService(uow.Object, timeProvider, timeProvider.DelayAsync);

        var waitTask = InvokeWait(svc, CancellationToken.None);

        await Task.Delay(50);
        Assert.False(waitTask.IsCompleted);

        var now = timeProvider.LocalNow;
        var nextRun = new DateTimeOffset(now.Date.AddDays(1).Add(tomorrowSchedule.StartTime.ToTimeSpan()), now.Offset);
        var advanceAlmostToStart = nextRun - now - TimeSpan.FromSeconds(1);
        Assert.True(advanceAlmostToStart > TimeSpan.Zero);

        timeProvider.Advance(advanceAlmostToStart);
        Assert.False(waitTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        var result = await waitTask;

        Assert.Same(settings, result.settings);
        Assert.Same(tomorrowSchedule, result.schedule);
        Assert.Equal(nextRun, timeProvider.LocalNow);
        Assert.Equal(tomorrow, timeProvider.LocalNow.DayOfWeek);
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

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _current.ToUniversalTime();
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
