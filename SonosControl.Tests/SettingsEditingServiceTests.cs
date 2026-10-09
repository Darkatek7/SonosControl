using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using SonosControl.Web.Controllers;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SettingsEditingServiceTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1001, 0)]
    [InlineData(100, -1)]
    [InlineData(100, 601)]
    public async Task UiServiceAndApi_RejectTheSameInvalidWindow(int priority, int fade)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var repository = new SettingsRepo(directory);
            var editor = new SettingsEditingService(repository);
            var draft = new ScheduleWindow { Priority = priority, FadeInSeconds = fade, IsEnabled = false };
            var error = await Assert.ThrowsAsync<SettingsEditException>(() => editor.SaveWindowAsync(draft, true));
            using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var api = new SchedulesController(repository, editor, new ActionLogger(db, new HttpContextAccessor()), new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
            var response = Assert.IsType<ObjectResult>((await api.Create(draft)).Result);
            Assert.Equal(error.StatusCode, response.StatusCode);
            Assert.Equal(error.Message, response.Value);
            Assert.Empty((await repository.GetSettings())!.ScheduleWindows);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("name")]
    [InlineData("duplicate")]
    [InlineData("source")]
    [InlineData("timer")]
    [InlineData("volume")]
    public async Task SceneUiServiceAndApiUseTheCurrentValidationRules(string invalidField)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var repository = new SettingsRepo(directory);
            var editor = new SettingsEditingService(repository);
            await editor.SaveSceneAsync(new Scene { Name = "Existing" }, true);
            await repository.UpdateSettingsAsync(settings => settings.MaxVolume = 40);
            var draft = new Scene { Name = "Candidate", SourceType = SceneSourceType.Station, SourceUrl = "example.com/stream" };
            switch (invalidField)
            {
                case "name": draft.Name = " "; break;
                case "duplicate": draft.Name = " existing "; break;
                case "source": draft.SourceUrl = null; break;
                case "timer": draft.TimerMinutes = -1; break;
                case "volume": draft.Actions.Add(new SceneAction { SpeakerIp = "A", Volume = 41 }); break;
            }
            var error = await Assert.ThrowsAsync<SettingsEditException>(() => editor.SaveSceneAsync(draft, true));
            using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var api = new ScenesController(repository, editor, Mock.Of<ISceneOrchestrationService>(), new ActionLogger(db, new HttpContextAccessor()));
            var response = Assert.IsType<ObjectResult>((await api.Create(draft)).Result);
            Assert.Equal(error.StatusCode, response.StatusCode);
            Assert.Equal(error.Message, response.Value);
            Assert.Single((await repository.GetSettings())!.Scenes);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StaleEntityEditPreservesAnotherFieldAndDeletedEntityCannotReturn()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var repository = new SettingsRepo(directory);
            var editor = new SettingsEditingService(repository);
            var initial = await editor.SaveSceneAsync(new Scene { Id = "scene", Name = "Initial" }, true);
            var original = initial.Scenes[0];
            var draft = SettingsChanges.MergeEntity(original, original, original);
            draft.Description = "My edit";
            await repository.UpdateSettingsAsync(s => s.Scenes[0].Name = "Another name");
            var saved = await editor.SaveSceneAsync(draft, false, original);
            Assert.Equal("Another name", saved.Scenes[0].Name);
            Assert.Equal("My edit", saved.Scenes[0].Description);
            await editor.DeleteSceneAsync("scene");
            await Assert.ThrowsAsync<SettingsEditException>(() => editor.SaveSceneAsync(draft, false, original));
            Assert.Empty((await repository.GetSettings())!.Scenes);
        }
        finally { Directory.Delete(directory, true); }
    }
}
