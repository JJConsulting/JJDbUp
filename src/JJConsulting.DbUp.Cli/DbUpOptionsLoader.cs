using JJConsulting.DbUp.Configuration;
using Microsoft.Extensions.Configuration;

namespace JJConsulting.DbUp.Cli;

internal sealed record DbUpOptionsLoader(string? Assembly, string? ConnectionString, DbUpOptions Migrations)
{
    public string RequireConnection() => !string.IsNullOrWhiteSpace(ConnectionString)
        ? ConnectionString
        : throw new CliException(
            "A connection string is required. Set the configuration value selected by DatabaseMigrations:ConnectionStringKey (default: ConnectionStrings:DefaultConnection), or use --connection-string.");

    public static DbUpOptionsLoader Load(DbUpCommandOptions args, string workingDirectory, IEnumerable<KeyValuePair<string, string?>>? environment = null)
    {
        var configPath = ResolveConfigPath(args.Config, workingDirectory);
        var environmentConfig = LoadEnvironment(environment);
        var environmentName = environmentConfig["DOTNET_ENVIRONMENT"];
        if (string.IsNullOrWhiteSpace(environmentName))
            environmentName = environmentConfig["ASPNETCORE_ENVIRONMENT"];
        var baseFile = new ConfigurationBuilder().AddJsonFile(configPath, optional: args.Config is null).Build();
        IConfiguration? environmentFile = null;
        IConfiguration file = baseFile;
        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            if (environmentName.IndexOfAny(['/', '\\', ':']) >= 0 || environmentName is "." or "..")
                throw new CliException("The environment name must not contain a path.");
            var environmentPath = Path.Combine(Path.GetDirectoryName(configPath)!,
                Path.GetFileNameWithoutExtension(configPath) + "." + environmentName + Path.GetExtension(configPath));
            environmentFile = new ConfigurationBuilder().AddJsonFile(environmentPath, optional: true).Build();
            file = new ConfigurationBuilder().AddConfiguration(baseFile).AddConfiguration(environmentFile).Build();
        }
        var config = BuildConfiguration(args, file, environmentConfig);
        var source = ResolveSource(args, environmentConfig, file, workingDirectory, configPath);
        var options = BindMigrationOptions(config, environmentConfig, environmentFile, baseFile);
        options.ScriptsPath = source.ScriptsPath;
        string connectionKey;
        if (args.ConnectionName is not null)
            connectionKey = "ConnectionStrings:" + args.ConnectionName;
        else
            connectionKey = options.ConnectionStringKey;
        if (args.ConnectionString is null && string.IsNullOrWhiteSpace(connectionKey))
            throw new CliException("DatabaseMigrations:ConnectionStringKey must not be empty.");
        var connection = args.ConnectionString ?? config[connectionKey];

        return new DbUpOptionsLoader(source.Assembly, connection, options);
    }

    private static string ResolveConfigPath(string? file, string workingDirectory)
    {
        string path;
        if (file is null)
            path = DirectoryDiscovery.Ancestors(workingDirectory)
                .Select(directory => Path.Combine(directory.FullName, "appsettings.json"))
                .FirstOrDefault(File.Exists) ?? Path.Combine(workingDirectory, "appsettings.json");
        else
            path = Path.GetFullPath(file, workingDirectory);
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
        return filePath is not null
            ? Script.Resolve(filePath, null, Path.GetDirectoryName(configPath)!)
            : new Script(DirectoryDiscovery.FindScripts(workingDirectory), null);
    }

    private static DbUpOptions BindMigrationOptions(IConfiguration config, params IConfiguration?[] layersByPriority)
    {
        var options = new DbUpOptions();
        try
        {
            config.GetSection(DbUpOptions.SectionName).Bind(options);
            // Configuration merges array indices; migration adoption must replace the whole selection.
            foreach (var layer in layersByPriority)
            {
                var selection = layer?.GetSection(DbUpOptions.SectionName).GetChildren()
                    .FirstOrDefault(section => section.Key.Equals(nameof(DbUpOptions.ScriptsToMarkAsExecuted),
                        StringComparison.OrdinalIgnoreCase));
                if (selection is null)
                    continue;
                options.ScriptsToMarkAsExecuted = selection.Get<string[]>() ?? [];
                break;
            }
        }
        catch (InvalidOperationException)
        {
            throw new CliException("Invalid DatabaseMigrations settings.");
        }

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
