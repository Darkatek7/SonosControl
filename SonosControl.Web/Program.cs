using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Repos;
using SonosControl.Web.Services;
using SonosControl.Web.Services.HealthChecks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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
builder.Services.AddRadzenComponents();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient(nameof(SonosConnectorRepo), client =>
{
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddHttpClient("RadioBrowser", client =>
{
    client.BaseAddress = new Uri("https://de1.api.radio-browser.info/");
    client.Timeout = TimeSpan.FromSeconds(8);
});
builder.Services.AddHttpClient(nameof(SonosDeviceDiscoveryService), client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddSingleton<ISettingsRepo, SettingsRepo>(); // Register ISettingsRepo
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>(); // Changed to Scoped
builder.Services.AddSingleton<IMetricsCollector, MetricsCollector>();
builder.Services.AddScoped<ISceneOrchestrationService, SceneOrchestrationService>();
builder.Services.AddScoped<ICollaborativeJukeboxService, CollaborativeJukeboxService>();
builder.Services.AddSingleton<ISonosDeviceDiscoveryService, SonosDeviceDiscoveryService>();
builder.Services.AddSingleton<IDeviceHealthSnapshotStore, DeviceHealthSnapshotStore>();

builder.Services.AddHostedService<SonosControlService>();
builder.Services.AddHostedService<PlaybackMonitorService>();
builder.Services.AddHostedService<ScheduleWindowAutomationService>();
builder.Services.AddHostedService<DeviceHealthMonitorService>();
// builder.Services.AddSingleton<SonosControlService>(); // Removed redundant registration
builder.Services.AddSingleton<HolidayCalendarSyncService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ActionLogger>();
builder.Services.AddScoped<IClaimsTransformation, RoleClaimsTransformation>();
builder.Services.AddScoped<SonosControl.Web.Services.ThemeService>();
builder.Services.AddScoped<INotifier, DiscordNotificationService>();
builder.Services.AddScoped<INotifier, TeamsNotificationService>();
builder.Services.AddScoped<INotificationService, AggregateNotificationService>();

builder.Services.AddLocalization();
builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddCheck<SettingsHealthCheck>("settings");

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.AllowedForNewUsers = true;
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

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "SAMEORIGIN");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Permissions-Policy", "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()");
    await next();
});

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
    endpoints.MapHealthChecks("/healthz", new HealthCheckOptions
    {
        AllowCachingResponses = false
    });
    endpoints.MapGet("/metricsz", (IMetricsCollector metricsCollector, IConfiguration configuration) =>
    {
        var enabled = configuration.GetValue<bool?>("Observability:EnableMetrics") ?? true;
        return enabled ? Results.Ok(metricsCollector.GetSnapshot()) : Results.NotFound();
    });
    endpoints.MapFallbackToPage("/_Host"); // only here once
});

app.UseRequestLocalization(new RequestLocalizationOptions()
    .AddSupportedCultures(new[] { "de-AT", "de-DE", "de", "en-US", "en-GB", "en", "fr-FR", "fr", "es-ES", "es", "it-IT", "it", "nl-NL", "nl" })
    .AddSupportedUICultures(new[] { "de-AT", "de-DE", "de", "en-US", "en-GB", "en", "fr-FR", "fr", "es-ES", "es", "it-IT", "it", "nl-NL", "nl" })
    .SetDefaultCulture("de-AT"));

app.Run();
