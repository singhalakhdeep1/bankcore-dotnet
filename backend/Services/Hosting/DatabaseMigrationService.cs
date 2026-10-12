using BankingAPI.Data;
using BankingAPI.Models;
using BankingAPI.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services.Hosting;

/// <summary>
/// On startup: applies pending EF Core migrations (set <c>Database:AutoMigrate</c> to false to disable) and
/// seeds reference data. Retries for a while so the API can start alongside a database that is still booting.
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

                await SeedAsync(db, cancellationToken);
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

    private async Task SeedAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        // Demo watch-list entries (fictional names) so sanctions screening can be exercised out of the box.
        if (_configuration.GetValue("Seed:SampleSanctions", true) && !await db.SanctionedEntities.AnyAsync(cancellationToken))
        {
            var samples = new[]
            {
                ("Test Sanctioned Person", (string?)null, "XX"),
                ("Blocked Example Trading Ltd", null, "XX"),
                ("Demo Watchlist Holdings", "SANCTIONED000000000001", "XX")
            };

            foreach (var (name, account, country) in samples)
            {
                db.SanctionedEntities.Add(new SanctionedEntity
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    NormalizedName = SanctionsService.Normalize(name),
                    AccountNumber = account,
                    Country = country,
                    ListName = "DEMO-WATCHLIST"
                });
            }

            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Seeded {Count} sample sanctions entries", samples.Length);
        }

        // First administrator, created only when Seed:AdminEmail and Seed:AdminPassword are provided.
        var email = _configuration["Seed:AdminEmail"]?.Trim().ToLowerInvariant();
        var password = _configuration["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, cancellationToken)) return;

        var problems = PasswordPolicy.Validate(password, email);
        if (problems.Count > 0)
        {
            _logger.LogError("Seed admin was not created: password does not meet the policy ({Problems})", string.Join(" ", problems));
            return;
        }

        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            FirstName = _configuration["Seed:AdminFirstName"] ?? "System",
            LastName = _configuration["Seed:AdminLastName"] ?? "Administrator",
            Role = UserRole.Admin,
            KycStatus = KycStatus.Verified,
            IsActive = true
        });

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Seeded administrator {Email}", email);
    }
}
