namespace JJConsulting.DbUp;

/// <summary>A validated schema snapshot. The caller owns all file operations.</summary>
public sealed class BaselineGenerationResult
{
    public required string Sql { get; init; }
    public required string ScriptName { get; init; }
}
