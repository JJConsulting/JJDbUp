using JJConsulting.DbUp.Exceptions;
using Spectre.Console.Cli;

namespace JJConsulting.DbUp.Cli;

internal static class CliApplication
{
    public static int Run(string[] args, TextWriter output, TextWriter error, string? workingDirectory = null,
        IEnumerable<KeyValuePair<string, string?>>? environment = null)
    {
        var command = args.FirstOrDefault(a => a is "migrate" or "status" or "validate" or "new" or "mark-executed" or "baseline") ??
                      "jjdbup";

        // Preserve JSON errors even when parsing fails before settings can be bound.
        var json = args.Contains("--output=json") || args.Zip(args.Skip(1)).Any(pair => pair is ("--output", "json"));
        var result = new ConsoleHelper(command, json, output, error);
        var commands = new DbUpCommandService(result, error, workingDirectory ?? Directory.GetCurrentDirectory(),
            environment);
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("jjdbup");
            config.SetApplicationVersion(typeof(CliApplication).Assembly.GetName().Version!.ToString(3));
            config.Settings.Console = result.Console;
            config.PropagateExceptions();
            config.AddDelegate<DbUpCommandOptions>("migrate", (_, args, _) => commands.Migrate(args))
                .WithDescription("Apply pending migrations and changed programmable objects.");
            config.AddDelegate<StatusOptions>("status", (_, args, _) => commands.Status(args))
                .WithDescription("Inspect pending scripts without changing the database.");
            config.AddDelegate<DbUpCommandOptions>("validate", (_, args, _) => commands.Validate(args))
                .WithDescription("Validate scripts and dependencies offline; SQL syntax is not checked.");
            config.AddDelegate<NewOptions>("new", (_, args, _) => commands.New(args))
                .WithDescription("Create a SQL script without overwriting existing files.");
            config.AddDelegate<BaselineOptions>("baseline", (_, args, _) => commands.Baseline(args))
                .WithDescription("Extract the current schema as a regular migration; use --dry-run to preview.");
            config.AddDelegate<MarkExecutedOptions>("mark-executed", (_, args, _) => commands.MarkExecuted(args))
                .WithDescription("Adopt selected migrations without executing their SQL.");
        });
        try
        {
            return app.Run(args);
        }
        catch (CliException exception)
        {
            return result.Fail(exception.Message, 2);
        }
        catch (CommandRuntimeException)
        {
            return result.Fail("Invalid command line. Use jjdbup <command> --help for supported arguments.", 2);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or FormatException
                                              or BadImageFormatException)
        {
            return result.Fail(
                "Invalid configuration or scripts. Check source paths, journal options, script names and dependencies.",
                2);
        }
        catch (DatabaseMigrationException exception)
        {
            return result.Fail($"Database operation failed during {exception.Stage}.", 1);
        }
        catch (Exception exception)
        {
            var detail = exception is Microsoft.Data.SqlClient.SqlException sql
                ? $"SQL Server error {sql.Number}"
                : exception.GetType().Name;
            return result.Fail(
                $"Operation failed ({detail}). Check database connectivity, permissions and script validity.", 1);
        }
    }
}
