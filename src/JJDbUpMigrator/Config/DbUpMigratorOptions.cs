using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace JJDbUpMigrator.Config;

using System.Reflection;


public sealed class DbUpMigratorOptions
{
    public string ConnectionString { get; set; }

    public Assembly ScriptAssembly { get; set; }

    public string BaseNamespace { get; set; }

    public string Schema { get; set; } = "dbo";

    public string MigrationJournal { get; set; } =
        "SchemaVersions";

    public string ProgrammableObjectJournal { get; set; } =
        "ProgrammableObjectVersions";

    public string DependencyDirective { get; set; } =
        "#requires";

    public bool EnsureDatabase { get; set; } =
        true;

    public string MigrationsPrefix
    {
        get
        {
            return BaseNamespace + ".Migrations.";
        }
    }

    public string ProgrammableObjectsPrefix
    {
        get
        {
            return BaseNamespace + ".ProgrammableObjects.";
        }
    }
}
