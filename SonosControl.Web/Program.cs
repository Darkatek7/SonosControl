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
using System.Globalization;

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
var settingsDataDirectory = Path.Combine(builder.Environment.ContentRootPath, "Data");
builder.Services.AddSingleton<ISettingsRepo>(_ => new SettingsRepo(settingsDataDirectory));
builder.Services.Configure<YouTubePlaybackOptions>(builder.Configuration.GetSection("Playback"));
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>(); // Changed to Scoped
builder.Services.AddSingleton<IMetricsCollector, MetricsCollector>();
builder.Services.AddSingleton<IYouTubeToolRunner, YouTubeToolRunner>();
builder.Services.AddSingleton<YouTubePlaybackService>();
builder.Services.AddSingleton<IYouTubePlaybackService>(sp => sp.GetRequiredService<YouTubePlaybackService>());
builder.Services.AddScoped<ISceneOrchestrationService, SceneOrchestrationService>();
builder.Services.AddScoped<ICollaborativeJukeboxService, CollaborativeJukeboxService>();
builder.Services.AddSingleton<ISonosDeviceDiscoveryService, SonosDeviceDiscoveryService>();
builder.Services.AddSingleton<IDeviceHealthSnapshotStore, DeviceHealthSnapshotStore>();

builder.Services.AddSingleton<AutomationRuntimeStatus>();
builder.Services.AddSingleton<ConfiguredTimeZoneService>();
builder.Services.AddSingleton<ISettingsSchemaMigrationService, SettingsSchemaMigrationService>();
builder.Services.AddSingleton<AutomationSchedulerService>();
builder.Services.AddSingleton<IAutomationScheduler>(sp => sp.GetRequiredService<AutomationSchedulerService>());
if (builder.Configuration.GetValue("BackgroundServices:Enabled", true))
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AutomationSchedulerService>());
    builder.Services.AddHostedService<PlaybackMonitorService>();
    builder.Services.AddHostedService<DeviceHealthMonitorService>();
    builder.Services.AddHostedService<YouTubePlaybackMaintenanceService>();
    builder.Services.AddHostedService<YouTubePlaybackCleanupService>();
}
builder.Services.AddSingleton<HolidayCalendarSyncService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ActionLogger>();
builder.Services.AddScoped<IClaimsTransformation, RoleClaimsTransformation>();
builder.Services.AddScoped<SonosControl.Web.Services.ThemeService>();
builder.Services.AddScoped<PlaybackUiStateService>();
builder.Services.AddScoped<SettingsAutosaveCoordinator>();
builder.Services.AddScoped<UserFavouriteSourceService>();
builder.Services.AddScoped<HomeLibraryService>();
builder.Services.AddScoped<INotifier, DiscordNotificationService>();
builder.Services.AddScoped<INotifier, TeamsNotificationService>();
builder.Services.AddScoped<INotificationService, AggregateNotificationService>();

builder.Services.AddLocalization();
var enGbCulture = CultureInfo.GetCultureInfo("en-GB");
CultureInfo.DefaultThreadCurrentCulture = enGbCulture;
CultureInfo.DefaultThreadCurrentUICulture = enGbCulture;
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(enGbCulture);
    options.SupportedCultures = new[] { enGbCulture };
    options.SupportedUICultures = new[] { enGbCulture };
});
builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddCheck<SettingsHealthCheck>("settings")
    .AddCheck<AutomationHealthCheck>("automation");

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

app.UseRequestLocalization();

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

app.Run();
