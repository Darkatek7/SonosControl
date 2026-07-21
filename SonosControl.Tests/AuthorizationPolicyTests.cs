using Microsoft.AspNetCore.Authorization;
using SonosControl.Web.Controllers;
using SonosControl.Web.Pages;
using Xunit;

namespace SonosControl.Tests;

public class AuthorizationPolicyTests
{
    private static readonly string[] OperatorAdminSuperadmin = ["admin", "operator", "superadmin"];
    private static readonly string[] AdminSuperadmin = ["admin", "superadmin"];

    public static IEnumerable<object[]> RouteRoleCases()
    {
        yield return [typeof(IndexPage), OperatorAdminSuperadmin];
        yield return [typeof(LibraryPage), OperatorAdminSuperadmin];
        yield return [typeof(AutomationPage), OperatorAdminSuperadmin];
        yield return [typeof(InsightsPage), OperatorAdminSuperadmin];
        yield return [typeof(UserEdit), OperatorAdminSuperadmin];
        yield return [typeof(AdministrationPage), AdminSuperadmin];
        yield return [typeof(DevicesPage), AdminSuperadmin];
        yield return [typeof(ConfigPage), AdminSuperadmin];
        yield return [typeof(UserManagement), AdminSuperadmin];
        yield return [typeof(SettingsBackupsPage), AdminSuperadmin];
    }

    public static IEnumerable<object[]> ControllerActionRoleCases()
    {
        yield return [typeof(SchedulesController), nameof(SchedulesController.GetAll), OperatorAdminSuperadmin];
        yield return [typeof(SchedulesController), nameof(SchedulesController.GetActive), OperatorAdminSuperadmin];
        yield return [typeof(SchedulesController), nameof(SchedulesController.Create), AdminSuperadmin];
        yield return [typeof(SchedulesController), nameof(SchedulesController.Update), AdminSuperadmin];
        yield return [typeof(SchedulesController), nameof(SchedulesController.Delete), AdminSuperadmin];
        yield return [typeof(ScenesController), nameof(ScenesController.Create), OperatorAdminSuperadmin];
        yield return [typeof(ScenesController), nameof(ScenesController.Update), OperatorAdminSuperadmin];
        yield return [typeof(ScenesController), nameof(ScenesController.Delete), OperatorAdminSuperadmin];
        yield return [typeof(ScenesController), nameof(ScenesController.Apply), OperatorAdminSuperadmin];
        yield return [typeof(QueueController), nameof(QueueController.RemoveQueueItem), OperatorAdminSuperadmin];
        yield return [typeof(DevicesController), nameof(DevicesController.Health), AdminSuperadmin];
        yield return [typeof(SettingsBackupsController), nameof(SettingsBackupsController.Restore), AdminSuperadmin];
    }

    [Theory]
    [MemberData(nameof(RouteRoleCases))]
    public void DirectRouteAccess_UsesExpectedRoles(Type componentType, string[] expectedRoles)
    {
        var roles = componentType
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SelectMany(attribute => SplitRoles(attribute.Roles))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(role => role)
            .ToArray();

        Assert.Equal(expectedRoles.OrderBy(role => role), roles);
    }

    [Theory]
    [MemberData(nameof(ControllerActionRoleCases))]
    public void DirectControllerActionAccess_UsesExpectedEffectiveRoles(
        Type controllerType,
        string methodName,
        string[] expectedRoles)
    {
        var method = controllerType.GetMethods().Single(candidate => candidate.Name == methodName);
        var roleSets = controllerType
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Concat(method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>())
            .Select(attribute => SplitRoles(attribute.Roles).ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Where(roles => roles.Count > 0)
            .ToList();

        Assert.NotEmpty(roleSets);
        var effectiveRoles = roleSets
            .Skip(1)
            .Aggregate(
                new HashSet<string>(roleSets[0], StringComparer.OrdinalIgnoreCase),
                (allowed, required) =>
                {
                    allowed.IntersectWith(required);
                    return allowed;
                })
            .OrderBy(role => role)
            .ToArray();

        Assert.Equal(expectedRoles.OrderBy(role => role), effectiveRoles);
    }

    private static IEnumerable<string> SplitRoles(string? roles)
        => string.IsNullOrWhiteSpace(roles)
            ? Array.Empty<string>()
            : roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
