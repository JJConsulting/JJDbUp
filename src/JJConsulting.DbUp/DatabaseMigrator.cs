using DbUp;
using DbUp.Engine;
using DbUpX;
using JJConsulting.DbUp.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace JJConsulting.DbUp;

public class DatabaseMigrator (IOptions<DbUpOptions> options)
{
    public void Migrate(Assembly assembly)
    {
        Migrate(assembly, options.Value);
    }

    public void Migrate()
    {
        Migrate(Assembly.GetCallingAssembly(), options.Value);
    }

    public static void Migrate(
        Assembly scriptAssembly,
        DbUpOptions options)
    {
        ValidateOptions(scriptAssembly, options);
        ValidateScripts(scriptAssembly, options);

        var scriptsToMark = ResolveScriptsToMark(scriptAssembly, options);

        // =================================================
        // 0. ENSURE DATABASE
        // =================================================

        if (options.EnsureDatabase)
        {
            EnsureDatabase.For.SqlDatabase(options.ConnectionString);
        }

        // =================================================
        // 1. MIGRATIONS
        // =================================================

        if (scriptsToMark.Count > 0)
        {
            // MarkAsExecuted skips DbUp's normal schema verification.
            var configuration = DeployChanges.To.SqlDatabase(options.ConnectionString, options.Schema)
                .WithScripts(Array.Empty<SqlScript>()).LogToConsole().BuildConfiguration();
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, []))
            {
                configuration.ScriptExecutor.VerifySchema();
            }

