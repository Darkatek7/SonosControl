using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Repos;
using SonosControl.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using System.IO;

using SonosControl.Web.Models; // For ApplicationUser
using SonosControl.Web.Data;   // For ApplicationDbContext
using Radzen;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddHttpClient();
builder.Services.AddTransient<IUnitOfWork, UnitOfWork>();

builder.Services.AddRadzenComponents();

builder.Services.AddHostedService<SonosControlService>();
builder.Services.AddSingleton<SonosControlService>();
builder.Services.AddSingleton<HolidayCalendarSyncService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ActionLogger>();
builder.Services.AddScoped<IClaimsTransformation, RoleClaimsTransformation>();
builder.Services.AddScoped<ThemeService>();

builder.Services.AddLocalization();
builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromDays(30);

    options.Events.OnSigningIn = context =>
    {
        if (context.Properties.IsPersistent)
        {
            // Ensure persistent logins survive for the full 30 days
            context.Properties.ExpiresUtc ??= DateTimeOffset.UtcNow.AddDays(30);
        }
        else
        {
            // Clear expiration => session cookie
            context.Properties.ExpiresUtc = null;
        }
        return Task.CompletedTask;
    };
});

// Configure persistent data protection keys so cookies survive restarts
var keysDirectory = builder.Configuration.GetValue<string>("DataProtection:KeysDirectory")
                   ?? Path.Combine(builder.Environment.ContentRootPath, "DataProtectionKeys");
Directory.CreateDirectory(keysDirectory);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate(); // Apply pending migrations or create DB schema
}

// Seed admin user/role
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    await DataSeeder.SeedAdminUser(services);
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapDefaultControllerRoute();
    endpoints.MapBlazorHub();
    endpoints.MapFallbackToPage("/_Host"); // only here once
});

app.UseRequestLocalization(new RequestLocalizationOptions()
    .AddSupportedCultures(new[] { "de-AT", "en-US" })
    .AddSupportedUICultures(new[] { "de-AT", "en-US" })
    .SetDefaultCulture("de-AT"));

app.Run();
