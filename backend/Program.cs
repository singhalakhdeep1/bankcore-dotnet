using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using BankCore.Services.CapacityEstimation;
using BankCore.Services.Caching;
using BankCore.Services.ConnectionPooling;
using BankCore.Services.ErrorHandling;
using BankCore.Services.Idempotency;
using BankCore.Services.Indexing;
using BankCore.Services.Logging;
using BankCore.Services.Patterns;
using BankCore.Services.RateLimiting;
using BankCore.Services.ReadReplica;
using BankCore.Services.Repositories;
using BankCore.Services.Saga;
using BankCore.Services.Sharding;
using BankCore.Services.SystemConstraints;
using BankCore.Services.UnitOfWork;
using BankingAPI.Data;
using BankingAPI.DTOs;
using BankingAPI.Middleware;
using BankingAPI.Services;
using BankingAPI.Services.Caching;
using BankingAPI.Services.Hosting;
using BankingAPI.Services.Security;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- logging (console + rolling file)
builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: context.Configuration["Logging:FilePath"] ?? "logs/bankcore-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}"));

// ---------------------------------------------------------------- MVC, validation, Swagger
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Enums travel as readable strings ("Staff", "Ach") in both directions.
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "BankCore API",
        Version = "v1",
        Description = "Retail banking API: accounts, transfers, payments, ledger, compliance and back-office."
    });

    var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml)) options.IncludeXmlComments(xml, includeControllerXmlComments: true);

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT access token. Paste the token only; the 'Bearer ' prefix is added for you.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "API key for server-to-server calls to /api/admin endpoints.",
        Name = ApiKeyDefaults.HeaderName,
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

// ---------------------------------------------------------------- database (primary + optional read replica)
var primaryConnection = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(primaryConnection));

var replicaConnection = builder.Configuration.GetConnectionString("ReadReplica");
builder.Services.AddDbContext<ReadDbContext>(options =>
    options.UseNpgsql(string.IsNullOrWhiteSpace(replicaConnection) ? primaryConnection : replicaConnection));

builder.Services.AddHealthChecks().AddDbContextCheck<ApplicationDbContext>("database");

// ---------------------------------------------------------------- cache (Redis, or in-memory when no Redis is configured)
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddDistributedMemoryCache();
}
else
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        // Fail fast: a slow or missing Redis must not stall every request for the default 5 seconds.
        var redis = StackExchange.Redis.ConfigurationOptions.Parse(redisConnection);
        redis.ConnectTimeout = 1500;
        redis.SyncTimeout = 1000;
        redis.AsyncTimeout = 1000;
        redis.AbortOnConnectFail = false;
        options.ConfigurationOptions = redis;
    });
}

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();

// ---------------------------------------------------------------- authentication and authorization
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
        // Keep claim names exactly as issued ("sub", "jti", and the long role URI) instead of remapping them.
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
            RoleClaimType = ClaimTypes.Role
        };

        // Logout and refresh rotation revoke token ids; reject a revoked access token immediately.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var jti = context.Principal?.FindFirst("jti")?.Value;
                var store = context.HttpContext.RequestServices.GetRequiredService<ITokenStore>();
                if (jti != null && await store.IsRevokedAsync(jti))
                {
                    context.Fail("The token has been revoked.");
                }
            }
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, _ => { });

// API keys act with their owner's role, but are held to two extra rules:
//  - a key without the "write" scope may only make read requests (GET/HEAD);
//  - actions that mint credentials or change roles/sanctions need an interactive (JWT) administrator.
static bool ApiKeyMayProceed(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context)
{
    if (!context.User.HasClaim("auth_method", "api_key")) return true;
    if (context.User.HasClaim("scope", "write")) return true;

    var method = (context.Resource as HttpContext)?.Request.Method ?? string.Empty;
    return HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Staff", p => p.RequireRole("Staff", "Manager", "Admin").RequireAssertion(ApiKeyMayProceed));
    options.AddPolicy("Manager", p => p.RequireRole("Manager", "Admin").RequireAssertion(ApiKeyMayProceed));
    options.AddPolicy("Admin", p => p.RequireRole("Admin").RequireAssertion(ApiKeyMayProceed));
    options.AddPolicy("InteractiveAdmin", p => p
        .RequireRole("Admin")
        .RequireAssertion(context => !context.User.HasClaim("auth_method", "api_key")));
});