            // Seleciona somente os scripts informados; os demais continuam pendentes.
            var result = BuildMigrationsUpgrader(scriptAssembly, options, scriptsToMark)
                .MarkAsExecuted();
            if (!result.Successful)
            {
                throw new DatabaseMigrationException(
                    "Migration adoption",
                    result.ErrorScript?.Name ?? string.Join(", ", scriptsToMark),
                    result.Error);
            }
        }

        var migrationsUpgrader =
            BuildMigrationsUpgrader(scriptAssembly, options);

        /*
         * Precisamos verificar antes do PerformUpgrade.
         *
         * Se existir migration pendente, depois da
         * execução vamos forçar todos os programmable
         * objects a serem executados novamente.
         */
        var hasPendingMigrations =
            migrationsUpgrader.IsUpgradeRequired();

        UpgradeOrThrow(
            migrationsUpgrader,
            "Migrations");

        // =================================================
        // 2. FORCE PROGRAMMABLE OBJECTS
        // =================================================

        if (hasPendingMigrations)
        {
            ClearProgrammableObjectJournal(
                options.ConnectionString,
                options.Schema,
                options.ProgrammableObjectJournal);
        }

        // =================================================
        // 3. PROGRAMMABLE OBJECTS
        // =================================================

        var programmableObjectsUpgrader =
            BuildProgrammableObjectsUpgrader(scriptAssembly, options);

        UpgradeOrThrow(
            programmableObjectsUpgrader,
            "Programmable Objects");
    }

    private static HashSet<string> ResolveScriptsToMark(Assembly scriptAssembly, DbUpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.ScriptsToMarkAsExecuted);

        var resources = new HashSet<string>(
            scriptAssembly.GetManifestResourceNames(), StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in options.ScriptsToMarkAsExecuted)
        {
            var resourceName = options.MigrationsPrefix + name;
            if (string.IsNullOrWhiteSpace(name)
                || !IsMigration(resourceName, options.MigrationsPrefix)
                || !resources.Contains(resourceName))
            {
                throw new ArgumentException(
                    $"Migration '{name}' was not found as embedded resource '{resourceName}'.",
                    nameof(options.ScriptsToMarkAsExecuted));
            }

            selected.Add(resourceName);
        }

        return selected;
    }

    private static void ValidateScripts(Assembly scriptAssembly, DbUpOptions options)
    {
        if (scriptAssembly.GetManifestResourceNames().Any(name =>
                IsMigration(name, options.MigrationsPrefix)
                || IsProgrammableObject(name, options.ProgrammableObjectsPrefix)))
        {
            return;
        }

        throw new ArgumentException(
            $"No embedded SQL scripts were found in assembly '{scriptAssembly.GetName().Name}' " +
            $"under '{options.MigrationsPrefix}' or '{options.ProgrammableObjectsPrefix}'.",
            nameof(options));
    }

    private static UpgradeEngine BuildMigrationsUpgrader(
        Assembly scriptAssembly,
        DbUpOptions options,
        HashSet<string>? scriptsToMark = null)
    {
        return DeployChanges.To
            .SqlDatabase(
                options.ConnectionString,
                options.Schema)
            .WithScriptsEmbeddedInAssembly(
                scriptAssembly,
                scriptName =>
                    IsMigration(
                        scriptName,
                        options.MigrationsPrefix)
                    && (scriptsToMark == null || scriptsToMark.Contains(scriptName)))
            .LogToConsole()
            .JournalToSqlTable(
                options.Schema,
                options.MigrationJournal)
            .WithTransaction()
            .Build();
    }

    private static UpgradeEngine BuildProgrammableObjectsUpgrader(
        Assembly scriptAssembly,
        DbUpOptions options)
    {
        return DeployChanges.To
            .SqlDatabase(options.ConnectionString, options.Schema)
            .WithScriptsEmbeddedInAssembly(
                scriptAssembly,
                scriptName =>
                    IsProgrammableObject(
                        scriptName,
                        options.ProgrammableObjectsPrefix))
            .WithScriptSorter(scripts =>
                scripts.OrderByDependency(
                    options.DependencyDirective))
            .LogToConsole()
            .JournalToSqlWithHashing(
                schemaName: options.Schema,
                tableName: options.ProgrammableObjectJournal)
            .WithTransaction()
            .Build();
    }

    private static void UpgradeOrThrow(
        UpgradeEngine upgrader,
        string stage)
    {
        var result =
            upgrader.PerformUpgrade();

        if (result.Successful)
        {
            return;
        }

        var scriptName = result.ErrorScript?.Name;

        throw new DatabaseMigrationException(
            stage,
            scriptName,
            result.Error);
    }

    private static void ClearProgrammableObjectJournal(
        string connectionString,
        string schema,
        string table)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "Connection string is required.",
                nameof(connectionString));
        }

        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new ArgumentException(
                "Schema is required.",
                nameof(schema));
        }

        if (string.IsNullOrWhiteSpace(table))
        {
            throw new ArgumentException(
                "Table is required.",
                nameof(table));
        }

        var quotedSchema =
            QuoteIdentifier(schema);

        var quotedTable =
            QuoteIdentifier(table);

        var qualifiedTable =
            quotedSchema + "." + quotedTable;

        using var connection =
            new SqlConnection(connectionString);
        connection.Open();

        var sql =
            "IF OBJECT_ID(@objectName, N'U') IS NOT NULL " +
            "BEGIN " +
            "DELETE FROM " + qualifiedTable + "; " +
            "END";

        using (var command =
               new SqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue(
                "@objectName",
                schema + "." + table);

            command.ExecuteNonQuery();
        }
    }

    private static string QuoteIdentifier(
        string identifier)
    {
        return "[" +
               identifier.Replace("]", "]]") +
               "]";
    }

    private static bool IsMigration(
        string scriptName,
        string migrationsPrefix)
    {
        return IsEmbeddedSqlScript(
            scriptName,
            migrationsPrefix);
    }

    private static bool IsProgrammableObject(
        string scriptName,
        string programmableObjectsPrefix)
    {
        return IsEmbeddedSqlScript(
            scriptName,
            programmableObjectsPrefix);
    }

    private static bool IsEmbeddedSqlScript(
        string scriptName,
        string prefix)
    {
        if (string.IsNullOrWhiteSpace(scriptName))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        return scriptName.StartsWith(
                   prefix,
                   StringComparison.OrdinalIgnoreCase)
               &&
               scriptName.EndsWith(
                   ".sql",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateOptions(
        Assembly scriptAssembly,
        DbUpOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(
                nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException(
                "Connection string is required.",
                nameof(options.ConnectionString));
        }

        if (scriptAssembly == null)
        {
            throw new ArgumentNullException(nameof(scriptAssembly));
        }

        if (string.IsNullOrWhiteSpace(
                options.BaseNamespace))
        {
            throw new ArgumentException(
                "BaseNamespace is required.",
                nameof(options.BaseNamespace));
        }

        if (string.IsNullOrWhiteSpace(
                options.Schema))
        {
            throw new ArgumentException(
                "Schema is required.",
                nameof(options.Schema));
        }

        if (string.IsNullOrWhiteSpace(
                options.MigrationJournal))
        {
            throw new ArgumentException(
                "MigrationJournal is required.",
                nameof(options.MigrationJournal));
        }

        if (string.IsNullOrWhiteSpace(
                options.ProgrammableObjectJournal))
        {
            throw new ArgumentException(
                "ProgrammableObjectJournal is required.",
                nameof(options.ProgrammableObjectJournal));
        }

        if (string.IsNullOrWhiteSpace(
                options.DependencyDirective))
        {
            throw new ArgumentException(
                "DependencyDirective is required.",
                nameof(options.DependencyDirective));
        }
    }
}
