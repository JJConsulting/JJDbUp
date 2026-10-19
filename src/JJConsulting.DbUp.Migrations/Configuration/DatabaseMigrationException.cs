namespace JJConsulting.DbUp.Migrations.Configuration;

public sealed class DatabaseMigrationException(
    string stage,
    string? scriptName,
    Exception innerException)
    : Exception(BuildMessage(stage, scriptName), innerException)
{
    public string Stage { get; } = stage;

    public string? ScriptName { get; } = scriptName;

    private static string BuildMessage(
        string stage,
        string? scriptName)
    {
        if (string.IsNullOrWhiteSpace(scriptName))
            return $"Database migration failed during '{stage}'.";

        return $"Database migration failed during '{stage}'. Script: '{scriptName}'.";
    }
}

