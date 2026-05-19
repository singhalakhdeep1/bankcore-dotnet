# .NET Angular Banking App

Full-stack banking application built with .NET 8 (C#) backend and Angular 17 frontend.

## Features

### Backend (.NET 8 Web API)

- **Authentication**: JWT-based authentication with BCrypt password hashing
- **Database**: PostgreSQL with Entity Framework Core
- **Caching**: Redis for session management
- **Architecture**: Clean architecture with Services, Controllers, DTOs
- **API Documentation**: Swagger/OpenAPI
- **Logging**: Serilog structured logging
- **Error Handling**: Global exception handling middleware
- **Security**: CORS, authentication middleware

### Frontend (Angular 17)

- **UI Framework**: Angular Material Design
- **State Management**: RxJS observables
- **Authentication**: JWT token management with HTTP interceptors
- **Routing**: Protected routes with Auth Guards
- **Forms**: Reactive forms with validation
- **HTTP Client**: Typed API services

## Tech Stack

### Backend

- .NET 8 (C#)
- ASP.NET Core Web API
- Entity Framework Core 8
- PostgreSQL 16
- Redis 7
- JWT Authentication
- BCrypt.Net
- Serilog
- Swagger/OpenAPI

### Frontend

- Angular 17
- TypeScript 5.3
- Angular Material 17
- RxJS 7.8
- Angular CLI

## Project Structure

```
06-dotnet-angular-banking/
├── backend/
│   ├── Controllers/         # API endpoints
│   ├── Services/           # Business logic
│   ├── Models/             # Entity models
│   ├── DTOs/               # Data transfer objects
│   ├── Data/               # DbContext
│   ├── Middleware/         # Custom middleware
│   ├── Program.cs          # Application entry point
│   ├── appsettings.json    # Configuration
│   └── BankingAPI.csproj   # Project file
├── frontend/
│   ├── src/
│   │   ├── app/
│   │   │   ├── components/    # Angular components
│   │   │   ├── services/      # HTTP services
│   │   │   ├── guards/        # Route guards
│   │   │   ├── interceptors/  # HTTP interceptors
│   │   │   ├── models/        # TypeScript interfaces
│   │   │   └── app.module.ts
│   │   └── environments/      # Environment configs
│   ├── angular.json
│   ├── package.json
│   └── tsconfig.json
└── docker-compose.yml
```

## Getting Started

### Prerequisites

- .NET 8 SDK
- Node.js 20+
- PostgreSQL 16
- Redis (optional, for development)

### Backend Setup

1. Navigate to backend directory:

```bash
cd PRODUCTION-PROJECTS/06-dotnet-angular-banking/backend
```

2. Restore dependencies:

```bash
dotnet restore
```

3. Update database connection string in `appsettings.json`

4. Run database migrations:

```bash
dotnet ef migrations add InitialCreate
dotnet ef database update
```

5. Run the API:

```bash
dotnet run
```

API will be available at: `http://localhost:5000`
Swagger UI: `http://localhost:5000/swagger`

### Frontend Setup

1. Navigate to frontend directory:

```bash
cd PRODUCTION-PROJECTS/06-dotnet-angular-banking/frontend
```

2. Install dependencies:

```bash
npm install
# or
pnpm install
```

3. Start development server:

```bash
npm start
# or
pnpm start
```

Frontend will be available at: `http://localhost:4200`

### Docker Setup

Run the entire stack with Docker Compose:

```bash
cd PRODUCTION-PROJECTS/06-dotnet-angular-banking
docker-compose up -d
```

Services:

- Frontend: http://localhost:4200
- Backend API: http://localhost:5000
- PostgreSQL: localhost:5432
- Redis: localhost:6379

## API Endpoints

### Authentication

- `POST /api/auth/register` - Register new user
- `POST /api/auth/login` - User login
- `POST /api/auth/logout` - User logout

### User

- `GET /api/user/profile` - Get current user profile

### Accounts

- `GET /api/accounts` - Get user accounts
- `GET /api/accounts/{id}` - Get account by ID
- `POST /api/accounts` - Create new account

## Database Models

### User

- Id (Guid)
- Email (unique)
- Password (hashed)
- FirstName, LastName
- Phone, Address, City, State, Country, PostalCode
- Role (Customer, Staff, Manager, Admin)
- KycStatus (Pending, Verified, Rejected)
- IsActive, IsLocked, TwoFactorEnabled

### Account

- Id (Guid)
- UserId (foreign key)
- AccountNumber (unique)
- AccountType (Savings, Checking, Business, FixedDeposit)
- Balance
- Currency
- Status (Active, Inactive, Frozen, Closed)
- OverdraftLimit, InterestRate

### Transaction

- Id (Guid)
- AccountId (foreign key)
- Type (Deposit, Withdrawal, Transfer, Payment, Refund, Interest, Fee, Reversal)
- Amount, Currency
- BalanceBefore, BalanceAfter
- Status (Pending, Completed, Failed, Reversed)
- Description, ReferenceNumber
- ToAccountNumber, FromAccountNumber

## Environment Variables

### Backend (appsettings.json)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=banking_db;Username=postgres;Password=postgres",
    "Redis": "localhost:6379"
  },
  "Jwt": {
    "Secret": "YourSecretKey",
    "Issuer": "BankingAPI",
    "Audience": "BankingClient",
    "ExpiryMinutes": 1440
  }
}
```

### Frontend (environment.ts)

```typescript
export const environment = {
  production: false,
  apiUrl: "http://localhost:5000/api",
};
```

## Development

### Backend Commands

```bash
# Restore packages
dotnet restore

# Build
dotnet build

# Run
dotnet run

# Watch mode
dotnet watch run

# Create migration
dotnet ef migrations add MigrationName

# Update database
dotnet ef database update
```

### Frontend Commands

```bash
# Install dependencies
npm install

# Development server
npm start

# Build for production
npm run build

# Run tests
npm test

# Lint code
npm run lint
```

## Security Features

- JWT token-based authentication
- BCrypt password hashing
- CORS configuration
- SQL injection protection (EF Core parameterized queries)
- XSS protection (Angular sanitization)
- HTTPS support
- Authentication middleware
- Role-based authorization

## Testing

### Backend Tests

```bash
cd backend
dotnet test
```

### Frontend Tests

```bash
cd frontend
npm test
```

## Production Deployment

### Backend

```bash
dotnet publish -c Release -o ./publish
```

### Frontend

```bash
npm run build
# Output in dist/banking-angular-app
```

## License

MIT

## Author

Created as part of the PRODUCTION-PROJECTS collection
