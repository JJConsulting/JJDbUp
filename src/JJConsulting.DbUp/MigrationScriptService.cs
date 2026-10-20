using System.Reflection;
using DbUp.Engine;
using DbUpX;
using JJConsulting.DbUp.Configuration;

namespace JJConsulting.DbUp;

/// <summary>Discovers, validates and orders migration scripts without database access.</summary>
public static class MigrationScriptService
{
    private static readonly string[] Categories = ["Migrations", "ProgrammableObjects"];

    /// <summary>Loads and validates the configured scripts directory.</summary>
    public static MigrationScriptResult GetScripts(DbUpOptions options)
    {
        var error = ValidateOptions(options);
        if (error is not null)
            return MigrationScriptResult.Failed(error);
        if (string.IsNullOrWhiteSpace(options.ScriptsPath))
            return MigrationScriptResult.Failed("A scripts directory is required.");
        if (!Directory.Exists(options.ScriptsPath))
            return MigrationScriptResult.Failed("The scripts directory does not exist.");

        var directory = options.ScriptsPath;

        var scripts = Categories.SelectMany(category =>
            {
                if (Directory.Exists(Path.Combine(directory, category)))
                    return Directory.EnumerateFiles(Path.Combine(directory, category), "*", SearchOption.AllDirectories)
                        .Where(path => path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));
                return [];
            })
            .Order(StringComparer.Ordinal)
            .Select(path => new SqlScript(options.BaseNamespace + "." + Path.GetRelativePath(directory, path)
                    .Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.'),
                File.ReadAllText(path)));

        return GetScriptsWithValidatedOptions(scripts, options);
    }

    /// <summary>Loads and validates embedded scripts; ScriptsPath is ignored.</summary>
    public static MigrationScriptResult GetScripts(Assembly assembly, DbUpOptions options)
    {
        if (assembly is null)
            return MigrationScriptResult.Failed("A scripts assembly is required.");
        var error = ValidateOptions(options);
        if (error is not null)
            return MigrationScriptResult.Failed(error);

        var scripts = assembly.GetManifestResourceNames()
            .Where(name =>
                IsScript(name, options.MigrationsPrefix) || IsScript(name, options.ProgrammableObjectsPrefix))
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return new SqlScript(name, reader.ReadToEnd());
            });

        return GetScriptsWithValidatedOptions(scripts, options);
    }

    /// <summary>Validates names, dependencies and migration selection, without database access.</summary>
    public static MigrationScriptResult GetScripts(IEnumerable<SqlScript> scripts, DbUpOptions options)
    {
        if (scripts is null)
            return MigrationScriptResult.Failed("SQL scripts are required.");
        var error = ValidateOptions(options);
        return error is not null
            ? MigrationScriptResult.Failed(error)
            : GetScriptsWithValidatedOptions(scripts, options);
    }

    private static MigrationScriptResult GetScriptsWithValidatedOptions(IEnumerable<SqlScript> scripts, DbUpOptions options)
    {
        var all = scripts.ToArray();
        var migrations = all.Where(s => IsScript(s.Name, options.MigrationsPrefix))
            .OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
        var objects = all.Where(s => IsScript(s.Name, options.ProgrammableObjectsPrefix))
            .OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();

        if (migrations.Length == 0 && objects.Length == 0)
            return MigrationScriptResult.Failed(
                "No SQL scripts were found under the configured migration or programmable object prefixes.");
        if (migrations.Concat(objects).GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            return MigrationScriptResult.Failed("Duplicate SQL script names were found.");

        try
        {
            objects = objects.OrderByDependency(options.DependencyDirective).ToArray();
        }
        catch (InvalidOperationException)
        {
            // Dependency diagnostics can include script names or contents. Keep the returned message safe for CLI logs.
            return MigrationScriptResult.Failed("Programmable object dependencies are missing or cyclic.");
        }

        if (options.ScriptsToMarkAsExecuted is null)
            return MigrationScriptResult.Failed("Migrations to mark as executed are required.");

        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in options.ScriptsToMarkAsExecuted)
        {
            var fullName = options.MigrationsPrefix + name;
            if (string.IsNullOrWhiteSpace(name) || migrations.All(s => s.Name != fullName))
                return MigrationScriptResult.Failed(
                    "A selected migration was not found under the configured migrations prefix.");
            selected.Add(fullName);
        }

        var scriptsToMark = migrations.Where(s => selected.Contains(s.Name)).ToArray();
        return MigrationScriptResult.Ready(new MigrationScripts
        {
            Migrations = migrations,
            ProgrammableObjects = objects,
            ScriptsToMark = scriptsToMark,
            // Prepared scripts retain the settings that were validated, even if the caller reuses its options.
            Options = new DbUpOptions
            {
                ScriptsPath = options.ScriptsPath,
                BaseNamespace = options.BaseNamespace,
                Schema = options.Schema,
                MigrationJournal = options.MigrationJournal,
                ProgrammableObjectJournal = options.ProgrammableObjectJournal,
                DependencyDirective = options.DependencyDirective,
                EnsureDatabase = options.EnsureDatabase,
                ScriptsToMarkAsExecuted = [.. options.ScriptsToMarkAsExecuted]
            }
        });
    }

    private static bool IsScript(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    private static string? ValidateOptions(DbUpOptions? options)
    {
        if (options is null)
            return "Migration options are required.";
        if (string.IsNullOrWhiteSpace(options.BaseNamespace))
            return "BaseNamespace is required.";
        if (string.IsNullOrWhiteSpace(options.Schema))
            return "Schema is required.";
        if (string.IsNullOrWhiteSpace(options.MigrationJournal))
            return "MigrationJournal is required.";
        if (string.IsNullOrWhiteSpace(options.ProgrammableObjectJournal))
            return "ProgrammableObjectJournal is required.";
        if (string.IsNullOrWhiteSpace(options.DependencyDirective))
            return "DependencyDirective is required.";
        return null;
    }
}
