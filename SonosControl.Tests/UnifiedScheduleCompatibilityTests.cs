using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using SonosControl.Web.Controllers;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class UnifiedScheduleCompatibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CanonicalTemplateCanBeUpdatedAppliedAndDeletedThroughLegacyApi()
    {
        using var repository = new SettingsRepo(_directory);
        var editor = new SettingsEditingService(repository);
        var template = new ScheduleWindow
        {
            Id = "template",
            Name = "Kitchen",
            HasTimeSchedule = false,
            Playback = new Scene { Id = "payload", Name = "Kitchen", SpeakerIps = ["A"] }
        };
        await editor.SaveWindowAsync(template, true);
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var orchestration = new Mock<ISceneOrchestrationService>();
        orchestration.Setup(service => service.ApplySceneAsync(It.Is<Scene>(scene => scene.Description == "Updated"),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "payload", ["A"]));
        var api = new ScenesController(repository, editor, orchestration.Object, new ActionLogger(db, new HttpContextAccessor()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var read = Assert.IsType<OkObjectResult>((await api.GetById("payload")).Result);
        var scene = Assert.IsType<Scene>(read.Value);
        scene.Description = "Updated";
        Assert.IsType<OkObjectResult>((await api.Update("payload", scene)).Result);
        Assert.Equal("Updated", (await repository.GetSettings())!.ScheduleWindows.Single().Playback!.Description);
        Assert.IsType<OkObjectResult>((await api.Apply("payload", default)).Result);
        orchestration.Verify(service => service.ApplySceneAsync(It.IsAny<Scene>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsType<NoContentResult>(await api.Delete("payload"));
        Assert.Empty((await repository.GetSettings())!.ScheduleWindows);
    }

    [Fact]
    public async Task LegacySceneReferenceIsCopiedAndSubsequentEditsAreIndependent()
    {
        using var repository = new SettingsRepo(_directory);
        var editor = new SettingsEditingService(repository);
        await editor.SaveSceneAsync(new Scene { Id = "source", Name = "Original", SpeakerIps = ["A"] }, true);
        await editor.SaveWindowAsync(new ScheduleWindow { Id = "one", SceneId = "source" }, true);
        await editor.SaveWindowAsync(new ScheduleWindow { Id = "two", SceneId = "source" }, true);
        var settings = (await repository.GetSettings())!;
        var first = settings.ScheduleWindows.Single(window => window.Id == "one");
        first.Playback!.SpeakerIps = ["B"];
        await editor.SaveWindowAsync(first, false);
        settings = (await repository.GetSettings())!;
        Assert.Equal(["A"], settings.ScheduleWindows.Single(window => window.Id == "two").Playback!.SpeakerIps);
        Assert.Equal(["A"], settings.Scenes.Single().SpeakerIps);
        Assert.NotEqual(first.Playback.Id, settings.ScheduleWindows.Single(window => window.Id == "two").Playback!.Id);
        await editor.DeleteSceneAsync("source");
        Assert.All((await repository.GetSettings())!.ScheduleWindows, window => Assert.True(window.IsEnabled));
    }

    [Fact]
    public async Task EditingAndDeletingMigratedTemplateDoesNotExposeStaleLegacyCopy()
    {
        using var repository = new SettingsRepo(_directory);
        var editor = new SettingsEditingService(repository);
        await editor.SaveSceneAsync(new Scene { Id = "source", Name = "Original", SpeakerIps = ["A"] }, true);
        var template = (await repository.GetSettings())!.ScheduleWindows.Single();
        template.Playback!.Description = "Current";
        await editor.SaveWindowAsync(template, false);
        Assert.Equal("Current", (await repository.GetSettings())!.Scenes.Single().Description);
        await editor.DeleteWindowAsync(template.Id);
        Assert.Empty((await repository.GetSettings())!.Scenes);
    }

    [Fact]
    public async Task ActiveRoomsEndpointReportsIndependentWinnersAndOmitsTemplates()
    {
        using var repository = new SettingsRepo(_directory);
        var editor = new SettingsEditingService(repository);
        await repository.UpdateSettingsAsync(settings => settings.ScheduleWindows =
        [
            new ScheduleWindow { Id = "a", Priority = 20, StartTime = TimeOnly.MinValue, StopTime = TimeOnly.MinValue,
                RecurrenceType = ScheduleRecurrenceType.Daily, Playback = new Scene { SpeakerIps = ["A"] } },
            new ScheduleWindow { Id = "b", Priority = 10, StartTime = TimeOnly.MinValue, StopTime = TimeOnly.MinValue,
                RecurrenceType = ScheduleRecurrenceType.Daily, Playback = new Scene { SpeakerIps = ["B"] } },
            new ScheduleWindow { Id = "template", Priority = 0, HasTimeSchedule = false,
                Playback = new Scene { SpeakerIps = ["A", "B"] } }
        ]);
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var api = new SchedulesController(repository, editor, new ActionLogger(db, new HttpContextAccessor()),
            new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        var response = Assert.IsType<OkObjectResult>((await api.GetActiveRooms()).Result);
        var rooms = Assert.IsAssignableFrom<IReadOnlyDictionary<string, ScheduleWindow>>(response.Value);
        Assert.Equal("a", rooms["A"].Id);
        Assert.Equal("b", rooms["B"].Id);
        Assert.Equal(2, rooms.Count);
    }

    [Fact]
    public async Task InvalidEnabledAutoStopDoesNotChangePersistedSettings()
    {
        using var repository = new SettingsRepo(_directory);
        await repository.UpdateSettingsAsync(settings => settings.Speakers = [new SonosSpeaker { IpAddress = "A" }]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateSettingsAsync(settings =>
        {
            settings.Speakers[0].ManualAutoStopDays[DayOfWeek.Monday] = new ManualAutoStopDay { Enabled = true };
            settings.Speakers[0].ManualAutoStopMinutes = 0;
        }));
        Assert.Empty((await repository.GetSettings())!.Speakers[0].ManualAutoStopDays);
    }

    [Theory]
    [InlineData(nameof(ScenesController.Create))]
    [InlineData(nameof(ScenesController.Update))]
    [InlineData(nameof(ScenesController.Delete))]
    public void SceneCompatibilityWritesRequireAdminOrSuperadmin(string action)
    {
        var method = typeof(ScenesController).GetMethod(action)!;
        var roles = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single().Roles;
        Assert.Equal("admin,superadmin", roles);
    }

    [Theory]
    [InlineData("bad|id")]
    [InlineData("with space")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task CreateRejectsUnsafeIdsWithBadRequest(string id)
    {
        using var repository = new SettingsRepo(_directory);
        var editor = new SettingsEditingService(repository);
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var logger = new ActionLogger(db, new HttpContextAccessor());
        var schedules = new SchedulesController(repository, editor, logger, new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        var scenes = new ScenesController(repository, editor, Mock.Of<ISceneOrchestrationService>(), logger);

        Assert.IsType<BadRequestObjectResult>((await schedules.Create(new ScheduleWindow { Id = id, Name = "W" })).Result);
        Assert.IsType<BadRequestObjectResult>((await scenes.Create(new Scene { Id = id, Name = "S" })).Result);
        Assert.Empty((await repository.GetSettings())!.ScheduleWindows);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
