using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.Web.Models;
using SonosControl.Web.Shared;
using Xunit;

namespace SonosControl.Tests;

public class AccessibilityPrimitivesTests
{
    [Fact]
    public void WorkspaceTabs_ExposeTabSemanticsAndRovingTabIndex()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());
        var selected = string.Empty;
        IReadOnlyList<WorkspaceTabItem> items =
        [
            new("now", "Now playing"),
            new("rooms", "Rooms"),
            new("library", "Library")
        ];

        var cut = ctx.RenderComponent<WorkspaceTabs>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.ActiveKey, "now")
            .Add(component => component.IdPrefix, "quality")
            .Add(component => component.OnSelect, EventCallback.Factory.Create<string>(
                this,
                value => selected = value)));

        Assert.Equal("tablist", cut.Find("nav").GetAttribute("role"));
        Assert.Equal(3, cut.FindAll("[role='tab']").Count);
        var nowTab = cut.Find("#quality-tab-now");
        var roomsTab = cut.Find("#quality-tab-rooms");
        Assert.Equal("true", nowTab.GetAttribute("aria-selected"));
        Assert.Equal("0", nowTab.GetAttribute("tabindex"));
        Assert.Equal("-1", roomsTab.GetAttribute("tabindex"));
        Assert.Equal("quality-panel-now", nowTab.GetAttribute("aria-controls"));

        nowTab.KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });

        Assert.Equal("rooms", selected);
        Assert.Contains(
            ctx.JSInterop.Invocations,
            invocation => invocation.Identifier == "window.sonosUi.focusById");
    }

    [Fact]
    public void AccessibleDialog_HasNameFocusTrapAndEscapeClose()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());
        var closed = false;

        var cut = ctx.RenderComponent<AccessibleDialog>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.Title, "Edit source")
            .Add(component => component.CloseLabel, "Close source editor")
            .Add(component => component.OnClose, EventCallback.Factory.Create(
                this,
                () => closed = true)));

        var dialog = cut.Find("[role='dialog']");
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        var titleId = dialog.GetAttribute("aria-labelledby");
        Assert.Equal("Edit source", cut.Find($"#{titleId}").TextContent);
        Assert.Equal(
            "Close source editor",
            cut.Find("button.app-dialog__close").GetAttribute("aria-label"));
        Assert.Contains(
            ctx.JSInterop.Invocations,
            invocation => invocation.Identifier == "window.sonosUi.activateFocusTrap");

        dialog.KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.True(closed);
        Assert.Contains(
            ctx.JSInterop.Invocations,
            invocation => invocation.Identifier == "window.sonosUi.releaseFocusTrap");
    }

    [Fact]
    public void EmptyState_DefaultsToH2AndHidesDecorativeIcon()
    {
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());

        var cut = ctx.RenderComponent<EmptyState>(parameters => parameters
            .Add(component => component.Title, "Nothing queued")
            .Add(component => component.IconName, "queue"));

        Assert.Equal("Nothing queued", cut.Find("h2").TextContent);
        Assert.Equal("true", cut.Find(".empty-state__icon").GetAttribute("aria-hidden"));
        Assert.Null(cut.Find(".empty-state__icon svg").GetAttribute("aria-label"));
    }

    [Theory]
    [InlineData(null, ThemePreferenceMode.System)]
    [InlineData("", ThemePreferenceMode.System)]
    [InlineData("light", ThemePreferenceMode.Light)]
    [InlineData("dark", ThemePreferenceMode.Dark)]
    [InlineData("unexpected", ThemePreferenceMode.System)]
    public void ThemePreference_PreservesLegacyValuesAndFallsBackToSystem(
        string? storedValue,
        ThemePreferenceMode expected)
    {
        Assert.Equal(expected, ThemePreferenceModeExtensions.FromIdentifier(storedValue));
    }
}
