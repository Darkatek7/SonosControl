using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.Web.Controllers;
using SonosControl.Web.Models;
using Xunit;

namespace SonosControl.Tests.Controllers
{
    public class AuthControllerTests
    {
        private readonly Mock<UserManager<ApplicationUser>> _mockUserManager;
        private readonly Mock<IUserClaimsPrincipalFactory<ApplicationUser>> _mockClaimsFactory;
        private readonly Mock<SignInManager<ApplicationUser>> _mockSignInManager;
        private readonly Mock<IUnitOfWork> _mockUow;
        private readonly AuthController _controller;

        public AuthControllerTests()
        {
            _mockUserManager = new Mock<UserManager<ApplicationUser>>(
                new Mock<IUserStore<ApplicationUser>>().Object,
                null, null, null, null, null, null, null, null);

            _mockClaimsFactory = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();

            var contextAccessor = new Mock<IHttpContextAccessor>();
            contextAccessor.Setup(x => x.HttpContext).Returns(new DefaultHttpContext());

            _mockSignInManager = new Mock<SignInManager<ApplicationUser>>(
                _mockUserManager.Object,
                contextAccessor.Object,
                _mockClaimsFactory.Object,
                null, null, null, null);

            _mockUow = new Mock<IUnitOfWork>();

            _controller = new AuthController(_mockSignInManager.Object, _mockUow.Object);
        }

        [Fact]
        public async Task Login_ValidCredentials_RedirectsToHome()
        {
            // Arrange
            _mockSignInManager.Setup(x => x.PasswordSignInAsync("user", "pass", false, true))
                .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Success);

            // Act
            var result = await _controller.Login("user", "pass", false);

            // Assert
            var redirectResult = Assert.IsType<RedirectResult>(result);
            Assert.Equal("/", redirectResult.Url);
        }

        [Fact]
        public async Task Login_LockedOut_RedirectsToLockedOut()
        {
            // Arrange
            _mockSignInManager.Setup(x => x.PasswordSignInAsync("user", "pass", false, true))
                .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.LockedOut);

            // Act
            var result = await _controller.Login("user", "pass", false);

            // Assert
            var redirectResult = Assert.IsType<RedirectResult>(result);
            Assert.Equal("/auth/login?error=lockedout", redirectResult.Url);
        }

        [Fact]
        public async Task Login_InvalidCredentials_RedirectsToError()
        {
            // Arrange
            _mockSignInManager.Setup(x => x.PasswordSignInAsync("user", "pass", false, true))
                .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Failed);

            // Act
            var result = await _controller.Login("user", "pass", false);

            // Assert
            var redirectResult = Assert.IsType<RedirectResult>(result);
            Assert.Equal("/auth/login?error=1", redirectResult.Url);
        }
    }
}
