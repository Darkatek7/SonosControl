using SonosControl.Web.Data;
using SonosControl.Web.Models;

namespace SonosControl.Web.Services;

public class ActionLogger
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ActionLogger(ApplicationDbContext context, IHttpContextAccessor httpContextAccessor)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task LogAsync(string action, string? details = null)
    {
        var user = _httpContextAccessor.HttpContext?.User?.Identity?.Name;

        var log = new LogEntry
        {
            Action = action,
            PerformedBy = user ?? "Unknown",
            Details = details
        };

        _context.Logs.Add(log);
        await _context.SaveChangesAsync();
    }
}
