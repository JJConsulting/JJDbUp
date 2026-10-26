# JJConsulting.DbUp

A .NET 10 library that runs embedded SQL Server scripts with DbUp. It handles versioned migrations and programmable objects (functions, views, and procedures).

## Installation

Create a separate .NET 10 console app with DI:

```sh
dotnet new console -n MyApp.Migrations -f net10.0
```

Add the packages to `MyApp.Migrations.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="JJConsulting.DbUp" Version="2.0.2" />
  <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
</ItemGroup>
```

## Embedded SQL scripts

Keep the runner and SQL scripts together:

```text
MyApp.Migrations/
├── Database/
│   ├── Migrations/
│   │   ├── V001_CreateMyApp.sql
│   │   └── V002_AddStatus.sql
│   └── ProgrammableObjects/
│       ├── Functions/fn_MyAppTotal.sql
│       ├── Views/vw_MyApp.sql
│       └── Procedures/sp_GetMyApp.sql
├── MyApp.Migrations.csproj
└── Program.cs
```

Embed the SQL scripts in `MyApp.Migrations.csproj`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Database/Migrations/**/*.sql" />
  <EmbeddedResource Include="Database/ProgrammableObjects/**/*.sql" />
</ItemGroup>
```

The resource names must start with `<BaseNamespace>.Migrations.` or `<BaseNamespace>.ProgrammableObjects.` and end with `.sql`. For example, `MyApp.Migrations.Database.Migrations.V001_CreateMyApp.sql` uses `MyApp.Migrations.Database` as its base namespace. Rebuild after changing SQL files.

## Configuration and execution

In `MyApp.Migrations/Program.cs`:

```csharp
using JJConsulting.DbUp.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbUpMigrations();
builder.Services.Configure<DbUpOptions>(options =>
{
    options.BaseNamespace = "MyApp.Migrations.Database";
    options.EnsureDatabase = false;
});

using var host = builder.Build();
host.Services.MigrateDatabase();
```

The host loads configuration from JSON and environment variables. Set `DatabaseMigrations__ConnectionString`, then run:

```sh
dotnet run --project MyApp.Migrations --configuration Release
```

The database must already exist with `EnsureDatabase = false`. Set it to `true` to create it.

## CI/CD

GitHub Actions: run migrations before deploying the application.

```yaml
name: Database migrations
on:
  workflow_dispatch:
  push:
    branches: [main]

concurrency:
  group: production-database
  cancel-in-progress: false

jobs:
  migrate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Apply migrations
        env:
          DatabaseMigrations__ConnectionString: ${{ secrets.DATABASE_CONNECTION_STRING }}
        run: dotnet run --project MyApp.Migrations --configuration Release
      # Add application deployment steps here.
```

The runner needs database access. A migration failure stops the job. Run one migration job per database at a time.

### Options

| Property | Default | Purpose |
| --- | --- | --- |
| `ConnectionString` | Empty string | Required SQL Server connection string. |
| `BaseNamespace` | Empty string | Required prefix before `.Migrations.` and `.ProgrammableObjects.` in embedded resource names. |
| `Schema` | `dbo` | Schema used by DbUp and its journal tables. Use qualified object names in SQL scripts when targeting a specific schema. |
| `MigrationJournal` | `SchemaVersions` | Table that records executed migration resource names. |
| `ProgrammableObjectJournal` | `ProgrammableObjectVersions` | Table that tracks programmable objects by hash. |
| `DependencyDirective` | `#requires` | Directive used to MyApp programmable objects by dependency. |
| `EnsureDatabase` | `true` | Create the database if it does not exist. |
| `ScriptsToMarkAsExecuted` | `null` | Optional migration names to record without executing SQL. Omitted, `null`, and empty selections mark no scripts; pending migrations run normally. |

DbUp verifies the configured `Schema` before executing scripts. Before baseline adoption, the library explicitly calls DbUp's `VerifySchema()` and then `MarkAsExecuted()`, without executing the baseline SQL. `EnsureDatabase` controls database creation only. The connection user must have permission to create the schema when it does not exist.

## Example scripts

`Database/Migrations/V001_CreateMyApp.sql`:

```sql
CREATE TABLE dbo.[MyApp] (
    Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Total decimal(18,2) NOT NULL
);
```

`Database/Migrations/V002_AddStatus.sql`:

```sql
ALTER TABLE dbo.[MyApp]
ADD Status nvarchar(30) NOT NULL
    CONSTRAINT DF_MyApp_Status DEFAULT N'Pending';
```

`Database/ProgrammableObjects/Functions/fn_MyAppTotal.sql`:

```sql
CREATE OR ALTER FUNCTION dbo.fn_MyAppTotal(@MyAppId int)
RETURNS decimal(18,2)
AS
BEGIN
    RETURN (SELECT Total FROM dbo.[MyApp] WHERE Id = @MyAppId);
END;
```

`Database/ProgrammableObjects/Views/vw_MyApp.sql`:

```sql
-- #requires fn_MyAppTotal
CREATE OR ALTER VIEW dbo.vw_MyApp
AS SELECT Id, Total, Status, dbo.fn_MyAppTotal(Id) AS CurrentTotal
FROM dbo.[MyApp];
```

`Database/ProgrammableObjects/Procedures/sp_GetMyApp.sql`:

```sql
-- #requires vw_MyApp
CREATE OR ALTER PROCEDURE dbo.sp_GetMyApp @MyAppId int
AS
BEGIN
    SELECT Id, Total, Status FROM dbo.vw_MyApp WHERE Id = @MyAppId;
END;
```

`#requires` is the default dependency directive. `-- #requires fn_MyAppTotal` makes DbUpX run the function before the view; `-- #requires vw_MyApp` runs the view before the procedure. Use the dependency script's filename without `.sql`. For multiple dependencies, separate names with commas.

## Execution MyApp

1. Create the database when `EnsureDatabase` is `true`.
2. Record any `ScriptsToMarkAsExecuted` without running them. Use this only for migrations already applied to the target database.
3. Run pending migrations once, tracked by name in `dbo.SchemaVersions` by default.
4. When migrations ran, clear the programmable object journal to force all objects to run again.
5. Run programmable objects in dependency MyApp. Without pending migrations, hashing selects new or changed objects. Their hashes are tracked in `dbo.ProgrammableObjectVersions` by default.

The two upgrade stages use separate transactions. A programmable object failure does not roll back completed migrations. Script failures raise `DatabaseMigrationException` with the stage and script name when available.

Add new numbered migration files for later schema changes; applied migrations are not rerun when edited. Use zero-padded filenames such as `V001`, `V002`, and `V010` to preserve filename MyApping. Programmable objects should be safe to run again.

## Adopting an existing database

If an existing database already has a migration's changes, add its filename to `DatabaseMigrations:ScriptsToMarkAsExecuted` to record it without executing it. For example, add this property inside the `DatabaseMigrations` section:

```json
"ScriptsToMarkAsExecuted": [
  "V001_CreateMyApp.sql",
  "V002_AddStatus.sql"
]
```

Names are relative to `<BaseNamespace>.Migrations.`, include `.sql`, and must match the embedded resource casing exactly. For a resource in a subfolder, use the dotted relative name, for example `Initial.V001_CreateMyApp.sql`. Missing or invalid names cause an error before database creation or script execution. Other pending migrations still run normally.

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
