# BankCore: .NET + Angular Banking Platform

A full-stack retail banking system: customers open accounts, move money, pay merchants and top up by card; staff run KYC, compliance, ledger and payment operations from a back-office console.

- **Backend:** ASP.NET Core 8 Web API, Entity Framework Core 8, PostgreSQL 16, Redis, JWT, Serilog, Polly, FluentValidation
- **Frontend:** Angular 17 + Angular Material
- **Optional integrations:** MongoDB, MySQL, SQL Server, RavenDB, live Stripe and PayPal
- **Ops:** Docker Compose, GitHub Actions CI

The design documents are [EXPANSION-COMPLETE.md](EXPANSION-COMPLETE.md) (feature spec), [HLD_LLD_IMPLEMENTATION.md](HLD_LLD_IMPLEMENTATION.md) (architecture patterns) and [BANKCORE_DOTNET_IMPLEMENTATION.md](BANKCORE_DOTNET_IMPLEMENTATION.md) (database integrations). The code implements them; the table below says where.

## What it does

| Area | Details |
|---|---|
| **Auth** | BCrypt passwords with a policy (10+ chars, upper, lower, digit, symbol); 15-minute JWT access tokens and rotating 7-day refresh tokens; logout revokes tokens immediately; **TOTP two-factor** (Google Authenticator and similar); **account lockout** (5 failures = 15 minutes); per-IP rate limit on auth routes |
| **Roles** | Customer, Staff, Manager, Admin with policy-based access. Staff: read, review, report. Manager: freeze accounts, lock users, resolve disputes, close periods, return transfers. Admin: roles, sanctions list, API keys. Optional IP allow-list for `/api/admin` |
| **API keys** | Server-to-server access to the back-office API with `X-Api-Key`. Only a hash is stored. Keys without the `write` scope are read-only, and cannot mint keys or change roles |
| **Accounts** | Checking, Savings, Business, Fixed Deposit in any currency; soft-delete on close |
| **Money movement** | Deposit, withdraw, transfer (by account number), merchant payment, refunds, reversals, fees, daily interest on savings |
| **Double-entry ledger** | Every posting writes a customer ledger entry and balanced general-ledger entries. **Trial balance, balance sheet, period closing**, and per-account reconciliation |
| **Compliance** | Pre-transaction gate: KYC status and limits, **sanctions (OFAC-style) screening** with fuzzy name matching, risk hold. Monitoring: large transaction, **CTR** (cash > 10,000/day), **structuring**, high frequency, unusual amount, many IPs. Alerts with a review workflow. **SAR, CTR and monthly compliance reports** |
| **KYC** | Document upload (content-sniffed PDF/JPEG/PNG, hashed, stored outside the web root), staff review queue, status drives limits (pending 2,500/day, verified 10,000/day, rejected blocked) |
| **Payments** | Stripe and PayPal behind a strategy + factory; **tokenized payment methods** (no card numbers stored); **card top-up** and refunds run as sagas with compensation; **recurring payments**; **chargebacks** (customer dispute, staff resolution); analytics and ledger reconciliation |
| **Bank transfers** | **ACH, domestic wire and SWIFT** with ABA-checksum, IBAN mod-97 and BIC validation, fees, sanctions screening, settlement job and returns |
| **Operations** | Audit log (PostgreSQL, mirrored to MongoDB), correlation ids, structured request logs (console + rolling file), security headers, health checks, startup migrations, seeded admin |
| **Resilience** | Idempotency-Key header, token-bucket rate limiting, Polly retry/circuit-breaker/timeout around gateways, optimistic concurrency on balances, Redis cache with version-based invalidation, read-replica context for history queries |

### How money is kept correct

- **Signed amounts**: credits are positive, debits negative, everywhere.
- **Atomic postings**: the transaction row, balance change, ledger entry and GL entries commit in one database transaction. A transfer's two legs commit together or not at all.
- **Optimistic concurrency**: `Account` carries PostgreSQL's `xmin` as a concurrency token. Racing requests: one wins, the other gets `409` and retries. No lost updates, no overdrafts.
- **Idempotency**: transfers accept an `idempotencyKey`; any state-changing request accepts an `Idempotency-Key` header and replays the first response.
- **Balanced books**: every journal has a counter-entry (cash, transfer/payments/external clearing, processor receivable, fee income, interest expense), so total debits equal total credits. `GET /api/admin/ledger/trial-balance` proves it.

