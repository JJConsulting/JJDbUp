using JJConsulting.DbUp.Configuration;
using Microsoft.Extensions.Configuration;

namespace JJConsulting.DbUp.Cli;

internal sealed record DbUpOptionsLoader(string? Assembly, string? ConnectionString, DbUpOptions Migrations)
{
    public string RequireConnection() => !string.IsNullOrWhiteSpace(ConnectionString)
        ? ConnectionString
        : throw new CliException(
            "A connection string is required. Set ConnectionStrings__DefaultConnection or --connection-string.");

    public static DbUpOptionsLoader Load(DbUpCommandOptions args, string workingDirectory, IEnumerable<KeyValuePair<string, string?>>? environment = null)
    {
        var configPath = ResolveConfigPath(args.Config, workingDirectory);
        var file = new ConfigurationBuilder().AddJsonFile(configPath, optional: args.Config is null).Build();
        var environmentConfig = LoadEnvironment(environment);
        var config = BuildConfiguration(args, file, environmentConfig);
        var source = ResolveSource(args, environmentConfig, file, workingDirectory, configPath);
        var options = BindMigrationOptions(config);
        options.ScriptsPath = source.ScriptsPath;
        var connection = args.ConnectionString ?? config.GetConnectionString(args.ConnectionName ?? "DefaultConnection");

        return new DbUpOptionsLoader(source.Assembly, connection, options);
    }

    private static string ResolveConfigPath(string? file, string workingDirectory)
    {
        var path = Path.GetFullPath(file ?? "appsettings.json", workingDirectory);
        if (file is not null && !File.Exists(path))
            throw new CliException("The configuration file does not exist.");
        return path;
    }

    private static IConfiguration LoadEnvironment(IEnumerable<KeyValuePair<string, string?>>? environment)
    {
        var builder = new ConfigurationBuilder();
        if (environment is null)
            builder.AddEnvironmentVariables();
        else
            builder.AddInMemoryCollection(environment.Select(entry =>
                new KeyValuePair<string, string?>(entry.Key.Replace("__", ":"), entry.Value)));
        return builder.Build();
    }

    private static IConfiguration BuildConfiguration(DbUpCommandOptions args, IConfiguration file, IConfiguration environment)
    {
        var overrides = new Dictionary<string, string?>
        {
            ["DatabaseMigrations:BaseNamespace"] = args.BaseNamespace,
            ["DatabaseMigrations:Schema"] = args.Schema,
            ["DatabaseMigrations:MigrationJournal"] = args.MigrationJournal,
            ["DatabaseMigrations:ProgrammableObjectJournal"] = args.ObjectJournal,
            ["DatabaseMigrations:DependencyDirective"] = args.Directive,
            ["DatabaseMigrations:EnsureDatabase"] = args.EnsureDatabase?.ToString()
        };

        return new ConfigurationBuilder()
            .AddConfiguration(file)
            .AddConfiguration(environment)
            .AddInMemoryCollection(overrides.Where(entry => entry.Value is not null))
            .Build();
    }

    private static Script ResolveSource(DbUpCommandOptions args, IConfiguration environment, IConfiguration file,
        string workingDirectory, string configPath)
    {
        // Select both paths from the same layer so a source replaces the lower-priority source entirely.
        if (args.ScriptsPath is not null || args.Assembly is not null)
            return Script.Resolve(args.ScriptsPath, args.Assembly, workingDirectory);

        var environmentPath = environment.GetSection(DbUpOptions.SectionName)["ScriptsPath"];
        if (environmentPath is not null)
            return Script.Resolve(environmentPath, null, workingDirectory);

        var filePath = file.GetSection(DbUpOptions.SectionName)["ScriptsPath"];
        return Script.Resolve(filePath, null, Path.GetDirectoryName(configPath)!);
    }

    private static DbUpOptions BindMigrationOptions(IConfiguration config)
    {
        var options = new DbUpOptions();
        try
        {
            config.GetSection(DbUpOptions.SectionName).Bind(options);
        }
        catch (InvalidOperationException)
        {
            throw new CliException("Invalid DatabaseMigrations settings.");
        }

        if (string.IsNullOrWhiteSpace(options.BaseNamespace))
            throw new CliException("BaseNamespace is required.");
        return options;
    }

    private sealed record Script(string? ScriptsPath, string? Assembly)
    {
        public static Script Resolve(string? scripts, string? assembly, string baseDirectory)
        {
            var scriptsPath = ResolvePath(scripts, baseDirectory);
            var assemblyPath = ResolvePath(assembly, baseDirectory);
            if ((scriptsPath is null) == (assemblyPath is null))
                throw new CliException(
                    "Specify --scripts or --assembly, or set DatabaseMigrations:ScriptsPath; select exactly one source.");
            return new Script(scriptsPath, assemblyPath);
        }

        private static string? ResolvePath(string? path, string baseDirectory)
        {
            if (path is null)
                return null;
            if (string.IsNullOrWhiteSpace(path))
                throw new CliException("Script source paths cannot be empty.");
            return Path.GetFullPath(path, baseDirectory);
        }
    }
}
