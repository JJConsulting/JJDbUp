using DbUp;
using DbUp.Engine;
using DbUpX;
using JJDbUpMigrator.Config;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace JJDbUpMigrator;


public static class DatabaseMigrator
{
    public static void Migrate(
        string connectionString,
        Assembly scriptAssembly,
        string baseNamespace)
    {
        var options = new DbUpMigratorOptions
        {
            ConnectionString = connectionString,
            ScriptAssembly = scriptAssembly,
            BaseNamespace = baseNamespace
        };

        Migrate(options);
    }

    public static void Migrate(
        DbUpMigratorOptions options)
    {
        ValidateOptions(options);

        var scriptsToMark = ResolveScriptsToMark(options);

        // =================================================
        // 0. ENSURE DATABASE
        // =================================================

        if (options.EnsureDatabase)
        {
            EnsureDatabase.For.SqlDatabase(
                options.ConnectionString);
        }

        // =================================================
        // 1. MIGRATIONS
        // =================================================

        if (scriptsToMark.Count > 0)
        {
            // Seleciona somente os scripts informados; os demais continuam pendentes.
            var result = BuildMigrationsUpgrader(options, scriptsToMark).MarkAsExecuted();
            if (!result.Successful)
            {
                throw new DatabaseMigrationException(
                    "Migration adoption",
                    result.ErrorScript?.Name ?? string.Join(", ", scriptsToMark),
                    result.Error);
            }
        }

        var migrationsUpgrader =
            BuildMigrationsUpgrader(options);

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
            BuildProgrammableObjectsUpgrader(
                options);

        UpgradeOrThrow(
            programmableObjectsUpgrader,
            "Programmable Objects");
    }

    private static HashSet<string> ResolveScriptsToMark(DbUpMigratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.ScriptsToMarkAsExecuted);

        var resources = new HashSet<string>(
            options.ScriptAssembly.GetManifestResourceNames(), StringComparer.Ordinal);
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

    private static UpgradeEngine BuildMigrationsUpgrader(
        DbUpMigratorOptions options,
        HashSet<string>? scriptsToMark = null)
    {
        return DeployChanges.To
            .SqlDatabase(
                options.ConnectionString)

            .WithScriptsEmbeddedInAssembly(
                options.ScriptAssembly,
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
        DbUpMigratorOptions options)
    {
        return DeployChanges.To
            .SqlDatabase(options.ConnectionString)

            .WithScriptsEmbeddedInAssembly(
                options.ScriptAssembly,
                scriptName =>
                    IsProgrammableObject(
                        scriptName,
                        options.ProgrammableObjectsPrefix))

            .WithScriptSorter(
                scripts =>
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

        var scriptName =
            result.ErrorScript == null
                ? null
                : result.ErrorScript.Name;

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

        using (var connection =
               new SqlConnection(connectionString))
        {
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
        DbUpMigratorOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(
                nameof(options));
        }

        if (string.IsNullOrWhiteSpace(
                options.ConnectionString))
        {
            throw new ArgumentException(
                "ConnectionString is required.",
                nameof(options.ConnectionString));
        }

        if (options.ScriptAssembly == null)
        {
            throw new ArgumentException(
                "ScriptAssembly is required.",
                nameof(options.ScriptAssembly));
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
