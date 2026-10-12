namespace BankingAPI.Services.Hosting;

/// <summary>
/// Credits daily interest to savings accounts. It wakes up hourly; <see cref="IInterestService"/> pays each
/// account at most once per UTC day, so restarts and repeat runs never double-pay.
/// Disable with <c>Interest:Enabled=false</c>.
/// </summary>
public class InterestAccrualWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InterestAccrualWorker> _logger;

    public InterestAccrualWorker(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<InterestAccrualWorker> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Interest:Enabled", true))
        {
            _logger.LogInformation("Interest accrual is disabled");
            return;
        }

        // Give the migration service time to finish before the first run.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                using var scope = _services.CreateScope();
                var interest = scope.ServiceProvider.GetRequiredService<IInterestService>();
                await interest.AccrueInterestForAllAccountsAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Interest accrual run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
