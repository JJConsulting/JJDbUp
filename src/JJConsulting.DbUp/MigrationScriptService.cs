using System.Reflection;
using DbUp.Engine;
using DbUp.ScriptProviders;
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
        string prefix;
        try
        {
            prefix = ScriptIdentity.DirectoryPrefix(directory);
        }
        catch (InvalidDataException exception)
        {
            return MigrationScriptResult.Failed(exception.Message);
        }

        // Let DbUp discover and read scripts; retain the namespace used by existing journals.
        var provider = new FileSystemScriptProvider(directory, new FileSystemScriptOptions
        {
            IncludeSubDirectories = true,
            // The default *.sql glob is case sensitive on Linux.
            Extensions = ["*"],
            Filter = path =>
            {
                var relative = Path.GetRelativePath(directory, path);
                return path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) &&
                       Categories.Any(category =>
                           relative.StartsWith(category + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            }
        });
        var scripts = provider.GetScripts(null)
            .Select(script =>
                new SqlScript(prefix + "." + script.Name, script.Contents, script.SqlScriptOptions));

        return GetScriptsWithValidatedOptions(scripts, options, prefix);
    }

    /// <summary>Loads and validates embedded scripts; ScriptsPath is ignored.</summary>
    public static MigrationScriptResult GetScripts(Assembly assembly, DbUpOptions options)
    {
        if (assembly is null)
            return MigrationScriptResult.Failed("A scripts assembly is required.");
        var error = ValidateOptions(options);
        if (error is not null)
            return MigrationScriptResult.Failed(error);

        var provider = new EmbeddedScriptProvider(assembly, name =>
            IsScript(name, "Migrations") || IsScript(name, "ProgrammableObjects"));
        var scripts = provider.GetScripts(null);

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

    private static MigrationScriptResult GetScriptsWithValidatedOptions(IEnumerable<SqlScript> scripts,
        DbUpOptions options, string? directoryPrefix = null)
    {
        var all = scripts.ToArray();
        var migrations = all.Where(s => ScriptIdentity.RelativeName(s.Name, "Migrations", directoryPrefix) is not null)
            .OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
        var objects = all.Where(s => ScriptIdentity.RelativeName(s.Name, "ProgrammableObjects", directoryPrefix) is not null)
            .OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();

        if (migrations.Length == 0 && objects.Length == 0)
            return MigrationScriptResult.Failed(
                "No SQL scripts were found under Migrations or ProgrammableObjects.");
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
            var matches = migrations.Where(s => ScriptIdentity.RelativeName(s.Name, "Migrations", directoryPrefix) == name).ToArray();
            if (string.IsNullOrWhiteSpace(name) || matches.Length != 1)
                return MigrationScriptResult.Failed(
                    "A selected migration was not found or its relative name is ambiguous.");
            selected.Add(matches[0].Name);
        }

        var scriptsToMark = migrations.Where(s => selected.Contains(s.Name)).ToArray();
        return MigrationScriptResult.Ready(new MigrationScripts
        {
            Migrations = migrations,
            ProgrammableObjects = objects,
            ScriptsToMark = scriptsToMark
        });
    }

    private static bool IsScript(string name, string category) => ScriptIdentity.RelativeName(name, category) is not null;

    private static string? ValidateOptions(DbUpOptions? options)
    {
        if (options is null)
            return "Migration options are required.";
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
