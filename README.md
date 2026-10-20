# JJConsulting.DbUp

SQL Server migrations for .NET 10, powered by DbUp and DbUpX. Use the `jjdbup` CLI locally or in CI before deploying your application.

## Scripts

```text
Database/
├── Migrations/
│   ├── V001_CreateOrder.sql
│   └── V002_AddStatus.sql
└── ProgrammableObjects/
    ├── Functions/fn_OrderTotal.sql
    ├── Views/vw_Order.sql
    └── Procedures/sp_GetOrder.sql
```

| Folder | Behavior |
|---|---|
| `Migrations` | Runs once per script name. Add a new numbered file for each change. |
| `ProgrammableObjects` | Runs when new or changed by content hash. Edit the existing file; use `CREATE OR ALTER`. |

Migrations run first. When migrations run, **all programmable objects are reapplied**. Otherwise, unchanged objects are skipped.
Declare object dependencies with `-- #requires fn_OrderTotal, vw_Order`, using filenames without `.sql`.

## CLI

Install as a local .NET tool and commit `.config/dotnet-tools.json`:

```sh
dotnet new tool-manifest
dotnet tool install JJConsulting.DbUp.Cli --version 1.0.0
dotnet tool run jjdbup --help
```

For a global installation: `dotnet tool install --global JJConsulting.DbUp.Cli`.
The commands below use `jjdbup`; for a local tool, prefix them with `dotnet tool run`.

Configure `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=OrderDb;Integrated Security=True;TrustServerCertificate=True"
  },
  "DatabaseMigrations": {
    "BaseNamespace": "Order.Api.Database",
    "ScriptsPath": "Database",
    "EnsureDatabase": true
  }
}
```

`ScriptsPath` is the only added property in `DbUpOptions`; it points to the scripts directory.
Use `--assembly Order.Api.dll` to read compiled embedded resources instead. Select a connection with `--connection-name` (default: `DefaultConnection`).
`BaseNamespace` retains its existing behavior: script names must start with `<BaseNamespace>.Migrations.` or `<BaseNamespace>.ProgrammableObjects.`.
Folder scripts use that same prefix. Keep it unchanged to preserve migration history.

| Command | Purpose |
|---|---|
| `jjdbup new V003_AddStatus` | Create a migration file without overwriting existing files. |
| `jjdbup new Views/vw_Order --type object` | Create a programmable object file. |
| `jjdbup validate` | Check script names and dependencies offline; does not validate SQL syntax. |
| `jjdbup status` | List pending scripts without changing the database. |
| `jjdbup migrate` | Apply pending migrations and changed objects. |
| `jjdbup mark-executed --script V001_CreateOrder.sql` | Record an already applied migration without executing SQL. Repeat `--script` for multiple files. |

Configuration priority: **arguments → environment → JSON → defaults**. Use `--config <file>` to select another JSON file.
JSON paths are relative to the file; argument and environment paths are relative to the working directory.
Use environment variables such as `ConnectionStrings__DefaultConnection`, `DatabaseMigrations__ScriptsPath`, and `DatabaseMigrations__BaseNamespace`.
`EnsureDatabase` defaults to `true`; use `--ensure-database false` for an existing database. See `<command> --help` for all options.

`--output json` emits one result on stdout; diagnostics go to stderr.
Exit codes: **0** success, **1** operational failure, **2** invalid input, **3** pending changes with `status --check`.

## CI

After checkout and .NET 10 setup, restore the pinned tool and run migrations before deploying:

```yaml
- name: Restore migration tool
  run: dotnet tool restore
- name: Apply migrations
  env:
    ConnectionStrings__DefaultConnection: ${{ secrets.DATABASE_CONNECTION_STRING }}
  run: dotnet tool run jjdbup migrate --scripts ./Database --base-namespace Order.Api.Database --ensure-database false --output json
```

Provision the database first when using `--ensure-database false`. Run one migration job per database at a time.
Remove the application's startup migration call when moving execution to CI.

## Library API

Install `JJConsulting.DbUp`. The library reads the same options as the CLI:

```csharp
using JJConsulting.DbUp;
using JJConsulting.DbUp.Configuration;

var options = new DbUpOptions { BaseNamespace = "Order.Api.Database", ScriptsPath = "Database" };
var preparation = MigrationScriptService.GetScripts(options);
if (!preparation.IsValid)
{
    Console.Error.WriteLine(preparation.Error);
    return;
}

DatabaseMigrator.Migrate(connectionString, preparation.Scripts);
```

For embedded resources, configure their build action:

```xml
<ItemGroup>
  <EmbeddedResource Include="Database/Migrations/**/*.sql" />
  <EmbeddedResource Include="Database/ProgrammableObjects/**/*.sql" />
</ItemGroup>
```

Prepare them with `MigrationScriptService.GetScripts(typeof(Program).Assembly, options)`; this ignores `ScriptsPath`.
`MigrationScriptService.GetScripts(scripts, options)` also accepts `IEnumerable<SqlScript>`.
Preparation returns either validated scripts or an error, without connecting to SQL Server. File access and assembly loading errors can still throw.

Pass the prepared scripts to `DatabaseMigrator.Migrate`, `GetStatus`, or `MarkAsExecuted`, optionally with a custom logger.
The same prepared scripts can be reused: discovery, validation, dependency ordering and adoption selection happen once, and execution uses the captured journal settings.
The CLI's `validate` command uses preparation without executing anything.
`AddDbUpMigrations(configuration)` binds the shared options.
For adoption, set `ScriptsToMarkAsExecuted` to names relative to `Migrations`, including `.sql`, before preparation.
An empty selection makes `MarkAsExecuted` a no-op; the CLI requires at least one selected migration.

Default journals: `dbo.SchemaVersions` and `dbo.ProgrammableObjectVersions`. Customize them through `Schema`, `MigrationJournal`, and `ProgrammableObjectJournal`.
The configured `Schema` is also passed to DbUp’s SQL Server executor, which creates it natively if missing before executing pending scripts. Adoption verifies the same schema through DbUp before writing journal entries, because `MarkAsExecuted` skips the normal upgrade verification. `EnsureDatabase` controls only database creation. `GetStatus` does not create schemas or journals.
Migrations and objects use separate transactions: an object failure does not roll back completed migrations. Script failures raise `DatabaseMigrationException`.
