using DbUp.Engine.Output;

namespace JJConsulting.DbUp;

public static class DatabaseMigrator
{
    /// <summary>Applies a previously validated set of scripts without repeating discovery or validation.</summary>
    public static void Migrate(string connectionString, MigrationScripts scripts, IUpgradeLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripts);
        var database = new MigrationDatabase(connectionString, scripts.Options, log ?? new ConsoleUpgradeLog());

        database.EnsureExists();
        database.MarkAsExecuted(scripts.ScriptsToMark);
        var hasPendingMigrations = database.ApplyMigrations(scripts.Migrations);

        // Invalidate before the object transaction so a failed stage can retry every object.
        if (hasPendingMigrations)
            database.ClearProgrammableObjectJournal();

        database.ApplyProgrammableObjects(scripts.ProgrammableObjects);
    }

    /// <summary>Reads journals without creating a database or changing any persisted state.</summary>
    public static DatabaseMigrationStatus GetStatus(string connectionString, MigrationScripts scripts, IUpgradeLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripts);
        var database = new MigrationDatabase(connectionString, scripts.Options, log ?? new ConsoleUpgradeLog());
        return database.GetStatus(scripts);
    }

    /// <summary>Records the prepared selection as executed; never executes SQL scripts.</summary>
    public static void MarkAsExecuted(string connectionString, MigrationScripts scripts, IUpgradeLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripts);
        if (scripts.ScriptsToMark.Count == 0)
            return;
        var database = new MigrationDatabase(connectionString, scripts.Options, log ?? new ConsoleUpgradeLog());
        database.EnsureExists();
        database.MarkAsExecuted(scripts.ScriptsToMark);
    }
}
