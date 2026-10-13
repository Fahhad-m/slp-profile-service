using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RabotSlp.Data;

public class SlpDbContext : DbContext
{
    // Databases hand DateTime values back with Kind = Unspecified. Everything in this
    // service is UTC, so the kind is set explicitly when reading.
    private static readonly ValueConverter<DateTime, DateTime> UtcConverter =
        new(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public SlpDbContext(DbContextOptions<SlpDbContext> options)
        : base(options)
    {
    }

    public DbSet<Profile> Profiles => Set<Profile>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<StoredProfileValue> ProfileValues => Set<StoredProfileValue>();

    public DbSet<ConsumptionForecast> Forecasts => Set<ConsumptionForecast>();

    public DbSet<ForecastInterval> ForecastIntervals => Set<ForecastInterval>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Master tables.
        modelBuilder.Entity<Profile>(entity =>
        {
            entity.ToTable("Profiles");
            entity.HasKey(x => x.ProfileId);
            entity.Property(x => x.ProfileId).HasMaxLength(32);
            entity.Property(x => x.CreatedAtUtc).HasConversion(UtcConverter);
            entity.Property(x => x.LastSyncedAtUtc).HasConversion(UtcConverter);
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.ToTable("Customers");
            entity.HasKey(x => x.CustomerId);
            entity.Property(x => x.CustomerId).HasMaxLength(64);
            entity.Property(x => x.CreatedAtUtc).HasConversion(UtcConverter);
        });

        // Profile data: Profiles 1 to many ProfileValues.
        modelBuilder.Entity<StoredProfileValue>(entity =>
        {
            entity.ToTable("ProfileValues");
            // The natural key makes the sync an upsert and rules out duplicates.
            entity.HasKey(x => new { x.ProfileId, x.TimestampUtc });
            entity.Property(x => x.ProfileId).HasMaxLength(32);
            entity.Property(x => x.TimestampUtc).HasConversion(UtcConverter);
            entity.Property(x => x.ValueKwh).HasPrecision(18, 3);
            entity.HasOne<Profile>()
                .WithMany()
                .HasForeignKey(x => x.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Results: Customers 1 to many ConsumptionForecasts, Profiles 1 to many ConsumptionForecasts,
        // ConsumptionForecasts 1 to many ForecastIntervals.
        modelBuilder.Entity<ConsumptionForecast>(entity =>
        {
            entity.ToTable("ConsumptionForecasts");
            entity.HasKey(x => x.ForecastId);
            entity.Property(x => x.CustomerId).HasMaxLength(64);
            entity.Property(x => x.ProfileId).HasMaxLength(32);
            entity.Property(x => x.AnnualKwh).HasPrecision(18, 6);
            entity.Property(x => x.ScalingFactor).HasPrecision(28, 12);
            entity.Property(x => x.CalculatedAtUtc).HasConversion(UtcConverter);
            entity.HasIndex(x => new { x.CustomerId, x.ProfileId, x.Year }).IsUnique();
            entity.HasOne<Customer>()
                .WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
            // A profile that still has results cannot be deleted by accident.
            entity.HasOne<Profile>()
                .WithMany()
                .HasForeignKey(x => x.ProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Intervals)
                .WithOne()
                .HasForeignKey(x => x.ForecastId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ForecastInterval>(entity =>
        {
            entity.ToTable("ForecastIntervals");
            entity.HasKey(x => new { x.ForecastId, x.TimestampUtc });
            entity.Property(x => x.TimestampUtc).HasConversion(UtcConverter);
            entity.Property(x => x.ConsumptionKwh).HasPrecision(18, 6);
        });
    }
}
