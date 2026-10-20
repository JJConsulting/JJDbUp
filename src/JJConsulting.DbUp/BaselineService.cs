using JJConsulting.DbUp.Configuration;
using JJConsulting.DbUp.Utils;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace JJConsulting.DbUp;

/// <summary>Extracts and statically validates a schema baseline without changing the database or files.</summary>
public static class BaselineService
{
    public static BaselineGenerationResult Generate(string connectionString, DbUpOptions options, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        var baselineName = NormalizeName(name);
        using var connection = new SqlConnection(connectionString);
        connection.Open(); // Never create a missing database, even when EnsureDatabase is enabled.
        RequireSchemaVisibility(connection);
        var journalTableIds = GetJournalTableIds(connection, options);
        RequireIndependentStructure(connection, journalTableIds);
        var sql = SmoSchemaExtractor.Extract(connection, journalTableIds);
        ValidateSql(sql);

        return new BaselineGenerationResult
        {
            Sql = sql,
            ScriptName = baselineName
        };
    }

    internal static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or "..")
            throw new ArgumentException("Use a simple baseline filename inside Migrations.", nameof(name));
        var normalized = name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            ? name[..^4] + ".sql"
            : name + ".sql";
        return normalized;
    }

    private static void RequireSchemaVisibility(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')";
        if (command.ExecuteScalar() is not 1)
            throw new InvalidOperationException("Baseline generation requires VIEW DEFINITION on the database.");
    }

    private static HashSet<int> GetJournalTableIds(SqlConnection connection, DbUpOptions options)
    {
        using var quoting = new SqlCommandBuilder();
        var schema = quoting.QuoteIdentifier(options.Schema) + ".";
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_ID(@migration, 'U') UNION SELECT OBJECT_ID(@objects, 'U')";
        command.Parameters.AddWithValue("@migration",
            schema + quoting.QuoteIdentifier(options.MigrationJournal));
        command.Parameters.AddWithValue("@objects",
            schema + quoting.QuoteIdentifier(options.ProgrammableObjectJournal));
        using var reader = command.ExecuteReader();
        var ids = new HashSet<int>();
        while (reader.Read())
            if (!reader.IsDBNull(0))
                ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static void RequireIndependentStructure(SqlConnection connection, IReadOnlySet<int> journalTableIds)
    {
        using var command = connection.CreateCommand();
        // A function in a computed column or constraint cannot be deferred until ProgrammableObjects.
        command.CommandText = """
                              SELECT COUNT_BIG(*)
                              FROM sys.sql_expression_dependencies d
                              JOIN sys.objects source ON source.object_id = d.referencing_id
                              JOIN sys.objects target ON target.object_id = d.referenced_id
                              WHERE (source.type = 'U' OR (source.type IN ('C', 'D') AND source.parent_object_id IN (SELECT object_id FROM sys.tables)))
                                AND target.type IN ('V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'AF')
                                AND COALESCE(OBJECT_SCHEMA_NAME(source.object_id), '') <> 'sys'
                                AND source.object_id NOT IN (@firstJournal, @secondJournal)
                                AND source.parent_object_id NOT IN (@firstJournal, @secondJournal)
                              """;
        command.Parameters.AddWithValue("@firstJournal", journalTableIds.FirstOrDefault(-1));
        command.Parameters.AddWithValue("@secondJournal", journalTableIds.Skip(1).FirstOrDefault(-1));
        if ((long)command.ExecuteScalar()! != 0)
            throw new InvalidOperationException(
                "The structure depends on programmable objects that must run after the baseline. This schema cannot be exported as a structure-only baseline.");
    }

    internal static void ValidateSql(string sql)
    {
        new TSql180Parser(initialQuotedIdentifiers: true).Parse(new StringReader(sql), out var errors);
        if (errors.Count > 0)
            throw new InvalidOperationException("The generated baseline failed static SQL syntax validation.");
    }
}