// ---------------------------------------------------------------- CORS
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:4200" };
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .WithExposedHeaders("X-Correlation-Id", "Idempotent-Replayed", "X-RateLimit-Remaining"));
});

// ---------------------------------------------------------------- strict per-IP limit on login / register / refresh
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

// ---------------------------------------------------------------- application services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<ILedgerService, LedgerService>();
builder.Services.AddScoped<IComplianceService, ComplianceService>();
builder.Services.AddScoped<IPaymentGatewayService, PaymentGatewayService>();
builder.Services.AddScoped<IPaymentMethodService, PaymentMethodService>();
builder.Services.AddScoped<IRecurringPaymentService, RecurringPaymentService>();
builder.Services.AddScoped<IBankTransferService, BankTransferService>();
builder.Services.AddScoped<IChargebackService, ChargebackService>();
builder.Services.AddScoped<IPaymentAnalyticsService, PaymentAnalyticsService>();
builder.Services.AddScoped<IKycService, KycService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IInterestService, InterestService>();
builder.Services.AddScoped<ISanctionsService, SanctionsService>();
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
builder.Services.AddScoped<IAccountCache, AccountCache>();
builder.Services.AddSingleton<IAuditService, AuditService>();
builder.Services.AddSingleton<ITokenStore, TokenStore>();
builder.Services.AddSingleton<ITwoFactorService, TwoFactorService>();
builder.Services.AddSingleton<IReportingMirror, ReportingMirror>();

builder.Services.AddHostedService<DatabaseMigrationService>();
builder.Services.AddHostedService<InterestAccrualWorker>();
builder.Services.AddHostedService<RecurringPaymentWorker>();
builder.Services.AddHostedService<BankTransferSettlementWorker>();

// ---------------------------------------------------------------- optional database integrations
// Each one is only registered when its connection string is configured; everything that uses them
// resolves them optionally, so the API runs fine on PostgreSQL alone.
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("MongoDB")))
    builder.Services.AddSingleton<MongoDBService>();
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("MySQL")))
    builder.Services.AddSingleton<MySQLService>();
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("SQLServer")))
    builder.Services.AddSingleton<SQLServerService>();
if ((builder.Configuration.GetSection("RavenDB:Urls").Get<string[]>() ?? Array.Empty<string>()).Any(u => !string.IsNullOrWhiteSpace(u)))
    builder.Services.AddSingleton<RavenDBService>();

// ---------------------------------------------------------------- architecture pattern library (HLD/LLD)
// Saga (payments), strategy + factory (Stripe/PayPal), resilience (retry/circuit breaker/timeout around gateways),
// distributed cache (balances, history), idempotency + token-bucket rate limiting (HTTP pipeline),
// structured logging (correlation ids), read replica (history queries), sharding, repository + unit of work,
// capacity estimation and constraints analysis (admin diagnostics).
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
builder.Services.AddSingleton<StripePaymentStrategy>();
builder.Services.AddSingleton<PayPalPaymentStrategy>();
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

// Say plainly which payment gateways are real and which are simulated.
{
    var factory = app.Services.GetRequiredService<PaymentStrategyFactory>();
    foreach (var strategy in factory.All())
    {
        app.Logger.LogInformation("Payment gateway {Provider}: {Mode}", strategy.ProviderName, strategy.IsLive ? "LIVE" : "simulated (no credentials configured)");
    }
}

app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (builder.Configuration.GetValue("Security:RequireHttps", false))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseCors("AllowAngular");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TokenBucketRateLimitMiddleware>();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapControllers();

// Liveness: the process is up. Readiness: the database answers.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapHealthChecks("/health/ready");

app.Run();
