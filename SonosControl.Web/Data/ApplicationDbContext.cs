using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SonosControl.Web.Models;

namespace SonosControl.Web.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<LogEntry> Logs { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Change column type for ConcurrencyStamp in AspNetRoles to TEXT for SQLite
        builder.Entity<IdentityRole>(entity => { entity.Property(r => r.ConcurrencyStamp).HasColumnType("TEXT"); });

        // Similarly, fix other properties if needed:
        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(u => u.PhoneNumber).HasColumnType("TEXT");
            entity.Property(u => u.ConcurrencyStamp).HasColumnType("TEXT");
            entity.Property(u => u.ThemePreference).HasColumnType("TEXT")
                .HasMaxLength(16)
                .HasDefaultValue(ThemePreferenceMode.System.ToIdentifier());
            // add other overrides if necessary
        });

        // You may need to adjust other Identity entities similarly if errors come up
    }
}
