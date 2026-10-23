namespace JJConsulting.DbUp.Configuration;

public sealed class DbUpOptions
{
    public const string SectionName = "DatabaseMigrations";

    public string BaseNamespace { get; set; } = string.Empty;

    public string Schema { get; set; } = "dbo";

    public string MigrationJournal { get; set; } = "SchemaVersions";

    public string ProgrammableObjectJournal { get; set; } = "ProgrammableObjectVersions";

    public string DependencyDirective { get; set; } = "#requires";

    public bool EnsureDatabase { get; set; } = true;

    // Nomes relativos a MigrationsPrefix, incluindo .sql. Use somente para scripts ja aplicados.
    public string[] ScriptsToMarkAsExecuted { get; set; } = [];

    public string MigrationsPrefix => BaseNamespace + ".Migrations.";

    public string ProgrammableObjectsPrefix => BaseNamespace + ".ProgrammableObjects.";
}
