using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace JJConsulting.DbUp.Cli;

internal class DbUpCommandOptions : CommandSettings
{
    [CommandOption("--config <FILE>"), Description("Configuration file; searches current and parent directories for appsettings.json.")]
    public string? Config { get; set; }

    [CommandOption("--scripts|--scripts-path <DIRECTORY>"), Description("Root containing Migrations/ and ProgrammableObjects/; discovered from the current directory by default.")]
    public string? ScriptsPath { get; set; }

    [CommandOption("--assembly <DLL>"), Description("Compiled assembly containing embedded SQL resources.")]
    public string? Assembly { get; set; }

    [CommandOption("--connection-string <CONNECTION>"), Description("SQL Server connection string; prefer environment variables for secrets.")]
    public string? ConnectionString { get; set; }

    [CommandOption("--connection-name <NAME>"), Description("ConnectionStrings name; overrides DatabaseMigrations:ConnectionStringKey.")]
    public string? ConnectionName { get; set; }

    [CommandOption("--schema <SCHEMA>"), Description("Journal schema; defaults to dbo.")]
    public string? Schema { get; set; }

    [CommandOption("--migration-journal <TABLE>"), Description("Migration journal table.")]
    public string? MigrationJournal { get; set; }

    [CommandOption("--programmable-object-journal <TABLE>"), Description("Object hash journal table.")]
    public string? ObjectJournal { get; set; }

    [CommandOption("--dependency-directive <DIRECTIVE>"), Description("Object dependency directive; defaults to #requires.")]
    public string? Directive { get; set; }

    [CommandOption("--ensure-database <BOOL>"), Description("Create the database if missing; defaults to true.")]
    public bool? EnsureDatabase { get; set; }

    [CommandOption("--output <FORMAT>"), Description("Output format: text or json."), DefaultValue("text")]
    public string Output { get; set; } = "text";

    public override ValidationResult Validate() => Output is "text" or "json"
        ? ValidationResult.Success()
        : ValidationResult.Error("Output must be text or json.");
}

internal sealed class StatusOptions : DbUpCommandOptions
{
    [CommandOption("--check"), Description("Exit with code 3 if changes are pending.")]
    public bool Check { get; set; }
}

internal sealed class NewOptions : DbUpCommandOptions
{
    [CommandArgument(0, "<name>"), Description("Relative script name, for example V003_AddStatus.")]
    public string Name { get; set; } = "";

    [CommandOption("--type <TYPE>"), Description("migration or object."), DefaultValue("migration")]
    public string Type { get; set; } = "migration";
}

internal sealed class MarkExecutedOptions : DbUpCommandOptions
{
    [CommandOption("--script <NAME>", isRequired: true), Description("Migration name relative to Migrations, including .sql; repeat to select more.")]
    public string[] ScriptsToMark { get; set; } = [];
}

internal sealed class BaselineOptions : DbUpCommandOptions
{
    [CommandArgument(0, "<name>"), Description("Migration filename, for example 001_InitialState; .sql is optional.")]
    public string Name { get; set; } = "";

    [CommandOption("--dry-run|--dry"), Description("Extract and validate, showing the destination without changing files.")]
    public bool DryRun { get; set; }
}
