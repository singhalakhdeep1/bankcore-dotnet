namespace BankingAPI.Services.Hosting;

/// <summary>
/// Base for periodic background jobs. Each tick runs in its own DI scope, a failing tick is logged and
/// retried at the next interval, and the interval (seconds) comes from configuration.
/// </summary>
public abstract class PeriodicJobWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    protected ILogger Logger { get; }

    protected PeriodicJobWorker(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        _services = services;
        _configuration = configuration;
        Logger = logger;
    }

    protected abstract string Name { get; }
    protected abstract string IntervalKey { get; }
    protected abstract int DefaultIntervalSeconds { get; }
    protected abstract Task RunAsync(IServiceProvider scopedServices, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Workers:Enabled", true))
        {
            Logger.LogInformation("{Worker} is disabled", Name);
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _configuration.GetValue(IntervalKey, DefaultIntervalSeconds)));

        // Give the migration service time to finish before the first run.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                using var scope = _services.CreateScope();
                await RunAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "{Worker} run failed", Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Runs due recurring payments (default: every hour).</summary>
public class RecurringPaymentWorker : PeriodicJobWorker
{
    public RecurringPaymentWorker(IServiceProvider services, IConfiguration configuration, ILogger<RecurringPaymentWorker> logger)
        : base(services, configuration, logger)
    {
    }

    protected override string Name => nameof(RecurringPaymentWorker);
    protected override string IntervalKey => "Workers:RecurringIntervalSeconds";
    protected override int DefaultIntervalSeconds => 3600;

    protected override async Task RunAsync(IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        var attempted = await scopedServices.GetRequiredService<IRecurringPaymentService>().ProcessDueAsync();
        if (attempted > 0) Logger.LogInformation("Processed {Count} recurring payments", attempted);
    }
}

/// <summary>Moves ACH/wire/SWIFT transfers through processing to settled (default: every 5 minutes).</summary>
public class BankTransferSettlementWorker : PeriodicJobWorker
{
    public BankTransferSettlementWorker(IServiceProvider services, IConfiguration configuration, ILogger<BankTransferSettlementWorker> logger)
        : base(services, configuration, logger)
    {
    }

    protected override string Name => nameof(BankTransferSettlementWorker);
    protected override string IntervalKey => "Workers:SettlementIntervalSeconds";
    protected override int DefaultIntervalSeconds => 300;

    protected override async Task RunAsync(IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        var changed = await scopedServices.GetRequiredService<IBankTransferService>().SettleDueAsync();
        if (changed > 0) Logger.LogInformation("Advanced {Count} bank transfers", changed);
    }
}
