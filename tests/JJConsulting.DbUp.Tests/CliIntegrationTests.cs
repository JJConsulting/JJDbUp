using System.Text.Json;
using JJConsulting.DbUp.Cli;

namespace JJConsulting.DbUp.Tests;

public sealed partial class DatabaseMigratorTests
{
    [Fact]
    public void Cli_EndToEnd_FolderMigrationsStatusObjectsAndAdoption()
    {
        var root = Path.Combine(Path.GetTempPath(), "jjdbup-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Migrations"));
        Directory.CreateDirectory(Path.Combine(root, "ProgrammableObjects"));
        var migration = Path.Combine(root, "Migrations", "V001_CreateTables.sql");
        var view = Path.Combine(root, "ProgrammableObjects", "ItemNames.sql");
        File.WriteAllText(migration, InitialMigration.Contents);
        File.WriteAllText(view, View("ItemNames", "Name").Contents);
        try
        {
            (int Code, JsonDocument Json, string Error) Cli(string command, params string[] extra)
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                var code = CliApplication.Run([command, "--scripts", root, "--connection-string", ConnectionString, "--output", "json", .. extra], output, error, root, []);
                Assert.DoesNotContain(ConnectionString, output.ToString() + error);
                return (code, JsonDocument.Parse(output.ToString()), error.ToString());
            }
            var pending = Cli("status", "--check");
            using (pending.Json) Assert.Equal(3, pending.Code);
            var first = Cli("migrate");
            using (first.Json) Assert.True(first.Code == 0, first.Json.RootElement.ToString());
            var again = Cli("migrate");
            using (again.Json) Assert.Equal(0, again.Code);
            var current = Cli("status", "--check");
            using (current.Json) Assert.Equal(0, current.Code);
            Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));
            File.WriteAllText(view, View("ItemNames", "upper(Name) AS Name").Contents);
            var changed = Cli("migrate");
            using (changed.Json) Assert.Equal(0, changed.Code);
            Assert.Equal("ORDER", Scalar<string>("SELECT Name FROM ItemNames"));
            File.WriteAllText(Path.Combine(root, "Migrations", "V002_Adopt.sql"), "THROW 50000, 'must not run', 1;");
            var adopted = Cli("mark-executed", "--script", "V002_Adopt.sql", "--script", "V001_CreateTables.sql");
            using (adopted.Json) Assert.Equal(0, adopted.Code);
            Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));
            Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
            File.WriteAllText(Path.Combine(root, "Migrations", "V003_AddStatus.sql"), "ALTER TABLE Items ADD Status int;");
            var schema = Cli("migrate");
            using (schema.Json) Assert.Equal(0, schema.Code);
            Assert.Equal(3L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));
            File.WriteAllText(Path.Combine(root, "ProgrammableObjects", "Broken.sql"), "SELECT * FROM MissingTable;");
            var brokenObject = Cli("migrate");
            using (brokenObject.Json)
            {
                Assert.Equal(1, brokenObject.Code);
                Assert.Contains("Programmable Objects", brokenObject.Json.RootElement.GetProperty("error").GetString());
            }
            File.Delete(Path.Combine(root, "ProgrammableObjects", "Broken.sql"));
            File.WriteAllText(Path.Combine(root, "Migrations", "V004_Broken.sql"), "SELECT * FROM MissingTable;");
            var brokenMigration = Cli("migrate");
            using (brokenMigration.Json)
            {
                Assert.Equal(1, brokenMigration.Code);
                Assert.Contains("Migrations", brokenMigration.Json.RootElement.GetProperty("error").GetString());
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Cli_AssemblySource_ExecutesEmbeddedScripts()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliApplication.Run(["migrate", "--assembly", typeof(DatabaseMigratorTests).Assembly.Location,
            "--connection-string", ConnectionString], output, error, environment: []);
        Assert.True(code == 0, output.ToString() + error.ToString());
        Assert.Equal("Order", Scalar<string>("SELECT Name FROM ItemNames"));
    }

    [Fact]
    public void Cli_Baseline_CreatesFirstMigrationWithoutExistingScriptsOrHistory()
    {
        CreateEmptyDatabase();
        Execute("CREATE TABLE dbo.ManualTable (Id int);");
        var root = Path.Combine(Path.GetTempPath(), "jjdbup-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = CliApplication.Run(["baseline", "InitialSchema", "--scripts", root,
                "--connection-string", ConnectionString, "--output", "json"], output, error,
                Path.GetTempPath(), []);
            Assert.True(code == 0, output.ToString() + error);
            Assert.Contains("ManualTable", File.ReadAllText(Path.Combine(root, "Migrations", "InitialSchema.sql")));
            Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM sys.tables"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Cli_BaselineDryRunAndSave_LeaveDatabaseAndExistingFilesUnchanged()
    {
        Run(InitialMigration);
        var root = Path.Combine(Path.GetTempPath(), "jjdbup-baseline-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Migrations"));
        Directory.CreateDirectory(Path.Combine(root, "ProgrammableObjects"));
        var first = Path.Combine(root, "Migrations", "V001_CreateTables.sql");
        File.WriteAllText(first, InitialMigration.Contents);
        File.WriteAllText(Path.Combine(root, "Migrations", "V002_Later.sql"), "ALTER TABLE dbo.Items ADD Status int;");
        var programmable = Path.Combine(root, "ProgrammableObjects", "Example.sql");
        File.WriteAllText(programmable, "CREATE OR ALTER VIEW dbo.Example AS SELECT * FROM dbo.Items;");
        try
        {
            foreach (var flag in new[] { "--dry-run", "--dry", "" })
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                string[] args = ["baseline", "B001_Initial.sql", "--scripts", root, "--connection-string", ConnectionString, "--output", "json"];
                var code = CliApplication.Run(flag.Length == 0 ? args : [.. args, flag], output, error, root, []);
                Assert.True(code == 0, output.ToString() + error);
                using var json = JsonDocument.Parse(output.ToString());
                Assert.Equal("baseline", json.RootElement.GetProperty("command").GetString());
                var data = json.RootElement.GetProperty("data");
                Assert.Equal(flag.Length != 0, data.GetProperty("dryRun").GetBoolean());
                Assert.True(File.Exists(first));
                Assert.Equal(flag.Length == 0, File.Exists(Path.Combine(root, "Migrations", "B001_Initial.sql")));
            }
            Assert.True(File.Exists(programmable));
            Assert.True(File.Exists(Path.Combine(root, "Migrations", "V002_Later.sql")));
            Assert.Equal("Order", Scalar<string>("SELECT Name FROM Items"));
            Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
        }
        finally { Directory.Delete(root, true); }
    }
}
