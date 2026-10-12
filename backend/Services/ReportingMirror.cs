using BankingAPI.Models;
using MongoDB.Bson;

namespace BankingAPI.Services;

public interface IReportingMirror
{
    Task MirrorRegulatoryReportAsync(RegulatoryReport report);
    Task<Dictionary<string, object>> CheckDatabasesAsync();
}

/// <summary>
/// Copies regulatory report summaries to the optional reporting databases: MySQL and SQL Server get a
/// relational summary row, MongoDB gets the full JSON document. PostgreSQL remains the system of record;
/// each mirror is skipped when its connection string is not configured, and a mirror failure never fails the request.
/// </summary>
public class ReportingMirror : IReportingMirror
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ReportingMirror> _logger;

    public ReportingMirror(IServiceProvider services, ILogger<ReportingMirror> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task MirrorRegulatoryReportAsync(RegulatoryReport report)
    {
        var summary = new Dictionary<string, object>
        {
            ["@id"] = report.Id.ToString(),
            ["@type"] = report.Type.ToString(),
            ["@from"] = report.PeriodStart,
            ["@to"] = report.PeriodEnd,
            ["@items"] = report.ItemCount,
            ["@generated"] = report.GeneratedAt
        };

        var mysql = _services.GetService<MySQLService>();
        if (mysql != null)
        {
            await TryAsync("MySQL", async () =>
            {
                await mysql.ExecuteNonQueryAsync(
                    @"CREATE TABLE IF NOT EXISTS regulatory_reports (
                        id CHAR(36) PRIMARY KEY, report_type VARCHAR(30) NOT NULL, period_start DATETIME NOT NULL,
                        period_end DATETIME NOT NULL, item_count INT NOT NULL, generated_at DATETIME NOT NULL)");
                await mysql.ExecuteNonQueryAsync(
                    @"INSERT IGNORE INTO regulatory_reports (id, report_type, period_start, period_end, item_count, generated_at)
                      VALUES (@id, @type, @from, @to, @items, @generated)", summary);
            });
        }

        var sql = _services.GetService<SQLServerService>();
        if (sql != null)
        {
            await TryAsync("SQL Server", async () =>
            {
                await sql.ExecuteNonQueryAsync(
                    @"IF OBJECT_ID('dbo.regulatory_reports') IS NULL
                      CREATE TABLE dbo.regulatory_reports (
                        id UNIQUEIDENTIFIER PRIMARY KEY, report_type NVARCHAR(30) NOT NULL, period_start DATETIME2 NOT NULL,
                        period_end DATETIME2 NOT NULL, item_count INT NOT NULL, generated_at DATETIME2 NOT NULL)");
                await sql.ExecuteNonQueryAsync(
                    @"IF NOT EXISTS (SELECT 1 FROM dbo.regulatory_reports WHERE id = @id)
                      INSERT INTO dbo.regulatory_reports (id, report_type, period_start, period_end, item_count, generated_at)
                      VALUES (@id, @type, @from, @to, @items, @generated)", summary);
            });
        }

        var mongo = _services.GetService<MongoDBService>();
        if (mongo != null)
        {
            await TryAsync("MongoDB", () => mongo.InsertOneAsync("regulatory_reports", new BsonDocument
            {
                { "_id", report.Id.ToString() },
                { "type", report.Type.ToString() },
                { "periodStart", report.PeriodStart },
                { "periodEnd", report.PeriodEnd },
                { "itemCount", report.ItemCount },
                { "generatedAt", report.GeneratedAt },
                { "payload", BsonDocument.Parse(report.PayloadJson) }
            }));
        }
    }

    public async Task<Dictionary<string, object>> CheckDatabasesAsync()
    {
        var result = new Dictionary<string, object>();

        async Task Check(string name, Func<Task<bool>>? probe)
        {
            if (probe == null)
            {
                result[name] = "not configured";
                return;
            }

            try
            {
                result[name] = await probe() ? "ok" : "unreachable";
            }
            catch (Exception ex)
            {
                result[name] = $"error: {ex.GetType().Name}";
            }
        }

        var mysql = _services.GetService<MySQLService>();
        var sql = _services.GetService<SQLServerService>();
        var mongo = _services.GetService<MongoDBService>();
        var raven = _services.GetService<RavenDBService>();

        await Check("mysql", mysql == null ? null : mysql.TestConnectionAsync);
        await Check("sqlServer", sql == null ? null : sql.TestConnectionAsync);
        await Check("mongodb", mongo == null ? null : mongo.TestConnectionAsync);
        await Check("ravendb", raven == null ? null : raven.TestConnectionAsync);
        return result;
    }

    private async Task TryAsync(string target, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reporting mirror to {Target} failed", target);
        }
    }
}
