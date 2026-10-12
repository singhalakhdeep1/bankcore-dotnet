using System.Text;
using System.Threading.RateLimiting;
using BankingAPI.Data;
using BankingAPI.Middleware;
using BankingAPI.Services;
using BankingAPI.Services.Hosting;
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
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// ---------------------------------------------------------------- MVC + Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "BankCore API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT access token. Paste the token only; the 'Bearer ' prefix is added for you.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

// ---------------------------------------------------------------- Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHealthChecks().AddDbContextCheck<ApplicationDbContext>("database");

// ---------------------------------------------------------------- Cache (Redis, or in-memory when no Redis is configured)
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddDistributedMemoryCache();
}
else
{
    builder.Services.AddStackExchangeRedisCache(options => options.Configuration = redisConnection);
}

// ---------------------------------------------------------------- JWT authentication
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret) || Encoding.UTF8.GetByteCount(jwtSecret) < 32)
{
    throw new InvalidOperationException("Jwt:Secret must be configured and at least 32 bytes long.");
}

if (!builder.Environment.IsDevelopment() && jwtSecret.StartsWith("DEV-ONLY", StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Refusing to start outside Development with the built-in development JWT secret. Set Jwt__Secret.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep claim names exactly as issued ("sub", and the long role URI) instead of remapping them.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            NameClaimType = "sub",
            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };
    });
builder.Services.AddAuthorization();

// ---------------------------------------------------------------- CORS
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:4200" };
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyMethod()
        .AllowAnyHeader());
});

// ---------------------------------------------------------------- Rate limiting for login / register / refresh
var authPermitLimit = builder.Configuration.GetValue("AuthRateLimit:PermitLimit", 10);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authPermitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

// The API sits behind the nginx container in Docker, so trust X-Forwarded-* for the real client IP.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// ---------------------------------------------------------------- Application services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<ILedgerService, LedgerService>();
builder.Services.AddScoped<IComplianceService, ComplianceService>();
builder.Services.AddScoped<IPaymentGatewayService, PaymentGatewayService>();
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IInterestService, InterestService>();

builder.Services.AddHostedService<DatabaseMigrationService>();
builder.Services.AddHostedService<InterestAccrualWorker>();

// ---------------------------------------------------------------- Architecture pattern library
// Reference implementations of common system-design patterns (see README). They are registered
// so they can be injected, but the core banking request path does not depend on them.
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

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseCors("AllowAngular");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Liveness: the process is up. Readiness: the database answers.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapHealthChecks("/health/ready");

app.Run();
