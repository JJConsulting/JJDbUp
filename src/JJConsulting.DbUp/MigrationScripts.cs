using DbUp.Engine;

namespace JJConsulting.DbUp;

/// <summary>Validated scripts, ordered for execution.</summary>
public sealed class MigrationScripts
{
    public required IReadOnlyList<SqlScript> Migrations { get; init; }
    public required IReadOnlyList<SqlScript> ProgrammableObjects { get; init; }
    public required IReadOnlyList<SqlScript> ScriptsToMark { get; init; }
    public int Count => Migrations.Count + ProgrammableObjects.Count;
}
