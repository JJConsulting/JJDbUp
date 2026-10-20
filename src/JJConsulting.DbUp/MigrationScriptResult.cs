using System.Diagnostics.CodeAnalysis;

namespace JJConsulting.DbUp;

/// <summary>The outcome of discovering and validating migration scripts without database access.</summary>
public sealed class MigrationScriptResult
{
    private MigrationScriptResult(MigrationScripts? scripts, string? error)
    {
        Scripts = scripts;
        Error = error;
    }

    [MemberNotNullWhen(true, nameof(Scripts))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsValid => Scripts is not null;

    public MigrationScripts? Scripts { get; }
    public string? Error { get; }

    internal static MigrationScriptResult Ready(MigrationScripts scripts) => new(scripts, null);
    public static MigrationScriptResult Failed(string error) => new(null, error);
}