### What is simulated or unverified

- **Payment gateways run in simulated mode until you configure credentials.** Without `Stripe__SecretKey` / `PayPal__ClientId` the strategies behave deterministically (test cards below) and say `mode: simulated` in responses and logs. The live Stripe (`Stripe.net`) and PayPal REST code paths exist but **have not been exercised against the real services**.
- **MySQL, SQL Server and RavenDB integrations are unverified.** They compile and are wired in (report mirrors and KYC metadata), but no instance of them was available to test against. **PostgreSQL and MongoDB were tested for real.**
- **Docker images were not built** in the environment this was developed in (no Docker daemon); the compose file is unexercised. The API, migrations and every endpoint were run directly against PostgreSQL 18.
- **The Angular screens were compiled (strict templates) but not clicked through in a browser.**
- Deposits are simulated cash deposits (there is no bank-to-bank funding), and ACH/wire/SWIFT settlement is simulated by a job; no real external bank is contacted.

## Quick start (Docker)

```bash
cp .env.example .env     # set POSTGRES_PASSWORD, JWT_SECRET, ADMIN_EMAIL, ADMIN_PASSWORD
docker compose up --build
```

| Service | URL |
|---|---|
| App (Angular behind nginx, proxies `/api`) | http://localhost:4200 |
| API | http://localhost:5000 (Swagger at `/swagger` when `ASPNETCORE_ENVIRONMENT=Development`) |

Register in the app, open an account, deposit, then try a transfer, a card top-up (Cards page first) and a bank transfer. Sign in with `ADMIN_EMAIL` to reach the **Back office**.

Optional databases: set the `*_URL` values in `.env` (see the comments there) and run `docker compose --profile integrations up --build`.

### Test cards (simulated gateway)

| Card | Behaviour |
|---|---|
| 4242 4242 4242 4242 | charges succeed |
| 4000 0000 0000 0002 | declined when charged |
| 4000 0000 0000 9995 | insufficient funds when charged |

Anything else that passes the Luhn check works like the first. Sanctions demo names: *Test Sanctioned Person*, *Blocked Example Trading Ltd*, *Demo Watchlist Holdings*.
Bank-transfer test values: routing `021000021`, IBAN `DE89370400440532013000`, BIC `DEUTDEFF`.

## Local development

Requirements: .NET 8 SDK, Node 20+, PostgreSQL 16 (Redis and the other databases are optional).

```bash
# Backend (http://localhost:5000)
cd backend
export ConnectionStrings__DefaultConnection="Host=localhost;Database=banking_db;Username=postgres;Password=..."
export Seed__AdminEmail=admin@example.com Seed__AdminPassword='Change-me-Admin-1!'
dotnet run

# Frontend (http://localhost:4200), another terminal
cd frontend && npm ci && npm start
```

Migrations are applied at startup (`Database:AutoMigrate`). Without a Redis connection string the API uses an in-memory cache. A Redis outage does not fail requests but makes them slower while connections time out (the revocation check fails open for access tokens and closed for refresh tokens).

New migration: `dotnet ef migrations add <Name> --context ApplicationDbContext --output-dir Migrations`.

The development JWT secret is prefixed `DEV-ONLY`; the API refuses to start outside Development while it is in use. Set `Jwt__Secret` (32+ chars) for anything real.

## API overview

Everything is documented in Swagger (XML comments included). Main groups:

| Route | Purpose |
|---|---|
| `/api/auth/*` | register, login (TOTP), refresh, logout, `2fa/setup\|enable\|disable` |
| `/api/user/*` | profile, change password, close profile, KYC documents |
| `/api/accounts/*` | accounts, transactions, ledger, compliance report, deposit, withdraw, transfer, close |
| `/api/payments/*` | merchant payment, **card top-up**, refunds, **chargebacks** |
| `/api/payment-methods`, `/api/recurring-payments`, `/api/bank-transfers` | tokenized methods, standing payments, ACH/wire/SWIFT |
| `/api/admin/users`, `/accounts`, `/compliance`, `/ledger`, `/payments`, `/system` | back office: roles, KYC queue, alerts, sanctions, reports, trial balance, periods, analytics, reconciliation, disputes, returns, audit, API keys, capacity, constraints, shards, database status |
| `/health`, `/health/ready` | liveness, readiness |

