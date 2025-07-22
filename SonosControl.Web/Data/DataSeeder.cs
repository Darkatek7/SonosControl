using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SonosControl.Web.Models; // Your ApplicationUser

public static class DataSeeder
{
    public static async Task SeedAdminUser(IServiceProvider serviceProvider)
    {
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        string adminRoleName = "admin";
        string adminUserName = "admin";
        string adminEmail = "admin@example.com"; // optional
        string adminPassword = "ESPmtZ7&LW2z&xHF";

        // Create admin role if it doesn't exist
        if (!await roleManager.RoleExistsAsync(adminRoleName))
        {
            await roleManager.CreateAsync(new IdentityRole(adminRoleName));
        }

        // Create admin user if it doesn't exist
        var adminUser = await userManager.FindByNameAsync(adminUserName);
        if (adminUser == null)
        {
            adminUser = new ApplicationUser
            {
                UserName = adminUserName,
                Email = adminEmail,
                EmailConfirmed = true, // optional
                FirstName = "Admin",   // <-- Added
                LastName = "User"      // <-- Added
            };

            var result = await userManager.CreateAsync(adminUser, adminPassword);
            if (!result.Succeeded)
            {
                throw new Exception($"Failed to create admin user: {string.Join(", ", result.Errors.Select(e => e.Description))}");
            }
        }

        // Add admin user to admin role if not already in it
        if (!await userManager.IsInRoleAsync(adminUser, adminRoleName))
        {
            await userManager.AddToRoleAsync(adminUser, adminRoleName);
        }
    }
}