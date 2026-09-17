using BillAssistant.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace BillAssistant.Infrastructure.Persistence;

/// <summary>Relational store for bills and their extracted metadata.</summary>
public sealed class BillDbContext(DbContextOptions<BillDbContext> options) : DbContext(options)
{
    public DbSet<Bill> Bills => Set<Bill>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var bill = modelBuilder.Entity<Bill>();

        bill.ToTable("Bills");
        bill.HasKey(b => b.Id);

        bill.Property(b => b.FileName).HasMaxLength(260).IsRequired();
        bill.Property(b => b.ProviderName).HasMaxLength(200);
        bill.Property(b => b.AccountNumber).HasMaxLength(8);
        bill.Property(b => b.Currency).HasMaxLength(3);
        bill.Property(b => b.UsageUnit).HasMaxLength(20);
        bill.Property(b => b.MetadataNotes).HasMaxLength(2000);

        // SQLite has no date type and cannot ORDER BY a DateTimeOffset, so upload time is stored as
        // UTC ticks. Ticks sort chronologically, which keeps "most recently uploaded" a SQL sort.
        bill.Property(b => b.UploadedAt).HasConversion(
            value => value.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

        // Enums as text: a database a human may open with sqlite3 should be readable.
        bill.Property(b => b.Utility).HasConversion<string>().HasMaxLength(20);
        bill.Property(b => b.MetadataStatus).HasConversion<string>().HasMaxLength(20);

        // Money lives in AmountDueMinor; AmountDue is a computed facade over it.
        bill.Ignore(b => b.AmountDue);
        bill.Ignore(b => b.PeriodLabel);

        // The three axes every query filters or sorts on.
        bill.HasIndex(b => b.Utility);
        bill.HasIndex(b => b.PeriodEnd);
        bill.HasIndex(b => b.UploadedAt);
    }
}
