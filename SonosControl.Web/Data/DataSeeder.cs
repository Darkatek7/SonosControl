using Microsoft.AspNetCore.Identity;
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

        string adminUserName = "admin";
        string adminEmail = "admin@example.com";
        string adminPassword = "ESPmtZ7&LW2z&xHF";

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