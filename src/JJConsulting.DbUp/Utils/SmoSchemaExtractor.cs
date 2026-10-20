using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;

namespace JJConsulting.DbUp.Utils;

/// <summary>Scripts structure objects with SMO, excluding journals and programmable objects.</summary>
internal static class SmoSchemaExtractor
{
    internal static string Extract(SqlConnection connection, IReadOnlySet<int> journalTableIds)
    {
        var serverConnection = new ServerConnection(connection);
        var server = new Server(serverConnection);
        var database = server.Databases[connection.Database]
                       ?? throw new InvalidOperationException("The source database is not accessible.");
        var roots = CollectStructureObjects(database, journalTableIds);

        var scripter = new Scripter(server)
        {
            Options = new ScriptingOptions
            {
                ScriptSchema = true,
                ScriptData = false,
                ScriptDrops = false,
                WithDependencies = false,
                Indexes = true,
                DriAll = true,
                Triggers = false,
                SchemaQualify = true,
                SchemaQualifyForeignKeysReferences = true,
                IncludeDatabaseContext = false,
                IncludeHeaders = false,
                ScriptOwner = false,
                Permissions = false,
                NoFileGroup = true,
                ContinueScriptingOnError = false
            }
        };
        var sql = new StringBuilder("-- Schema baseline. Data, journals and programmable objects are excluded.\n");
        if (roots.Count > 0)
        {
            var ordered = OrderStructureDependencies(scripter, roots);
            foreach (var batch in scripter.Script(ordered).Cast<string>())
            {
                sql.AppendLine(batch.TrimEnd());
                sql.AppendLine("GO");
            }
        }

        return sql.ToString();
    }

    private static List<Urn> CollectStructureObjects(Database database, IReadOnlySet<int> journalTableIds)
    {
        var roots = new List<Urn>();
        foreach (var schema in database.Schemas)
        {
            if (schema.Name is not ("dbo" or "guest" or "sys" or "INFORMATION_SCHEMA" or
                "db_owner" or "db_accessadmin" or "db_securityadmin" or "db_ddladmin" or "db_backupoperator" or
                "db_datareader" or "db_datawriter" or "db_denydatareader" or "db_denydatawriter"))
                roots.Add(schema.Urn);
        }

        foreach (var type in database.UserDefinedDataTypes)
        {
            roots.Add(type.Urn);
        }

        foreach (var type in database.UserDefinedTableTypes)
        {
            roots.Add(type.Urn);
        }

        foreach (var type in database.UserDefinedTypes)
        {
            roots.Add(type.Urn);
        }

        foreach (var sequence in database.Sequences)
        {
            roots.Add(sequence.Urn);
        }

        foreach (var schema in database.XmlSchemaCollections)
        {
            roots.Add(schema.Urn);
        }

        foreach (var function in database.PartitionFunctions)
        {
            roots.Add(function.Urn);
        }

        foreach (var scheme in database.PartitionSchemes)
        {
            roots.Add(scheme.Urn);
        }

        foreach (var assembly in database.Assemblies)
        {
            if (!assembly.IsSystemObject)
                roots.Add(assembly.Urn);
        }

        foreach (var table in database.Tables)
        {
            if (!table.IsSystemObject && !journalTableIds.Contains(table.ID))
                roots.Add(table.Urn);
        }

        return roots;
    }

    private static Urn[] OrderStructureDependencies(Scripter scripter, List<Urn> roots)
    {
        // Walk dependencies, but keep only structure roots: never pull in modules or journal tables.
        // SMO dependency discovery does not accept schemas; create those before their contents.
        var schemas = roots.Where(urn => urn.Type == "Schema")
            .OrderBy(urn => urn.ToString(), StringComparer.Ordinal)
            .ToArray();
        var structureObjects = roots.Where(urn => urn.Type != "Schema")
            .OrderBy(urn => urn.ToString(), StringComparer.Ordinal)
            .ToArray();

        var allowedObjectNames = structureObjects.Select(urn => urn.ToString()).ToHashSet(StringComparer.Ordinal);
        Urn[] dependencyOrder = [];
        if (structureObjects.Length > 0)
        {
            var dependencies = scripter.DiscoverDependencies(structureObjects, true);
            dependencyOrder = scripter.WalkDependencies(dependencies)
                .Select(node => node.Urn)
                .DistinctBy(urn => urn.ToString())
                .ToArray();
        }

        if (dependencyOrder.Any(urn => !allowedObjectNames.Contains(urn.ToString())))
            throw new InvalidOperationException(
                "The structure requires an excluded or unsupported object and cannot be exported as a standalone baseline.");

        return schemas.Concat(dependencyOrder).ToArray();
    }
}
