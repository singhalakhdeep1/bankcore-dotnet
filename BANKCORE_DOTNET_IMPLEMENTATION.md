# BankCore .NET - Database Implementation

## Overview
This document describes the database integrations added to the bankcore-dotnet project.

## Technologies Added

### 1. MySQL
- MySQL Connector/NET
- ADO.NET data access
- Transaction support

### 2. MongoDB
- MongoDB C# Driver
- Document operations
- Index management
- Aggregation pipelines

### 3. RavenDB
- RavenDB Client
- Document session
- Index creation
- Bulk insert

### 4. SQL Server
- Microsoft.Data.SqlClient
- Stored procedures
- Transaction support
- Data reader

## Files Created

### Database Services
- `backend/Services/MySQLService.cs` - MySQL database service
- `backend/Services/MongoDBService.cs` - MongoDB service
- `backend/Services/RavenDBService.cs` - RavenDB service
- `backend/Services/SQLServerService.cs` - SQL Server service

## Features

### MySQL Service
- Execute queries with parameters
- Execute non-query commands
- Execute scalar queries
- Connection testing
- Transaction support

### MongoDB Service
- Collection access
- Find operations (single and multiple)
- Insert operations (single and bulk)
- Update operations
- Delete operations
- Count operations
- Index creation
- Aggregation pipelines
- Connection testing

### RavenDB Service
- Document session management
- Load operations (single and multiple)
- Store operations
- Delete operations
- Query operations
- Index creation
- Bulk insert
- Connection testing

### SQL Server Service
- Execute queries with parameters
- Execute non-query commands
- Execute scalar queries
- Data reader operations
- Stored procedure execution
- Transaction support
- Connection testing

## Installation Required

Add to `backend/BankingAPI.csproj`:
```xml
<ItemGroup>
  <PackageReference Include="MySql.Data" Version="9.0.0" />
  <PackageReference Include="MongoDB.Driver" Version="3.0.0" />
  <PackageReference Include="RavenDB.Client" Version="6.0.0" />
  <PackageReference Include="Microsoft.Data.SqlClient" Version="5.2.0" />
</ItemGroup>
```

Run:
```bash
dotnet restore
```

## Configuration

Add to `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "MySQL": "Server=localhost;Database=bankcore;User=root;Password=password;",
    "MongoDB": "mongodb://localhost:27017",
    "SQLServer": "Server=localhost;Database=bankcore;User Id=sa;Password=YourPassword123;TrustServerCertificate=True;"
  },
  "RavenDB": {
    "Urls": ["http://localhost:8080"],
    "Database": "bankcore"
  }
}
```

## Status

- The four packages above are installed in `backend/BankingAPI.csproj` and the services compile.
- The services are registered in `Program.cs` **only when their connection string is configured**, so the API runs on PostgreSQL alone.
- How each one is used:
  - **MongoDB**: every audit-log entry is mirrored to the `audit_logs` collection, and each generated regulatory report is stored as a document in `regulatory_reports`. *Tested against a real MongoDB.*
  - **MySQL** and **SQL Server**: each generated regulatory report adds a summary row to a `regulatory_reports` table (created on first use). *Not tested: no instance was available.*
  - **RavenDB**: KYC document metadata is stored as `kycdocuments/{id}` documents; the database is created on first use. *Not tested: no instance was available.*
- `GET /api/admin/system/databases` reports connectivity of all four.
- `docker compose --profile integrations up` starts containers for them (see `docker-compose.yml` and `.env.example`).
- Fixed while wiring them in: `MySQLService.BeginTransactionAsync` opened a transaction and discarded it (now `ExecuteInTransactionAsync`), and `MongoDBService.AggregateAsync` discarded its result (now returns it).
