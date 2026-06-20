namespace QBLockService.Services;

public class StaleChecker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<StaleChecker> _logger;

    public StaleChecker(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<StaleChecker> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _config.GetValue<int>("LockSettings:StaleCheckIntervalSeconds", 60);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var lockService = scope.ServiceProvider.GetRequiredService<ILockService>();
                await lockService.ExpireStaleLocksAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during stale lock expiration check.");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }
}
