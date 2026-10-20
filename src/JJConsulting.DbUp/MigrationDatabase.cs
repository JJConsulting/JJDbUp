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

/// <summary>Owns SQL Server access, upgrade engines, and migration journals.</summary>
internal sealed class MigrationDatabase(string connectionString, DbUpOptions options, IUpgradeLog log)
{
    public void EnsureExists()
    {
        if (options.EnsureDatabase)
            EnsureDatabase.For.SqlDatabase(connectionString, log);
    }

    public void MarkAsExecuted(IReadOnlyCollection<SqlScript> scripts)
    {
        if (scripts.Count == 0)
            return;

        var result = CreateMigrationEngine(scripts).MarkAsExecuted();
        if (!result.Successful)
            throw new DatabaseMigrationException("Migration adoption", result.ErrorScript?.Name, result.Error);
    }

    public bool ApplyMigrations(IReadOnlyCollection<SqlScript> scripts)
    {
        var engine = CreateMigrationEngine(scripts);
        var hasPendingMigrations = engine.IsUpgradeRequired();
        UpgradeOrThrow(engine, "Migrations");
        return hasPendingMigrations;
    }

    public void ApplyProgrammableObjects(IReadOnlyCollection<SqlScript> scripts)
    {
        UpgradeOrThrow(CreateProgrammableObjectEngine(scripts), "Programmable Objects");
    }

    public DatabaseMigrationStatus GetStatus(MigrationScripts scripts)
    {
        if (!DatabaseExists())
        {
            return new DatabaseMigrationStatus
            {
                DatabaseExists =  false,
                PendingMigrations = scripts.Migrations.Select(s=>s.Name).ToArray(),
                PendingObjects = scripts.ProgrammableObjects.Select(s=>s.Name).ToArray()
            };
        }
      

        var pendingMigrations = CreateMigrationEngine(scripts.Migrations).GetScriptsToExecute();
        // Use DbUpX's own hash comparison when no schema migration forces a full refresh.
        string[] pendingObjects;
        if (pendingMigrations.Count > 0)
        {
            pendingObjects = scripts.ProgrammableObjects.Select(s => s.Name).ToArray();
        }
        else
        {
            pendingObjects = CreateProgrammableObjectEngine(scripts.ProgrammableObjects)
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

    public void ClearProgrammableObjectJournal()
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

    private bool DatabaseExists()
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

    private UpgradeEngine CreateMigrationEngine(IEnumerable<SqlScript> scripts) =>
        CreateBuilder(scripts).JournalToSqlTable(options.Schema, options.MigrationJournal).Build();

    private UpgradeEngine CreateProgrammableObjectEngine(IEnumerable<SqlScript> scripts)
    {
        return CreateBuilder(scripts)
            .WithScriptSorter(items => items)
            .JournalToSqlWithHashing(schemaName: options.Schema, tableName: options.ProgrammableObjectJournal)
            .Build();
    }

    private UpgradeEngineBuilder CreateBuilder(IEnumerable<SqlScript> scripts)
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
