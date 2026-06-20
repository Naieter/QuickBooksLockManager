using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using QBLockService.Data;
using QBLockService.Models;
using QBLockService.Services;

namespace QBLockService.Tests;

/// <summary>
/// Holds an open SqliteConnection so the in-memory database persists for the duration of a test.
/// Dispose this to drop the database.
/// </summary>
public sealed class TestContext : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;
    public AppDbContext Db { get; }
    public ILockService Service { get; }

    public TestContext(SqliteConnection keepAlive, AppDbContext db, ILockService service)
    {
        _keepAlive = keepAlive;
        Db = db;
        Service = service;
    }

    // Allow: var (db, svc) = await TestHelpers.CreateInMemoryAsync();
    public void Deconstruct(out AppDbContext db, out ILockService service)
    {
        db = Db;
        service = Service;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }
}

public static class TestHelpers
{
    public static async Task<TestContext> CreateAsync(int timeoutMinutes = 5)
    {
        // Keep the connection open so the in-memory DB survives multiple DbContext instances.
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn)
            .Options;

        var factory = new TestDbContextFactory(options);

        // Seed schema
        var seedCtx = new AppDbContext(options);
        await seedCtx.Database.EnsureCreatedAsync();
        await seedCtx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS UQ_ActiveLocks_FileKey_Active " +
            "ON ActiveLocks(FileKey) WHERE Status = 'Active';");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LockSettings:TimeoutMinutes"] = timeoutMinutes.ToString()
            })
            .Build();

        var service = new LockService(factory, config, NullLogger<LockService>.Instance);
        return new TestContext(conn, seedCtx, service);
    }

    /// <summary>Convenience for tests that don't need to await IAsyncDisposable.</summary>
    public static Task<TestContext> CreateInMemoryAsync(int timeoutMinutes = 5)
        => CreateAsync(timeoutMinutes);

    public static AcquireLockRequest MakeRequest(
        string fileKey = "testfile.qbw",
        string userName = "user1",
        string machine = "PC-001",
        string? appInstanceId = null) => new()
    {
        FileKey = fileKey,
        FileName = fileKey,
        LocalPath = $@"C:\QB\{fileKey}",
        UserName = userName,
        MachineName = machine,
        AppInstanceId = appInstanceId ?? Guid.NewGuid().ToString("N")[..16]
    };
}

/// <summary>Wraps DbContextOptions so tests can share a single SQLite connection.</summary>
public class TestDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;
    public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
    public AppDbContext CreateDbContext() => new(_options);
    public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}
