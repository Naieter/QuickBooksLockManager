using Microsoft.EntityFrameworkCore;
using QBLockService.Models;

namespace QBLockService.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ActiveLock> ActiveLocks => Set<ActiveLock>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActiveLock>(entity =>
        {
            entity.HasKey(e => e.LockId);
            entity.Property(e => e.Status)
                  .HasConversion<string>();

            // Partial unique index: only one Active lock per FileKey at a time.
            // SQLite enforces this; EF Core generates the WHERE clause filter.
            entity.HasIndex(e => e.FileKey)
                  .HasFilter("[Status] = 'Active'")
                  .IsUnique()
                  .HasDatabaseName("UQ_ActiveLocks_FileKey_Active");
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.AuditId);
            entity.Property(e => e.AuditId).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.TimestampUtc);
            entity.HasIndex(e => e.FileKey);
        });
    }
}
