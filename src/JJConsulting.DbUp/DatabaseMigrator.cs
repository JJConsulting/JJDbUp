using System.Reflection;
using DbUp;
using DbUp.Builder;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.SqlServer;
using DbUpX;
using JJConsulting.DbUp.Configuration;
using JJConsulting.DbUp.Exceptions;
using Microsoft.Data.SqlClient;

namespace JJConsulting.DbUp;

public static class DatabaseMigrator
{
    /// <summary>Loads embedded scripts and applies them using the configured namespace.</summary>
    public static void Migrate(string connectionString, Assembly scriptsAssembly, string baseNamespace)
    {
        Migrate(connectionString, scriptsAssembly, new DbUpOptions { BaseNamespace = baseNamespace });
    }

    /// <summary>Loads, validates and applies embedded scripts.</summary>
    public static void Migrate(string connectionString, Assembly scriptsAssembly, DbUpOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scriptsAssembly);
        ArgumentNullException.ThrowIfNull(options);
        Migrate(connectionString, RequireScripts(MigrationScriptService.GetScripts(scriptsAssembly, options)));
    }

    /// <summary>Validates and applies explicitly supplied scripts.</summary>
    public static void Migrate(string connectionString, IEnumerable<SqlScript> scripts, DbUpOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(options);
        Migrate(connectionString, RequireScripts(MigrationScriptService.GetScripts(scripts, options)));
    }

    private static MigrationScripts RequireScripts(MigrationScriptResult result)
    {
        if (!result.IsValid)
            throw new ArgumentException(result.Error, "options");
        return result.Scripts;
    }

    /// <summary>Applies a previously validated set of scripts without repeating discovery or validation.</summary>
    public static void Migrate(string connectionString, MigrationScripts scripts, IUpgradeLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripts);
        log ??= new ConsoleUpgradeLog();

        EnsureExists(connectionString, scripts.Options, log);
        // Marks only the selected scripts as executed; the others remain pending.
        MarkAsExecuted(connectionString, scripts.Options, log, scripts.ScriptsToMark);
        var hasPendingMigrations = ApplyMigrations(connectionString, scripts.Options, log, scripts.Migrations);

        // Clear the persisted hashes before the object transaction. If that stage fails,
        // the next run must still refresh every object after the committed schema change.
        if (hasPendingMigrations)
            ClearProgrammableObjectJournal(connectionString, scripts.Options, log);

        ApplyProgrammableObjects(connectionString, scripts.Options, log, scripts.ProgrammableObjects);
    }

    /// <summary>Reads journals without creating a database or changing any persisted state.</summary>
    public static DatabaseMigrationStatus GetStatus(string connectionString, MigrationScripts scripts, IUpgradeLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripts);
        log ??= new ConsoleUpgradeLog();
        if (!DatabaseExists(connectionString))
        {
            return new DatabaseMigrationStatus
            {
                DatabaseExists = false,
                PendingMigrations = scripts.Migrations.Select(s => s.Name).ToArray(),
                PendingObjects = scripts.ProgrammableObjects.Select(s => s.Name).ToArray()
            };
        }

        var pendingMigrations = CreateMigrationEngine(connectionString, scripts.Options, log, scripts.Migrations).GetScriptsToExecute();
        // Use DbUpX's own hash comparison when no schema migration forces a full refresh.
        string[] pendingObjects;
        if (pendingMigrations.Count > 0)
        {
            pendingObjects = scripts.ProgrammableObjects.Select(s => s.Name).ToArray();
        }
        else
        {
            pendingObjects = CreateProgrammableObjectEngine(connectionString, scripts.Options, log, scripts.ProgrammableObjects)
                .GetScriptsToExecute()
                .Select(s => NameWithHash.TryParse(s.Name, out var name) ? name.PlainName : s.Name)
                .ToArray();
        }

        return new DatabaseMigrationStatus
        {
            DatabaseExists = true,
            PendingMigrations = pendingMigrations.Select(s => s.Name).ToArray(),
            PendingObjects = pendingObjects
        };
    }

    /// <summary>Records the prepared selection as executed; never executes SQL scripts.</summary>
    public static void MarkAsExecuted(string connectionString, MigrationScripts scripts, IUpgradeLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripts);
        if (scripts.ScriptsToMark.Count == 0)
            return;
        log ??= new ConsoleUpgradeLog();
        EnsureExists(connectionString, scripts.Options, log);
        MarkAsExecuted(connectionString, scripts.Options, log, scripts.ScriptsToMark);
    }

    private static void EnsureExists(string connectionString, DbUpOptions options, IUpgradeLog log)
    {
        if (options.EnsureDatabase)
            EnsureDatabase.For.SqlDatabase(connectionString, log);
    }

    private static void MarkAsExecuted(string connectionString, DbUpOptions options, IUpgradeLog log, IReadOnlyCollection<SqlScript> scripts)
    {
        if (scripts.Count == 0)
            return;

        var result = CreateMigrationEngine(connectionString, options, log, scripts).MarkAsExecuted();
        if (!result.Successful)
            throw new DatabaseMigrationException("Migration adoption", result.ErrorScript?.Name, result.Error);
    }

    private static bool ApplyMigrations(string connectionString, DbUpOptions options, IUpgradeLog log, IReadOnlyCollection<SqlScript> scripts)
    {
        var engine = CreateMigrationEngine(connectionString, options, log, scripts);
        /*
         * Check for pending migrations before PerformUpgrade.
         *
         * If any migrations are pending, force all programmable
         * objects to run again after the migrations complete.
         */
        var hasPendingMigrations = engine.IsUpgradeRequired();
        UpgradeOrThrow(engine, "Migrations");
        return hasPendingMigrations;
    }

    private static void ApplyProgrammableObjects(string connectionString, DbUpOptions options, IUpgradeLog log, IReadOnlyCollection<SqlScript> scripts)
    {
        UpgradeOrThrow(CreateProgrammableObjectEngine(connectionString, options, log, scripts), "Programmable Objects");
    }

    private static void ClearProgrammableObjectJournal(string connectionString, DbUpOptions options, IUpgradeLog log)
    {
        var manager = new SqlConnectionManager(connectionString);
        using var operation = manager.OperationStarting(log, []);
        manager.ExecuteCommandsWithManagedConnection(factory =>
        {
            var parser = new SqlServerObjectParser();
            var table = parser.QuoteIdentifier(options.Schema) + "." + parser.QuoteIdentifier(options.ProgrammableObjectJournal);
            using var command = factory();
            command.CommandText = $"IF OBJECT_ID(@table, N'U') IS NOT NULL DELETE FROM {table};";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@table";
            parameter.Value = table;
            command.Parameters.Add(parameter);
            command.ExecuteNonQuery();
        });
    }

    private static bool DatabaseExists(string connectionString)
    {
        var settings = new SqlConnectionStringBuilder(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.InitialCatalog, "InitialCatalog");
        // A failed probe must not cache a login failure if a later migration creates the database.
        settings.Pooling = false;
        try
        {
            // Contained users can access the target database without being able to log in to master.
            using var target = new SqlConnection(settings.ConnectionString);
            target.Open();
            return true;
        }
        catch (SqlException exception) when (exception.Number == 4060)
        {
            var databaseName = settings.InitialCatalog;
            settings.InitialCatalog = "master";
            using var connection = new SqlConnection(settings.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DB_ID(@name), HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW ANY DATABASE')";
            command.Parameters.AddWithValue("@name", databaseName);
            using var reader = command.ExecuteReader();
            reader.Read();
            if (reader.IsDBNull(0) && !reader.IsDBNull(1) && reader.GetInt32(1) == 1)
                return false;

            // Only report a missing database when visible metadata confirms its absence.
            throw;
        }
    }

    private static UpgradeEngine CreateMigrationEngine(string connectionString, DbUpOptions options, IUpgradeLog log, IEnumerable<SqlScript> scripts) =>
        CreateBuilder(connectionString, log, scripts).JournalToSqlTable(options.Schema, options.MigrationJournal).Build();

    private static UpgradeEngine CreateProgrammableObjectEngine(string connectionString, DbUpOptions options, IUpgradeLog log, IEnumerable<SqlScript> scripts)
    {
        return CreateBuilder(connectionString, log, scripts)
            .WithScriptSorter(items => items)
            .JournalToSqlWithHashing(schemaName: options.Schema, tableName: options.ProgrammableObjectJournal)
            .Build();
    }

    private static UpgradeEngineBuilder CreateBuilder(string connectionString, IUpgradeLog log, IEnumerable<SqlScript> scripts)
    {
        return DeployChanges.To.SqlDatabase(connectionString).WithScripts(scripts).LogTo(log).WithTransaction();
    }

    private static void UpgradeOrThrow(UpgradeEngine engine, string stage)
    {
        var result = engine.PerformUpgrade();
        if (!result.Successful)
            throw new DatabaseMigrationException(stage, result.ErrorScript?.Name, result.Error);
    }
}
