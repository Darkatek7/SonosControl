using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using SonosControl.Web.Models;

namespace SonosControl.Web.Services;

/// <summary>
/// Refreshes role claims for authenticated users on each request so that
/// role changes are reflected after a simple page refresh instead of
/// requiring a full re-login.
/// </summary>
public class RoleClaimsTransformation : IClaimsTransformation
{
    private readonly UserManager<ApplicationUser> _userManager;

    public RoleClaimsTransformation(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = principal.Identity as ClaimsIdentity;
        if (identity?.IsAuthenticated != true)
            return principal;

        var user = await _userManager.GetUserAsync(principal);
        if (user == null)
            return principal;

        // Remove existing role claims and reload from database
        foreach (var claim in identity.FindAll(identity.RoleClaimType).ToList())
        {
            identity.RemoveClaim(claim);
        }

        var roles = await _userManager.GetRolesAsync(user);
        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(identity.RoleClaimType, role));
        }

        return principal;
    }
}

