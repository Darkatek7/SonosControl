using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SonosControl.Web.Models;

namespace SonosControl.Web.Controllers
{
    [Route("auth")]
    public class AuthController : Controller
    {
        private readonly SignInManager<ApplicationUser> _signInManager;

        public AuthController(SignInManager<ApplicationUser> signInManager)
        {
            _signInManager = signInManager;
        }

        [HttpGet("login")]
        public IActionResult Login(string? error = null)
        {
            ViewBag.Error = error == "1" ? "Invalid username or password." : null;
            return View();
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login(string username, string password, bool rememberMe)
        {
            var result = await _signInManager.PasswordSignInAsync(username, password, rememberMe, false);
            if (result.Succeeded)
            {
                return Redirect("/"); // Redirect to your app home or dashboard
            }

            return Redirect("/auth/login?error=1"); // Redirect back with error
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return Redirect("/");  // Redirect to home page or wherever
        }

    }
}