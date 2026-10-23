# JJConsulting.DbUp

A .NET 10 library that runs embedded SQL Server scripts with DbUp. It handles versioned migrations and programmable objects (functions, views, and procedures).

## Installation

The library targets .NET 10 and supports SQL Server. Add a project reference to `src/JJConsulting.DbUp/JJConsulting.DbUp.csproj` when consuming this repository locally. The NuGet package ID is `JJConsulting.DbUp`.

## Embedded SQL scripts

Put the scripts in the consuming project:

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

## Configuration and execution

Set the connection and migration options in the `DatabaseMigrations` section of `appsettings.json`:

```json
{
  "DatabaseMigrations": {
    "ConnectionString": "Server=localhost;Database=OrderDb;Integrated Security=True;TrustServerCertificate=True",
    "BaseNamespace": "Order.Api.Database",
    "EnsureDatabase": true
  }
}
```

In an ASP.NET Core application, register the migrator and run it before the app starts serving requests:

```csharp
using JJConsulting.DbUp;
using JJConsulting.DbUp.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbUpMigrations();

var app = builder.Build();

app.Services.GetRequiredService<DatabaseMigrator>()
    .Migrate(typeof(Program).Assembly);

app.Run();
```

`AddDbUpMigrations()` registers `DatabaseMigrator` as a singleton and binds `DbUpOptions` using the host's `IConfiguration`. It takes no configuration argument. The connection string is read from `DatabaseMigrations:ConnectionString`; the library does not resolve `ConnectionStrings:DefaultConnection` automatically. With the host's default environment variable provider, use `DatabaseMigrations__ConnectionString` to supply it outside the JSON file.

The instance overload `Migrate()` uses `Assembly.GetCallingAssembly()` and the injected `DbUpOptions`. Call it directly from the assembly containing the embedded scripts:

```csharp
app.Services.GetRequiredService<DatabaseMigrator>().Migrate();
```

Use `Migrate(Assembly)` to select the script assembly explicitly. If the scripts live in a separate project, use a type from that project, for example `.Migrate(typeof(DatabaseScriptsMarker).Assembly)`.

`ScriptsToMarkAsExecuted` is optional. Omit it when all pending migrations should execute normally. The migrator treats `null` and an empty array as an empty selection, so no `PostConfigure` callback is needed.

For a console application or another caller without dependency injection, pass `DbUpOptions` directly:

```csharp
using JJConsulting.DbUp;
using JJConsulting.DbUp.Configuration;

var options = new DbUpOptions
{
    ConnectionString = Environment.GetEnvironmentVariable("DatabaseMigrations__ConnectionString")
        ?? throw new InvalidOperationException("Connection string is missing."),
    BaseNamespace = "Order.Api.Database"
};

DatabaseMigrator.Migrate(typeof(Program).Assembly, options);
```

### Options

| Property | Default | Purpose |
| --- | --- | --- |
| `ConnectionString` | Empty string | Required SQL Server connection string. |
| `BaseNamespace` | Empty string | Required prefix before `.Migrations.` and `.ProgrammableObjects.` in embedded resource names. |
| `Schema` | `dbo` | Schema used by DbUp and its journal tables. Use qualified object names in SQL scripts when targeting a specific schema. |
| `MigrationJournal` | `SchemaVersions` | Table that records executed migration resource names. |
| `ProgrammableObjectJournal` | `ProgrammableObjectVersions` | Table that tracks programmable objects by hash. |
| `DependencyDirective` | `#requires` | Directive used to order programmable objects by dependency. |
| `EnsureDatabase` | `true` | Create the database if it does not exist. |
| `ScriptsToMarkAsExecuted` | `null` | Optional migration names to record without executing SQL. Omitted, `null`, and empty selections mark no scripts; pending migrations run normally. |

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
4. When migrations ran, clear the programmable object journal to force all objects to run again.
5. Run programmable objects in dependency order. Without pending migrations, hashing selects new or changed objects. Their hashes are tracked in `dbo.ProgrammableObjectVersions` by default.

The two upgrade stages use separate transactions. A programmable object failure does not roll back completed migrations. Script failures raise `DatabaseMigrationException` with the stage and script name when available.

Add new numbered migration files for later schema changes; applied migrations are not rerun when edited. Use zero-padded filenames such as `V001`, `V002`, and `V010` to preserve filename ordering. Programmable objects should be safe to run again.

## Adopting an existing database

If an existing database already has a migration's changes, add its filename to `DatabaseMigrations:ScriptsToMarkAsExecuted` to record it without executing it. For example, add this property inside the `DatabaseMigrations` section:

```json
"ScriptsToMarkAsExecuted": [
  "V001_CreateOrder.sql",
  "V002_AddStatus.sql"
]
```

Names are relative to `<BaseNamespace>.Migrations.`, include `.sql`, and must match the embedded resource casing exactly. For a resource in a subfolder, use the dotted relative name, for example `Initial.V001_CreateOrder.sql`. Missing or invalid names cause an error before database creation or script execution. Other pending migrations still run normally.

## Validation and failures

Before connecting, the migrator validates required options and checks that the supplied assembly contains at least one matching embedded SQL script. Failures reported by DbUp during adoption, migrations, or programmable object execution are wrapped in `DatabaseMigrationException`, exposing `Stage`, `ScriptName`, and `InnerException`. Validation and direct connection or journal maintenance errors can propagate separately.

## Building from source

Install a .NET 10 SDK compatible with `global.json`, then run from the repository root:

```shell
dotnet restore JJConsulting.DbUp.slnx
dotnet build JJConsulting.DbUp.slnx --configuration Release --no-restore
dotnet pack src/JJConsulting.DbUp/JJConsulting.DbUp.csproj --configuration Release --no-build --output artifacts
```

The package includes this README.
