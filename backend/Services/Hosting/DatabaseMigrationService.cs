using BankingAPI.Data;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services.Hosting;

/// <summary>
/// Applies pending EF Core migrations when the app starts (set <c>Database:AutoMigrate</c> to false to disable).
/// Retries for a while so the API can start alongside a database that is still booting.
/// Runs as a hosted service, not in Program.cs, so design-time tools such as "dotnet ef" never touch the database.
/// </summary>
public class DatabaseMigrationService : IHostedService
{
    private const int MaxAttempts = 15;

    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseMigrationService> _logger;

    public DatabaseMigrationService(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<DatabaseMigrationService> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("Database:AutoMigrate", true))
        {
            _logger.LogInformation("Automatic migrations are disabled");
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Database.MigrateAsync(cancellationToken);
                _logger.LogInformation("Database is up to date");
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Database not ready (attempt {Attempt}/{Max}): {Message}", attempt, MaxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
