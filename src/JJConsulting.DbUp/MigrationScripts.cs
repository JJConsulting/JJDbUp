using DbUp.Engine;
using JJConsulting.DbUp.Configuration;

namespace JJConsulting.DbUp;

/// <summary>Validated scripts, ordered for execution and bound to their migration settings.</summary>
public sealed class MigrationScripts
{
    public required IReadOnlyList<SqlScript> Migrations { get; init; }
    public required IReadOnlyList<SqlScript> ProgrammableObjects { get; init; }
    public required IReadOnlyList<SqlScript> ScriptsToMark { get; init; }
    public required DbUpOptions Options { get; init; }
    public int Count => Migrations.Count + ProgrammableObjects.Count;
}
