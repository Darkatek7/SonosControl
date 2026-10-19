using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using SonosControl.Web.Services.FlyDj;

namespace SonosControl.Web.Controllers;

/// <summary>Hides the whole FlyDJ API (404) unless the feature is enabled. Runs before the controller is created.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class FlyDjEnabledAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var options = context.HttpContext.RequestServices.GetService<IOptions<FlyDjOptions>>()?.Value;
        if (options is not { Enabled: true }) context.Result = new NotFoundResult();
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}

[ApiController]
[Route("api/flydj")]
[Authorize(Roles = FlyDjServiceCollectionExtensions.AllowedRoles)]
[FlyDjEnabled]
public sealed class FlyDjController(
    FlyDjStateStore state,
    IFlyDjConductor conductor,
    FlyDjGeometryCache geometry) : ControllerBase
{
    private const string GeometryCacheControl = "private, max-age=3600";

    [HttpGet("state")]
    public ActionResult<FlyDjDashboardState> GetState() => Ok(state.Current);

    [HttpPost("start")]
    public async Task<ActionResult<FlyDjDashboardState>> Start(CancellationToken cancellationToken)
    {
        await conductor.StartAsync(User.Identity?.Name, cancellationToken);
        return Ok(state.Current);
    }

    [HttpPost("stop")]
    public async Task<ActionResult<FlyDjDashboardState>> Stop(CancellationToken cancellationToken)
    {
        await conductor.StopAsync(User.Identity?.Name, cancellationToken);
        return Ok(state.Current);
    }

    /// <summary>Proxies the brain geometry with ETag revalidation so browsers download it once.</summary>
    [HttpGet("geometry")]
    public async Task<IActionResult> GetGeometry(CancellationToken cancellationToken)
    {
        (byte[] Content, string ETag) current;
        try { current = await geometry.GetAsync(cancellationToken); }
        catch (FlyDjException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        Response.Headers.ETag = current.ETag;
        Response.Headers.CacheControl = GeometryCacheControl;
        if (Request.Headers.IfNoneMatch.Any(value => IfNoneMatchContains(value, current.ETag)))
            return StatusCode(StatusCodes.Status304NotModified);
        return File(current.Content, "application/octet-stream");
    }

    private static bool IfNoneMatchContains(string? header, string etag)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        if (header.Trim() == "*") return true;
        return header.Split(',').Select(part => part.Trim()).Any(part =>
            string.Equals(part, etag, StringComparison.Ordinal)
            || string.Equals(part.StartsWith("W/", StringComparison.Ordinal) ? part[2..] : part, etag, StringComparison.Ordinal));
    }
}
