# BankCore: .NET + Angular Banking App

A full-stack retail banking app. Customers register, open accounts, deposit, withdraw, transfer money and pay merchants. Every balance change is recorded in a double-entry style ledger.

- **Backend:** ASP.NET Core 8 Web API, Entity Framework Core 8, PostgreSQL 16, JWT auth, Serilog, Polly
- **Frontend:** Angular 17 + Angular Material
- **Ops:** Docker Compose (PostgreSQL, Redis, API, nginx), GitHub Actions CI

## What works

| Area | Details |
|---|---|
| Auth | Register / login with BCrypt-hashed passwords, 15-minute JWT access tokens, 7-day refresh tokens with automatic refresh in the Angular client, per-IP rate limiting on `/api/auth/*` |
| Accounts | Checking, Savings, Business and Fixed Deposit accounts in any 3-letter currency; each user only ever sees their own accounts |
| Money movement | Deposit, withdraw, transfer (by account number), merchant payment, partial or full refund |
| Ledger | Every movement writes a ledger entry; `GET /api/accounts/{id}/ledger` reports whether the stored balance still matches the ledger |
| Interest | Savings accounts earn 4.5% a year, credited daily by a background worker (once per account per UTC day) |
| Compliance monitoring | Rule-based alerts: large transaction, high frequency, unusual amount; `GET /api/accounts/{id}/compliance` returns a score |
| Operations | `/health` (liveness), `/health/ready` (database), structured logs, migrations applied on startup |

### How money is kept correct

- **Signed amounts.** `Transaction.Amount` is positive for credits and negative for debits everywhere.
- **Atomic postings.** The transaction row, the balance change and the ledger entry commit in one database transaction. A transfer's two legs commit together or not at all.
- **Optimistic concurrency.** `Account` carries PostgreSQL's `xmin` as a concurrency token. If two requests race on one balance, one wins and the other gets `409 Conflict` and retries; there are no lost updates and no overdrafts.
- **Idempotent transfers.** Send an `idempotencyKey` and a repeated request returns the original transfer instead of moving money twice (keys are scoped per user).
- **Limits.** 10,000 per account per UTC day across withdrawals, payments and outgoing transfers; amounts must be positive with at most 2 decimals; transfers need both accounts active and in the same currency; overdraft is allowed only up to the account's `OverdraftLimit`.
- **Ownership.** Every account, ledger, compliance and payment endpoint checks that the caller owns the account.

### What is simulated

- **The payment gateway.** `PaymentGatewayService` approves payments after a short delay instead of calling Stripe or PayPal. Set `Payments:SimulatedDeclineRate` (0-1) to make it decline at random. Debits, refunds, ledger entries and audit logs are real.
- **Deposits.** There is no bank-to-bank funding; a deposit is a simulated cash deposit that any account owner can make. This is how you add money to try the app.
- **KYC.** `KycStatus` is stored (always `Pending`) but not enforced.

## Quick start (Docker)

```bash
cp .env.example .env        # then edit POSTGRES_PASSWORD and JWT_SECRET
docker compose up --build
```

| Service | URL |
|---|---|
| App (Angular behind nginx, proxies `/api`) | http://localhost:4200 |
| API | http://localhost:5000 |
| Swagger UI | http://localhost:5000/swagger (only when `ASPNETCORE_ENVIRONMENT=Development`) |

Open the app, register, open an account, make a deposit, then open a second account and transfer between them. To transfer to another person, give them your account number (shown on the account page).

## Local development

Requirements: .NET 8 SDK, Node 20+, PostgreSQL 16 (Redis is optional).

```bash
# Backend (http://localhost:5000 with the default launch profile)
cd backend
# point at your database, e.g. with an environment variable:
#   ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=banking_db;Username=postgres;Password=..."
dotnet run

# Frontend (http://localhost:4200), in another terminal
cd frontend
npm ci
npm start
```

The database schema is created automatically on first start (`Database:AutoMigrate`, default `true`). Without a Redis connection string the API falls back to an in-memory distributed cache.

New migration after changing a model:

```bash
cd backend
dotnet tool install --global dotnet-ef --version 8.0.11   # once
dotnet ef migrations add <Name> --output-dir Migrations
```

The development JWT secret in `appsettings.json` is prefixed `DEV-ONLY`, and the API **refuses to start outside Development** while it is still in use. Set `Jwt__Secret` (32+ characters) for any real deployment.