Errors are JSON `{ "message": "..." }` with `400`, `401`, `403`, `404`, `409` (concurrent update, retry), `422` (reused idempotency key), `423` (locked out) or `429`.

## Configuration

Environment variables use `__` for nesting (`Jwt__Secret`).

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | local Postgres | primary database |
| `ConnectionStrings:ReadReplica` | primary | read-only context for history queries |
| `ConnectionStrings:Redis` | `localhost:6379` | cache; empty = in-memory |
| `ConnectionStrings:MongoDB` / `MySQL` / `SQLServer`, `RavenDB:Urls` | empty (off) | optional integrations |
| `Jwt:Secret`, `Jwt:ExpiryMinutes`, `Jwt:RefreshTokenExpiryDays` | dev value, 15, 7 | tokens |
| `Security:MaxFailedLogins`, `Security:LockoutMinutes` | 5, 15 | lockout |
| `Security:AdminAllowedIps` | `[]` | IPs/CIDRs allowed on `/api/admin` (empty = any) |
| `Security:RequireHttps` | false | HTTPS redirect + HSTS |
| `Stripe:SecretKey`, `Stripe:PublishableKey`, `PayPal:ClientId`, `PayPal:ClientSecret`, `PayPal:Mode` | empty | live gateways |
| `Payments:DefaultProvider` | Stripe | provider used when none is named |
| `Workers:Enabled`, `Workers:RecurringIntervalSeconds`, `Workers:SettlementIntervalSeconds` | true, 3600, 300 | background jobs |
| `Seed:AdminEmail`, `Seed:AdminPassword`, `Seed:SampleSanctions` | empty, empty, true | startup seed |
| `Storage:KycPath` | `App_Data/kyc` | where KYC files are stored |
| `AuthRateLimit:PermitLimit`, `RateLimit:*` | 10/min per IP; bucket 100, 1/s | rate limits |

## Project structure

```
backend/
  Controllers/        Auth, User, Accounts, Payments, PaymentMethods, RecurringPayments, BankTransfers, Admin*
  Services/           Auth, Account, Transaction, Ledger, Compliance, PaymentGateway (sagas), BankTransfer,
                      Recurring, Chargeback, Kyc, Admin, Interest, Reporting mirror
  Services/Security/  password policy, TOTP, token store, audit, sanctions, API keys
  Services/Hosting/   startup migration + seed, interest / recurring / settlement workers
  Services/<pattern>/ HLD/LLD pattern library (saga, caching, idempotency, rate limiting, strategy, ...)
  Services/{Mongo,MySQL,SQLServer,RavenDB}Service.cs   database integrations
  Models/ Data/ Migrations/ DTOs/ Middleware/ Extensions/
  tests/              pre-existing xUnit project (excluded from the API build; not run by CI)
frontend/src/app/     dashboard, accounts, account-detail (+ top-up, bank transfer, payments), profile,
                      payment methods, recurring, admin console, auth + role guards, interceptors
```

### Where the architecture patterns are used

| Pattern (HLD/LLD doc) | Used for |
|---|---|
| Saga | card top-up, merchant payment and top-up refund, each with compensating steps |
| Strategy + Factory | Stripe / PayPal providers behind `IPaymentStrategy` |
| Error handling (Polly) | retry, circuit breaker, timeout around gateway calls |
| Distributed cache | account lists and transaction history, invalidated by version bumps |
| Idempotency | `Idempotency-Key` middleware |
| Rate limiting | token-bucket middleware per user/IP |
| Structured logging | correlation ids on every request |
| Read replica | transaction history through `ReadDbContext` |
| Sharding, capacity estimation, constraints analysis | `/api/admin/system/shards`, `/capacity`, `/constraints` |
| Repository / Unit of Work, connection pooling, indexing | implemented and registered; the EF-based services use `DbContext` directly (see the pattern doc) |

## Known limitations

- Account balances are in each account's own currency; there is no FX conversion (transfers need matching currencies).
- Webhooks from Stripe/PayPal (asynchronous disputes and refunds) are not implemented; disputes are opened by customers and resolved by staff.
- Sharding routes are informational: data is not actually partitioned across databases.
- The frontend has no unit tests.
