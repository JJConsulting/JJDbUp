using System.Text.Json;
using Spectre.Console;

namespace JJConsulting.DbUp.Cli;

internal sealed class ConsoleHelper(string command, bool json, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public IAnsiConsole Console { get; } = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(output), Interactive = InteractionSupport.No
    });

    public int Complete(DatabaseMigrationStatus status, int exitCode = 0)
    {
        if (json)
            WriteJson(status, exitCode);
        else
            WriteStatus(status);
        return exitCode;
    }

    public int Complete(ScriptCreated created)
    {
        if (json)
            WriteJson(created, 0);
        else
            Console.WriteLine(created.Path);
        return 0;
    }

    public int Complete(BaselineFileResult result)
    {
        if (json)
            WriteJson(result, 0);
        else
            Console.WriteLine($"{(result.DryRun ? "Would create" : "Created")}: {result.Path}");
        return 0;
    }

    public int Complete(ValidationSummary summary)
    {
        if (json)
            WriteJson(summary, 0);
        else
            Console.MarkupLine($"[green]Validated {summary.ScriptCount} SQL scripts.[/]");
        return 0;
    }

    public int Complete(CommandMessage result)
    {
        if (json)
            WriteJson(result, 0);
        else
            Console.MarkupLine($"[green]{Markup.Escape(result.Message)}[/]");
        return 0;
    }

    public int Fail(string message, int exitCode)
    {
        if (json)
            WriteJson(null, exitCode, message);
        else
            error.WriteLine(message);
        return exitCode;
    }

    private void WriteJson(object? data, int exitCode, string? message = null)
    {
        output.WriteLine(JsonSerializer.Serialize(new { command, success = exitCode is 0 or 3, data, error = message }, JsonOptions));
    }

    private void WriteStatus(DatabaseMigrationStatus status)
    {
        Console.MarkupLine(status.DatabaseExists ? "[green]Database exists.[/]" : "[yellow]Database does not exist.[/]");
        
        if (!status.HasPendingChanges)
        {
            Console.MarkupLine("[green]No pending changes.[/]");
            return;
        }
        var table = new Table().Border(TableBorder.Simple).AddColumn("Type").AddColumn("Pending script");
        
        foreach (var name in status.PendingMigrations) 
            table.AddRow("Migration", Markup.Escape(name));
        foreach (var name in status.PendingObjects) 
            table.AddRow("Object", Markup.Escape(name));
        
        Console.Write(table);
    }
}

internal sealed record ScriptCreated(string Path);
internal sealed record ValidationSummary(int ScriptCount);
internal sealed record CommandMessage(string Message);
