using System.Reflection;
using DbUp.Engine.Output;

namespace JJConsulting.DbUp.Cli;

internal sealed class DbUpCommandService(ConsoleHelper output, TextWriter error, string workingDirectory, IEnumerable<KeyValuePair<string, string?>>? environment)
{
    private readonly IUpgradeLog _log = new SafeUpgradeLog(error);

    public int Migrate(DbUpCommandOptions args)
    {
        var settings = Load(args);
        var connection = settings.RequireConnection();
        var preparation = GetScripts(settings);
        if (!preparation.IsValid)
            return output.Fail(preparation.Error, 2);
        var scripts = preparation.Scripts;
        DatabaseMigrator.Migrate(connection, scripts, _log);
        return output.Complete(new CommandMessage("Migration completed."));
    }

    public int Status(StatusOptions args)
    {
        var settings = Load(args);
        var connection = settings.RequireConnection();
        var preparation = GetScripts(settings);
        if (!preparation.IsValid)
            return output.Fail(preparation.Error, 2);
        var scripts = preparation.Scripts;
        var status = DatabaseMigrator.GetStatus(connection, scripts, _log);
        return output.Complete(status, args.Check && status.HasPendingChanges ? 3 : 0);
    }

    public int Validate(DbUpCommandOptions args)
    {
        var settings = Load(args);
        var preparation = GetScripts(settings);
        if (!preparation.IsValid)
            return output.Fail(preparation.Error, 2);
        var scripts = preparation.Scripts;
        return output.Complete(new ValidationSummary(scripts.Count));
    }

    public int New(NewOptions args)
    {
        return output.Complete(new ScriptCreated(SqlScriptCreator.Create(Load(args), args.Name, args.Type)));
    }

    public int MarkExecuted(MarkExecutedOptions args)
    {
        var settings = Load(args);
        settings.Migrations.ScriptsToMarkAsExecuted = args.ScriptsToMark.Select(n => n.Replace('\\', '.').Replace('/', '.')).ToArray();
        var connection = settings.RequireConnection();
        var preparation = GetScripts(settings);
        if (!preparation.IsValid)
            return output.Fail(preparation.Error, 2);
        var scripts = preparation.Scripts;
        if (scripts.ScriptsToMark.Count == 0)
            return output.Fail("Select at least one migration to mark as executed.", 2);
        DatabaseMigrator.MarkAsExecuted(connection, scripts, _log);
        return output.Complete(new CommandMessage("Selected migrations marked as executed."));
    }

    private DbUpOptionsLoader Load(DbUpCommandOptions args)
    {
        return DbUpOptionsLoader.Load(args, workingDirectory, environment);
    }

    private static MigrationScriptResult GetScripts(DbUpOptionsLoader optionsLoader)
    {
        if (optionsLoader.Assembly is null)
            return MigrationScriptService.GetScripts(optionsLoader.Migrations);
        if (!File.Exists(optionsLoader.Assembly))
            return MigrationScriptResult.Failed("The scripts assembly does not exist.");
        
        return MigrationScriptService.GetScripts(Assembly.LoadFrom(optionsLoader.Assembly), optionsLoader.Migrations);
    }

    // DbUp messages can contain SQL or credentials. Keep diagnostics safe for CI logs.
    private sealed class SafeUpgradeLog(TextWriter error) : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) { }
        public void LogDebug(string format, params object[] args) { }
        public void LogInformation(string format, params object[] args) { }
        public void LogWarning(string format, params object[] args) => error.WriteLine("Database operation reported a warning.");
        public void LogError(string format, params object[] args) => error.WriteLine("Database operation reported an error.");
        public void LogError(Exception ex, string format, params object[] args) => LogError(format, args);
    }
}
