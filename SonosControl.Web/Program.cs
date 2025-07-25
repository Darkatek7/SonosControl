using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Repos;
using SonosControl.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using SonosControl.Web.Models; // For ApplicationUser
using SonosControl.Web.Data;   // For ApplicationDbContext


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddTransient<IUnitOfWork, UnitOfWork>();

builder.Services.AddHostedService<SonosControlService>();
builder.Services.AddSingleton<SonosControlService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ActionLogger>();

builder.Services.AddLocalization();
builder.Services.AddControllersWithViews();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();


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
