# JJConsulting.DbUp.Migrations

A .NET 10 library that runs embedded SQL Server scripts with DbUp. It handles versioned migrations and programmable objects (functions, views, and procedures).

## Use it

Reference `src/JJConsulting.DbUp/JJConsulting.DbUp.csproj`. Put the scripts in the consuming project:

```text
Order.Api/
├── Database/
│   ├── Migrations/
│   │   ├── V001_CreateOrder.sql
│   │   └── V002_AddStatus.sql
│   └── ProgrammableObjects/
│       ├── Functions/fn_OrderTotal.sql
│       ├── Views/vw_Order.sql
│       └── Procedures/sp_GetOrder.sql
└── Order.Api.csproj
```

Embed them in `Order.Api.csproj`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Database/Migrations/**/*.sql" />
  <EmbeddedResource Include="Database/ProgrammableObjects/**/*.sql" />
</ItemGroup>
```

The resource names must start with `<BaseNamespace>.Migrations.` or `<BaseNamespace>.ProgrammableObjects.` and end with `.sql`. For example, `Order.Api.Database.Migrations.V001_CreateOrder.sql` uses `Order.Api.Database` as its base namespace. Rebuild the application after changing SQL files.

Set the connection and migration options in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=OrderDb;Integrated Security=True;TrustServerCertificate=True"
  },
  "DatabaseMigrations": {
    "BaseNamespace": "Order.Api.Database",
    "EnsureDatabase": true,
    "ScriptsToMarkAsExecuted": []
  }
}
```

Bind `DbUpOptions` through `IOptions` and migrate before the app starts serving requests:

```csharp
using JJConsulting.DbUp.Migrations;
using JJConsulting.DbUp.Migrations.Configuration;
using Microsoft.Extensions.Options;

builder.Services.AddDbUpMigrations(builder.Configuration);
var app = builder.Build();

var options = app.Services.GetRequiredService<IOptions<DbUpOptions>>().Value;
var connectionString = app.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string is missing.");
DatabaseMigrator.Migrate(connectionString, typeof(Program).Assembly, options);
app.Run();
```

If the scripts live in a separate project, pass that project's assembly instead of `typeof(Program).Assembly`.

You can also call `DatabaseMigrator.Migrate(connectionString, assembly, baseNamespace)` without configuration binding. `DbUpOptions` additionally supports `Schema`, `MigrationJournal`, `ProgrammableObjectJournal`, and `DependencyDirective`.

DbUp verifies the configured `Schema` before executing scripts. Before baseline adoption, the library explicitly calls DbUp's `VerifySchema()` and then `MarkAsExecuted()`, without executing the baseline SQL. `EnsureDatabase` controls database creation only. The connection user must have permission to create the schema when it does not exist.

## Example scripts

`Database/Migrations/V001_CreateOrder.sql`:

```sql
CREATE TABLE dbo.[Order] (
    Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Total decimal(18,2) NOT NULL
);
```

`Database/Migrations/V002_AddStatus.sql`:

```sql
ALTER TABLE dbo.[Order]
ADD Status nvarchar(30) NOT NULL
    CONSTRAINT DF_Order_Status DEFAULT N'Pending';
```

`Database/ProgrammableObjects/Functions/fn_OrderTotal.sql`:

```sql
CREATE OR ALTER FUNCTION dbo.fn_OrderTotal(@OrderId int)
RETURNS decimal(18,2)
AS
BEGIN
    RETURN (SELECT Total FROM dbo.[Order] WHERE Id = @OrderId);
END;
```

`Database/ProgrammableObjects/Views/vw_Order.sql`:

```sql
-- #requires fn_OrderTotal
CREATE OR ALTER VIEW dbo.vw_Order
AS SELECT Id, Total, Status, dbo.fn_OrderTotal(Id) AS CurrentTotal
FROM dbo.[Order];
```

`Database/ProgrammableObjects/Procedures/sp_GetOrder.sql`:

```sql
-- #requires vw_Order
CREATE OR ALTER PROCEDURE dbo.sp_GetOrder @OrderId int
AS
BEGIN
    SELECT Id, Total, Status FROM dbo.vw_Order WHERE Id = @OrderId;
END;
```

`#requires` is the default dependency directive. `-- #requires fn_OrderTotal` makes DbUpX run the function before the view; `-- #requires vw_Order` runs the view before the procedure. Use the dependency script's filename without `.sql`. For multiple dependencies, separate names with commas.

## Execution order

1. Create the database when `EnsureDatabase` is `true`.
2. Record any `ScriptsToMarkAsExecuted` without running them. Use this only for migrations already applied to the target database.
3. Run pending migrations once, tracked by name in `dbo.SchemaVersions` by default.
4. When migrations ran, clear the programmable object journal, then run objects in dependency order. Their hashes are tracked in `dbo.ProgrammableObjectVersions` by default.

The two upgrade stages use separate transactions. A programmable object failure does not roll back completed migrations. Script failures raise `DatabaseMigrationException` with the stage and script name when available.

Add new numbered migration files for later schema changes; applied migrations are not rerun when edited. Programmable objects should be safe to run again. If an existing database already has a migration's changes, list its filename in `ScriptsToMarkAsExecuted` to record it without executing it.
