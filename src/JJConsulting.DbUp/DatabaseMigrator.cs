using System.Reflection;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.SqlServer;
using DbUpX;
using JJConsulting.DbUp.Configuration;
using JJConsulting.DbUp.Exceptions;

namespace JJConsulting.DbUp;

public static class DatabaseMigrator
{
    public static void Migrate(string connectionString, Assembly scriptsAssembly, string baseNamespace)
    {
        Migrate(connectionString, scriptsAssembly, new DbUpOptions { BaseNamespace = baseNamespace });
    }

    public static void Migrate(string connectionString, Assembly scriptsAssembly, DbUpOptions options)
    {
        ArgumentNullException.ThrowIfNull(scriptsAssembly);
        
        ValidateOptions(options);
        var scripts = scriptsAssembly.GetManifestResourceNames()
            .Where(name => IsScript(name, options.MigrationsPrefix) || IsScript(name, options.ProgrammableObjectsPrefix))
            .Select(name =>
            {
                using var stream = scriptsAssembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return new SqlScript(name, reader.ReadToEnd());
            }).ToArray();
        Migrate(connectionString, scripts, options);
    }

    /// <summary>Runs explicitly supplied scripts with the same naming, hashing, and dependency rules as embedded scripts.</summary>
    public static void Migrate(string connectionString, IEnumerable<SqlScript> scripts, DbUpOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripts);
        
        ValidateOptions(options);
        var allScripts = scripts.ToArray();
        var migrations = allScripts.Where(s => IsScript(s.Name, options.MigrationsPrefix))
            .OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
        var objects = allScripts.Where(s => IsScript(s.Name, options.ProgrammableObjectsPrefix)).ToArray();
        if (migrations.Length == 0 && objects.Length == 0)
            throw new ArgumentException("No SQL scripts were found under the configured migration or programmable object prefixes.", nameof(options));

        var scriptsToMark = ResolveScriptsToMark(migrations, options);
        if (options.EnsureDatabase) EnsureDatabase.For.SqlDatabase(connectionString);

        if (scriptsToMark.Length > 0)
        {
            var result = Build(connectionString, options, scriptsToMark, false).MarkAsExecuted();
            if (!result.Successful)
                throw new DatabaseMigrationException("Migration adoption", result.ErrorScript?.Name, result.Error);
        }

        var migrationsUpgrader = Build(connectionString, options, migrations, false);
        var hasPendingMigrations = migrationsUpgrader.IsUpgradeRequired();
        UpgradeOrThrow(migrationsUpgrader, "Migrations");

        // Clear the persisted hashes before the object transaction. If that stage fails,
        // the next run must still refresh every object after the committed schema change.
        if (hasPendingMigrations) ClearProgrammableObjectJournal(connectionString, options);

        UpgradeOrThrow(Build(connectionString, options, objects, true), "Programmable Objects");
    }

    private static UpgradeEngine Build(string connectionString, DbUpOptions options, IEnumerable<SqlScript> scripts, bool objects)
    {
        var builder = DeployChanges.To.SqlDatabase(connectionString)
            .WithScripts(scripts)
            .LogToConsole()
            .WithTransaction();
        if (objects)
        {
            builder.WithScriptSorter(items => items.OrderByDependency(options.DependencyDirective));
            builder.JournalToSqlWithHashing(schemaName: options.Schema, tableName: options.ProgrammableObjectJournal);
        }
        else
        {
            builder.JournalToSqlTable(options.Schema, options.MigrationJournal);
        }
        return builder.Build();
    }

    private static void ClearProgrammableObjectJournal(string connectionString, DbUpOptions options)
    {
        var manager = new SqlConnectionManager(connectionString);
        using var operation = manager.OperationStarting(new ConsoleUpgradeLog(), []);
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

    private static SqlScript[] ResolveScriptsToMark(SqlScript[] migrations, DbUpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.ScriptsToMarkAsExecuted);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in options.ScriptsToMarkAsExecuted)
        {
            var fullName = options.MigrationsPrefix + name;
            if (string.IsNullOrWhiteSpace(name) || migrations.All(s => s.Name != fullName))
                throw new ArgumentException($"Migration '{name}' was not found as SQL script '{fullName}'.", nameof(options.ScriptsToMarkAsExecuted));
            selected.Add(fullName);
        }
        return migrations.Where(s => selected.Contains(s.Name)).ToArray();
    }

    private static void UpgradeOrThrow(UpgradeEngine upgrader, string stage)
    {
        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
            throw new DatabaseMigrationException(stage, result.ErrorScript?.Name, result.Error);
    }

    private static bool IsScript(string name, string prefix)
    {
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && 
               name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateOptions(DbUpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseNamespace, nameof(options.BaseNamespace));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Schema, nameof(options.Schema));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MigrationJournal, nameof(options.MigrationJournal));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProgrammableObjectJournal, nameof(options.ProgrammableObjectJournal));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DependencyDirective, nameof(options.DependencyDirective));
    }
}
