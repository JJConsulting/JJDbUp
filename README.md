# JJConsulting.DbUp

A .NET 10 library for SQL Server migrations and versionable objects: functions, views, and stored procedures.
It uses DbUp to execute scripts and track migrations, and DbUpX to detect object changes by hash and resolve
script dependencies.

## Main advantage: versionable objects

**Keep one SQL file per function, view, or procedure, and edit that file whenever the object changes.** The library
tracks the script's content hash and applies new or changed objects on the next migration run. You do not need a new
numbered migration for every procedure or view change.

For example, keep the current definition of `sp_GetOrder` in `ProgrammableObjects/Procedures/sp_GetOrder.sql`. To change
the procedure, edit that file, rebuild, and deploy. Its history stays in Git, and the database receives the updated
definition when `DatabaseMigrator.Migrate` runs. Use `CREATE OR ALTER` so the script can be applied again.

The code and configuration call these **programmable objects** (`ProgrammableObjects`). They are the versionable objects
managed by this library.

| Script type           | What belongs here                                        | How it runs                                                                   |
|-----------------------|----------------------------------------------------------|-------------------------------------------------------------------------------|
| `Migrations`          | Table changes, indexes, constraints, and data migrations | Once per script name. Add a new numbered file for each change.                |
| `ProgrammableObjects` | Functions, views, and stored procedures                  | When new or when the script's content hash changes. Update the existing file. |

Objects can declare dependencies with `-- #requires`, so functions, views, and procedures are applied in dependency
order. When schema migrations run, the library also reapplies **all** programmable objects, even if their SQL files have
not changed. This refreshes their definitions after the schema changes.

For example:

- Change only `sp_GetOrder.sql`: the changed procedure is applied; unchanged objects are skipped.
- Add `V003_AddCustomerId.sql`: the migration runs first, then all programmable objects are reapplied in dependency
  order.
- Run again with no pending migrations or changed objects: no SQL scripts are reapplied.

## Set up your application (SQL Server)

Reference `src/JJConsulting.DbUp/JJConsulting.DbUp.csproj`. Put the scripts in the consuming
project:

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
    <EmbeddedResource Include="Database/Migrations/**/*.sql"/>
    <EmbeddedResource Include="Database/ProgrammableObjects/**/*.sql"/>
</ItemGroup>
```

The resource names must start with `<BaseNamespace>.Migrations.` or `<BaseNamespace>.ProgrammableObjects.` and end with
`.sql`. For example, `Order.Api.Database.Migrations.V001_CreateOrder.sql` uses `Order.Api.Database` as its base
namespace. Rebuild the application after changing SQL files.

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
using JJConsulting.DbUp;
using JJConsulting.DbUp.Configuration;
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

You can also call `DatabaseMigrator.Migrate(connectionString, assembly, baseNamespace)` without configuration binding.
`DbUpOptions` additionally supports `Schema`, `MigrationJournal`, `ProgrammableObjectJournal`, and
`DependencyDirective`.

You can also pass `IEnumerable<DbUp.Engine.SqlScript>` instead of an assembly to
`DatabaseMigrator.Migrate(connectionString, scripts, options)`. Script names follow the same configured prefixes.

## SQL scripts and dependencies

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

`#requires` is the default dependency directive. `-- #requires fn_OrderTotal` makes DbUpX run the function before the
view; `-- #requires vw_Order` runs the view before the procedure. Use the dependency script's filename without `.sql`.
For multiple dependencies, separate names with commas.

## Execution order

1. Create the database when `EnsureDatabase` is `true`.
2. Record any `ScriptsToMarkAsExecuted` without running them. Use this only for migrations already applied to the target
   database.
3. Run pending migrations once, tracked by name in `dbo.SchemaVersions` by default.
4. When migrations ran, clear the programmable object journal, then run objects in dependency order. Their hashes are
   tracked in `dbo.ProgrammableObjectVersions` by default.

The two upgrade stages use separate transactions. A programmable object failure does not roll back completed migrations.
After a schema change, persisted object hashes are cleared before the object stage so a failed run can retry all
objects. Script failures raise `DatabaseMigrationException` with the stage and script name when available.

Never edit an already applied numbered migration to introduce a schema change: add a new migration instead. Edit the
existing programmable object file when its definition changes, and make it safe to run again with `CREATE OR ALTER`.

For an existing database, `ScriptsToMarkAsExecuted` can record migrations that have already been applied outside this
library. List their filenames relative to the `Migrations` folder, including `.sql` (for example,
`V001_CreateOrder.sql`). These scripts are recorded without executing their SQL; other pending migrations still run.
Only mark migrations whose changes already exist in the target database.