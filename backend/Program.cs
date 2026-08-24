using BankingAPI.Data;
using BankingAPI.Services;
using BankingAPI.Middleware;
using BankCore.Services.Saga;
using BankCore.Services.Sharding;
using BankCore.Services.ReadReplica;
using BankCore.Services.Caching;
using BankCore.Services.Repositories;
using BankCore.Services.UnitOfWork;
using BankCore.Services.ConnectionPooling;
using BankCore.Services.Indexing;
using BankCore.Services.RateLimiting;
using BankCore.Services.Idempotency;
using BankCore.Services.Logging;
using BankCore.Services.ErrorHandling;
using BankCore.Services.Patterns;
using BankCore.Services.CapacityEstimation;
using BankCore.Services.SystemConstraints;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Redis
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis");
});

// JWT Authentication
var jwtSecret = builder.Configuration["Jwt:Secret"] ?? throw new InvalidOperationException("JWT Secret not configured");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
        };
    });

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular",
        policy => policy
            .WithOrigins("http://localhost:4200")
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials());
});

// Register Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IInterestService, InterestService>();

// HLD/LLD Pattern Services
builder.Services.AddSingleton<SagaManager>();
builder.Services.AddSingleton<DatabaseShardingService>();
builder.Services.AddSingleton<ReadReplicaService>();
builder.Services.AddSingleton<DistributedCacheService>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
builder.Services.AddScoped(typeof(IPaginatedRepository<>), typeof(PaginatedRepository<>));
builder.Services.AddSingleton<ConnectionPoolService>();
builder.Services.AddScoped<DatabaseIndexingService>();
builder.Services.AddSingleton<RateLimitingService>();
builder.Services.AddSingleton<IdempotencyService>();
builder.Services.AddSingleton<StructuredLoggingService>();
builder.Services.AddSingleton<ErrorHandlingService>();
builder.Services.AddScoped<IPaymentStrategy, StripePaymentStrategy>();
builder.Services.AddScoped<IPaymentStrategy, PayPalPaymentStrategy>();
builder.Services.AddSingleton<PaymentStrategyFactory>();
builder.Services.AddSingleton<ITransactionFactory, TransactionFactory>();
builder.Services.AddSingleton<CapacityEstimationService>();
builder.Services.AddSingleton<SystemConstraintsAnalysisService>();

// Configure Options
builder.Services.Configure<ShardingOptions>(builder.Configuration.GetSection("Sharding"));
builder.Services.Configure<ReadReplicaOptions>(builder.Configuration.GetSection("ReadReplica"));
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));
builder.Services.Configure<ConnectionPoolOptions>(builder.Configuration.GetSection("ConnectionPool"));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection("RateLimit"));
builder.Services.Configure<IdempotencyOptions>(builder.Configuration.GetSection("Idempotency"));
builder.Services.Configure<ErrorHandlingOptions>(builder.Configuration.GetSection("ErrorHandling"));
builder.Services.Configure<StripeOptions>(builder.Configuration.GetSection("Stripe"));
builder.Services.Configure<PayPalOptions>(builder.Configuration.GetSection("PayPal"));
builder.Services.Configure<CapacityOptions>(builder.Configuration.GetSection("Capacity"));
builder.Services.Configure<BankingComplianceOptions>(builder.Configuration.GetSection("BankingCompliance"));

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ErrorHandlingMiddleware>();

app.UseCors("AllowAngular");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