## API

All routes except `/api/auth/*` and the health endpoints require `Authorization: Bearer <accessToken>`.

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/auth/register` | Create a user; returns tokens |
| POST | `/api/auth/login` | Log in; returns tokens |
| POST | `/api/auth/refresh` | Exchange a refresh token for new tokens |
| POST | `/api/auth/logout` | Stateless; the client discards its tokens |
| GET | `/api/user/profile` | Current user |
| GET / POST | `/api/accounts` | List own accounts / open an account |
| GET | `/api/accounts/{id}` | One account |
| GET | `/api/accounts/{id}/transactions?page=&pageSize=` | Paged history (newest first) |
| GET | `/api/accounts/{id}/ledger` | Ledger entries and the reconciliation flag |
| GET | `/api/accounts/{id}/compliance?days=` | Compliance report |
| POST | `/api/accounts/{id}/deposit` | `{ amount, description? }` |
| POST | `/api/accounts/{id}/withdraw` | `{ amount, description? }` |
| POST | `/api/accounts/transfer` | `{ fromAccountId, toAccountNumber \| toAccountId, amount, description?, idempotencyKey? }` |
| POST | `/api/payments` | `{ accountId, amount, merchantName, description? }` |
| GET | `/api/payments?accountId=` | Payment history for an account |
| GET | `/api/payments/{transactionId}` | Payment status |
| POST | `/api/payments/{transactionId}/refund` | `{ amount? }` (omit for a full refund) |
| GET | `/health`, `/health/ready` | Liveness, readiness |

Errors are JSON `{ "message": "..." }` with `400` (rejected request), `401`, `404` (also used for accounts you do not own), `409` (concurrent update, retry) or `429` (rate limited).

## Configuration

Set any of these as environment variables using `__` for nesting (for example `Jwt__Secret`).

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | local Postgres | PostgreSQL connection string |
| `ConnectionStrings:Redis` | `localhost:6379` | Distributed cache; empty uses an in-memory cache |
| `Jwt:Secret` | dev-only value | Signing key, 32+ bytes |
| `Jwt:ExpiryMinutes` / `Jwt:RefreshTokenExpiryDays` | 15 / 7 | Token lifetimes |
| `Cors:AllowedOrigins` | `["http://localhost:4200"]` | Needed only when a browser calls the API cross-origin |
| `AuthRateLimit:PermitLimit` | 10 | Auth requests per minute per IP |
| `Database:AutoMigrate` | true | Apply migrations on startup |
| `Interest:Enabled` | true | Daily interest worker |
| `Payments:SimulatedDeclineRate` | 0 | Share of simulated payments the fake gateway declines |

## Project structure

```
backend/
  Controllers/      Auth, User, Accounts, Payments
  Services/         Auth, Account, Transaction, Ledger, Compliance, Interest, PaymentGateway, Jwt
  Services/Hosting/ Startup migration + daily interest worker
  Models/ Data/     Entities and the EF Core DbContext
  DTOs/ Middleware/ Request/response shapes, error handling
  Migrations/       EF Core migrations (PostgreSQL)
  tests/            Pre-existing xUnit project (excluded from the API build; not run by CI)
frontend/src/app/
  components/       login, register, dashboard, accounts, account-detail, navbar
  services/ interceptors/ guards/ models/
```

### Architecture pattern library

`backend/Services/{Saga,Sharding,ReadReplica,Caching,Repositories,UnitOfWork,ConnectionPooling,Indexing,RateLimiting,Idempotency,Logging,ErrorHandling,Patterns,CapacityEstimation,SystemConstraints}` are reference implementations of common system-design patterns, described in [HLD_LLD_IMPLEMENTATION.md](HLD_LLD_IMPLEMENTATION.md). They are registered in dependency injection but **the banking request path does not use them**. Some methods (for example cache pattern invalidation and statistics) are placeholders, and sharding and read-replica routing are not connected to the EF Core context. Treat them as study material, not production features.

## Known limitations

- Two-factor auth, account lockout, KYC checks and role-based back-office endpoints are not implemented (the `User` model has fields for them).
- Tokens are stateless, so logout does not revoke them before they expire.
- The daily limit is a fixed constant (`TransactionService.DailyDebitLimit`).
- The frontend has no unit tests.
