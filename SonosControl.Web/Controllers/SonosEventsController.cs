using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SonosControl.DAL.Repos;
using SonosControl.Web.Services.SonosEvents;

namespace SonosControl.Web.Controllers;

[ApiController]
[AllowAnonymous] // Speakers send NOTIFY without the user's authentication cookie; the token authorizes the callback.
[Route("api/sonos-events")]
public sealed class SonosEventsController : ControllerBase
{
    private readonly SonosEventSubscriptionRegistry _registry;
    private readonly SonosEventDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;

    public SonosEventsController(SonosEventSubscriptionRegistry registry, SonosEventDispatcher dispatcher, TimeProvider timeProvider)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
    }

    [AcceptVerbs("NOTIFY", Route = "{token}")]
    [RequestSizeLimit(SonosEventParser.MaxBodyLength)]
    public async Task<IActionResult> NotifyAsync(string token, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(token, out var registration)) return NotFound();
        if (!registration.MatchesSid(Request.Headers["SID"])) return StatusCode(StatusCodes.Status412PreconditionFailed);
        if (Request.Headers["NT"] != "upnp:event" || Request.Headers["NTS"] != "upnp:propchange"
            || !long.TryParse(Request.Headers["SEQ"], out var sequence) || sequence < 0)
            return BadRequest();
        if (Request.ContentLength > SonosEventParser.MaxBodyLength) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);
        if (body.Length > SonosEventParser.MaxBodyLength) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        _dispatcher.Enqueue(new SonosEventNotification(registration, sequence, body, _timeProvider.GetUtcNow()));
        return Ok();
    }
}
