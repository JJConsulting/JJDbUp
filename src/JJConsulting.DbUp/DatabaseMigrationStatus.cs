namespace JJConsulting.DbUp;

/// <summary>The scripts a migration run would apply, before any configured adoption.</summary>
public sealed class DatabaseMigrationStatus
{
    public required bool DatabaseExists { get; init; }
    public required IReadOnlyList<string> PendingMigrations { get; init; } 
    public required IReadOnlyList<string> PendingObjects { get; init; }
    public bool HasPendingChanges => PendingMigrations.Count > 0 || PendingObjects.Count > 0;
}
