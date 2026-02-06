using System;
using System.Collections.Generic;
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

namespace SonosControl.Tests;

public class SonosControlStopTests
{
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
    public async Task StopSpeaker_StopsAllSpeakers()
    {
        var initial = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(initial);

        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var speakers = new List<SonosSpeaker>
        {
            new SonosSpeaker { IpAddress = "1.1.1.1" },
            new SonosSpeaker { IpAddress = "2.2.2.2" }
        };

        var schedule = new DaySchedule { IsSyncedPlayback = false };
        var stopDateTime = initial.AddHours(1);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var svc = new SonosControlService(scopeFactory, timeProvider, (d, t) => Task.CompletedTask);
        var method = typeof(SonosControlService).GetMethod("StopSpeaker", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Act
        var task = (Task)method.Invoke(svc, new object[] { uow.Object, speakers, stopDateTime, schedule, CancellationToken.None })!;
        await task;

        // Assert
        sonosRepo.Verify(r => r.StopPlaying("1.1.1.1"), Times.Once);
        sonosRepo.Verify(r => r.StopPlaying("2.2.2.2"), Times.Once);
    }

    [Fact]
    public async Task StopSpeaker_CorrectlyDelaysUntilStopDateTime()
    {
        var initial = new DateTimeOffset(2024, 1, 1, 22, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(initial);

        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var speakers = new List<SonosSpeaker>
        {
            new SonosSpeaker { IpAddress = "1.1.1.1" }
        };

        var schedule = new DaySchedule { IsSyncedPlayback = true };
        var stopDateTime = initial.AddHours(4); // 02:00 AM next day

        var scopeFactory = CreateMockScopeFactory(uow.Object);

        TimeSpan capturedDelay = TimeSpan.Zero;
        var svc = new SonosControlService(scopeFactory, timeProvider, (d, t) => { capturedDelay = d; return Task.CompletedTask; });
        var method = typeof(SonosControlService).GetMethod("StopSpeaker", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Act
        var task = (Task)method.Invoke(svc, new object[] { uow.Object, speakers, stopDateTime, schedule, CancellationToken.None })!;
        await task;

        // Assert
        Assert.Equal(TimeSpan.FromHours(4), capturedDelay);
    }

    [Theory]
    [InlineData("22:00", "02:00", 4)]   // Cross-midnight
    [InlineData("08:00", "17:00", 9)]   // Same day
    [InlineData("08:00", "08:00", 0)]   // Zero duration
    [InlineData("02:00", "01:00", 23)]  // Almost 24h
    public void DurationCalculation_IsCorrect(string startStr, string stopStr, int expectedHours)
    {
        var startTimeOnly = TimeOnly.Parse(startStr);
        var stopTimeOnly = TimeOnly.Parse(stopStr);

        TimeSpan duration = stopTimeOnly - startTimeOnly;
        if (duration < TimeSpan.Zero)
        {
            duration = duration.Add(TimeSpan.FromDays(1));
        }

        Assert.Equal(TimeSpan.FromHours(expectedHours), duration);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _current;
        public ManualTimeProvider(DateTimeOffset current) => _current = current;
        public override DateTimeOffset GetUtcNow() => _current.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
