using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using SonosControl.Web.Models;

public static class DataSeeder
{
    public static async Task SeedAdminUser(IServiceProvider serviceProvider)
    {
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        string[] roles = { "superadmin", "admin", "operator" };

        // Ensure all roles exist
        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        var configuration = serviceProvider.GetRequiredService<IConfiguration>();

        string? adminUserName = configuration["Admin:UserName"]
                                 ?? Environment.GetEnvironmentVariable("ADMIN_USERNAME");
        string? adminEmail = configuration["Admin:Email"]
                               ?? Environment.GetEnvironmentVariable("ADMIN_EMAIL");
        string? adminPassword = configuration["Admin:Password"]
                                  ?? Environment.GetEnvironmentVariable("ADMIN_PASSWORD");

        if (string.IsNullOrWhiteSpace(adminUserName) ||
            string.IsNullOrWhiteSpace(adminEmail) ||
            string.IsNullOrWhiteSpace(adminPassword))
        {
            throw new InvalidOperationException(
                "Admin seeding requires ADMIN_USERNAME, ADMIN_EMAIL, and ADMIN_PASSWORD to be provided");
        }

        foreach (var validator in userManager.PasswordValidators)
        {
            var validationResult = await validator.ValidateAsync(
                userManager,
                new ApplicationUser { UserName = adminUserName, Email = adminEmail },
                adminPassword);

            if (!validationResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Admin password does not meet requirements: {string.Join(", ", validationResult.Errors.Select(e => e.Description))}");
            }
        }

        var adminUser = await userManager.FindByNameAsync(adminUserName);
        if (adminUser == null)
        {
            adminUser = new ApplicationUser
            {
                UserName = adminUserName,
                Email = adminEmail,
                EmailConfirmed = true,
                FirstName = "Admin",
                LastName = "User"
            };

            var result = await userManager.CreateAsync(adminUser, adminPassword);
            if (!result.Succeeded)
            {
                throw new Exception($"Failed to create admin user: {string.Join(", ", result.Errors.Select(e => e.Description))}");
            }
        }

        // Ensure user is in admin and superadmin roles
        if (!await userManager.IsInRoleAsync(adminUser, "admin"))
        {
            await userManager.AddToRoleAsync(adminUser, "admin");
        }

        if (!await userManager.IsInRoleAsync(adminUser, "superadmin"))
        {
            await userManager.AddToRoleAsync(adminUser, "superadmin");
        }
    }
}