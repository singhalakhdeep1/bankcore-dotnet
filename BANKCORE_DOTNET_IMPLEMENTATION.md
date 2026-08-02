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

## Notes
- All code is written but packages are not installed (per user request)
- Requires running instances of MySQL, MongoDB, RavenDB, and SQL Server
- Services should be registered in Program.cs for dependency injection
