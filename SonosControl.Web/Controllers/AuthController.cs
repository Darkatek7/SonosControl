using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SonosControl.Web.Models;
using SonosControl.DAL.Interfaces;

namespace SonosControl.Web.Controllers
{
    [Route("auth")]
    public class AuthController : Controller
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly ISettingsRepo _settingsRepo;

        public AuthController(SignInManager<ApplicationUser> signInManager, ISettingsRepo settingsRepo)
        {
            _signInManager = signInManager;
            _settingsRepo = settingsRepo;
        }

        [HttpGet("login")]
        public async Task<IActionResult> Login(string? error = null, string? registered = null)
        {
            ViewBag.Error = error switch
            {
                "1" => "Invalid username or password.",
                "lockedout" => "This account is temporarily locked after too many failed sign-in attempts. Please try again in five minutes.",
                _ => null
            };
            ViewBag.Registered = registered == "1";
            try
            {
                var settings = await _settingsRepo.GetSettings();
                ViewBag.AllowRegistration = settings?.AllowUserRegistration ?? true;
            }
            catch (SonosControl.DAL.Repos.SettingsReadException ex)
            {
                // Keep administrator login available for explicit configuration recovery.
                ViewBag.AllowRegistration = false;
                ViewBag.SettingsError = ex.Message;
            }
            return View();
        }

        [HttpPost("login")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string username, string password, bool rememberMe)
        {
            var result = await _signInManager.PasswordSignInAsync(username, password, rememberMe, true);
            if (result.Succeeded)
            {
                return Redirect("/"); // Redirect to your app home or dashboard
            }
            if (result.IsLockedOut)
            {
                return Redirect("/auth/login?error=lockedout");
            }

            return Redirect("/auth/login?error=1"); // Redirect back with error
        }

        [HttpPost("logout")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return Redirect("/");  // Redirect to home page or wherever
        }

        [HttpPost("register")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register()
        {
            var settings = await _settingsRepo.GetSettings();
            if (settings?.AllowUserRegistration == false)
            {
                return Forbid();
            }

            return BadRequest();
        }

    }
}
