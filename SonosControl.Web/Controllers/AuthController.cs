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
        private readonly IUnitOfWork _uow;

        public AuthController(SignInManager<ApplicationUser> signInManager, IUnitOfWork uow)
        {
            _signInManager = signInManager;
            _uow = uow;
        }

        [HttpGet("login")]
        public IActionResult Login(string? error = null)
        {
            ViewBag.Error = error == "1" ? "Invalid username or password." : null;
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
            var settings = await _uow.ISettingsRepo.GetSettings();
            if (settings?.AllowUserRegistration == false)
            {
                return Forbid();
            }

            return BadRequest();
        }

    }
}