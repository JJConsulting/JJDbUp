using System.Text.Json;
using DbUp.Engine.Output;
using JJConsulting.DbUp.Cli;
using JJConsulting.DbUp.Configuration;
using Microsoft.Data.SqlClient;

namespace JJConsulting.DbUp.Tests;

public sealed partial class DatabaseMigratorTests
{
    [Fact]
    public void Status_OnMissingDatabase_DoesNotCreateIt()
    {
        var status = DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts([InitialMigration, View("ItemNames", "Name")], Options)), new NoOpUpgradeLog());
        Assert.False(status.DatabaseExists);
        Assert.True(status.HasPendingChanges);
        using var connection = new SqlConnection(fixture.Container.GetConnectionString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@name)";
        command.Parameters.AddWithValue("@name", databaseName);
        Assert.IsType<DBNull>(command.ExecuteScalar());
    }

    [Fact]
    public void Status_OnDatabaseWithoutJournals_DoesNotCreateTables()
    {
        CreateEmptyDatabase();
        var status = DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts([InitialMigration, View("ItemNames", "Name")], Options)), new NoOpUpgradeLog());
        Assert.True(status.DatabaseExists);
        Assert.Single(status.PendingMigrations);
        Assert.Single(status.PendingObjects);
        Assert.Equal(0L, Scalar<long>("SELECT COUNT_BIG(*) FROM sys.tables"));
    }

    [Fact]
    public void Status_UsesHashesAndPredictsReapplicationWithoutChangingJournals()
    {
        var view = View("ItemNames", "Name");
        Run(InitialMigration, view);
        Assert.False(DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts([InitialMigration, view], Options)), new NoOpUpgradeLog()).HasPendingChanges);
        var changed = View("ItemNames", "upper(Name) AS Name");
        var status = DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts([InitialMigration, changed], Options)), new NoOpUpgradeLog());
        Assert.Empty(status.PendingMigrations);
        Assert.Equal("TestScripts.ProgrammableObjects.ItemNames.sql", Assert.Single(status.PendingObjects));
        var hash = HashFor("ItemNames");
        var pendingMigration = Migration("V002_AddStatus", "ALTER TABLE Items ADD Status int;");
        status = DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts([InitialMigration, pendingMigration, view], Options)), new NoOpUpgradeLog());
        Assert.Single(status.PendingMigrations);
        Assert.Single(status.PendingObjects);
        Assert.Equal(hash, HashFor("ItemNames"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));
    }

    [Fact]
    public void Adoption_IsIsolatedIdempotentAndValidatesBeforeCreatingDatabase()
    {
        var options = Options;
        options.ScriptsToMarkAsExecuted = ["Missing.sql"];
        Assert.False(MigrationScriptService.GetScripts([InitialMigration], options).IsValid);
        Assert.False(DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts([InitialMigration], Options)), new NoOpUpgradeLog()).DatabaseExists);
        options.ScriptsToMarkAsExecuted = ["V001_CreateTables.sql"];
        var scripts = new[] { InitialMigration, Migration("V002_Broken", "THROW 50000, 'must not run', 1;"), View("ItemNames", "Name") };
        DatabaseMigrator.MarkAsExecuted(ConnectionString, Prepared(MigrationScriptService.GetScripts(scripts, options)), new NoOpUpgradeLog());
        DatabaseMigrator.MarkAsExecuted(ConnectionString, Prepared(MigrationScriptService.GetScripts(scripts, options)), new NoOpUpgradeLog());
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
        Assert.Equal(0L, Scalar<long>("SELECT COUNT_BIG(*) FROM sys.tables WHERE name IN ('Items', 'ProgrammableObjectVersions')"));
    }

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
                var code = CliApplication.Run([command, "--scripts", root, "--base-namespace", "TestScripts",
                    "--connection-string", ConnectionString, "--output", "json", .. extra], output, error, root, []);
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
            "--base-namespace", "TestScripts", "--connection-string", ConnectionString], output, error, environment: []);
        Assert.True(code == 0, output.ToString() + error.ToString());
        Assert.Equal("Order", Scalar<string>("SELECT Name FROM ItemNames"));
    }

    [Fact]
    public void Status_WithContainedUser_DoesNotRequireAccessToMaster()
    {
        CreateEmptyDatabase();
        using (var admin = new SqlConnection(fixture.Container.GetConnectionString()))
        {
            admin.Open();
            using var command = admin.CreateCommand();
            command.CommandText = $"""
                EXEC sp_configure 'contained database authentication', 1;
                RECONFIGURE;
                ALTER DATABASE [{databaseName}] SET CONTAINMENT = PARTIAL;
                """;
            command.ExecuteNonQuery();
        }
        using (var admin = new SqlConnection(ConnectionString))
        {
            admin.Open();
            using var command = admin.CreateCommand();
            command.CommandText = """
                CREATE USER ContainedUser WITH PASSWORD = 'TestOnly#123456789!';
                ALTER ROLE db_owner ADD MEMBER ContainedUser;
                """;
            command.ExecuteNonQuery();
        }

        var settings = new SqlConnectionStringBuilder(ConnectionString)
        {
            UserID = "ContainedUser", Password = "TestOnly#123456789!", ConnectTimeout = 2, Pooling = false
        };
        var options = Options;
        options.EnsureDatabase = false;
        var scripts = Prepared(MigrationScriptService.GetScripts([InitialMigration], options));
        DatabaseMigrator.Migrate(settings.ConnectionString, scripts, new NoOpUpgradeLog());

        var status = DatabaseMigrator.GetStatus(settings.ConnectionString, scripts, new NoOpUpgradeLog());

        Assert.True(status.DatabaseExists);
        Assert.False(status.HasPendingChanges);
        settings.InitialCatalog = "master";
        using var master = new SqlConnection(settings.ConnectionString);
        Assert.Throws<SqlException>(() => master.Open());
    }

    [Fact]
    public void Status_DoesNotTreatHiddenInaccessibleDatabaseAsMissing()
    {
        CreateEmptyDatabase();
        var login = "jjdbup_" + Guid.NewGuid().ToString("N");
        using var admin = new SqlConnection(fixture.Container.GetConnectionString());
        admin.Open();
        using var command = admin.CreateCommand();
        command.CommandText = $"CREATE LOGIN [{login}] WITH PASSWORD = 'TestOnly#123456789!'; DENY VIEW ANY DATABASE TO [{login}];";
        command.ExecuteNonQuery();
        try
        {
            var restricted = new SqlConnectionStringBuilder(ConnectionString)
            {
                UserID = login, Password = "TestOnly#123456789!", ConnectTimeout = 2, Pooling = false
            }.ConnectionString;
            Assert.Throws<SqlException>(() => DatabaseMigrator.GetStatus(restricted, Prepared(MigrationScriptService.GetScripts([InitialMigration], Options)), new NoOpUpgradeLog()));
        }
        finally
        {
            command.CommandText = $"DROP LOGIN [{login}]";
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public void AssemblyPreparation_IgnoresScriptsPathAndPreservesJournalNames()
    {
        var assembly = typeof(DatabaseMigratorTests).Assembly;
        DatabaseMigrator.Migrate(ConnectionString, Prepared(MigrationScriptService.GetScripts(assembly, new DbUpOptions { BaseNamespace = "TestScripts" })));
        var options = Options;
        options.ScriptsPath = "missing-directory-must-not-be-used";

        Assert.False(DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts(assembly, options)), new NoOpUpgradeLog()).HasPendingChanges);
        DatabaseMigrator.Migrate(ConnectionString, Prepared(MigrationScriptService.GetScripts(assembly, options)), new NoOpUpgradeLog());
        Assert.Equal("TestScripts.Migrations.V001_CreateTable.sql", Scalar<string>("SELECT ScriptName FROM SchemaVersions"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
        Assert.Equal("Order", Scalar<string>("SELECT Name FROM ItemNames"));
    }

    [Fact]
    public void ConfiguredFolder_PreservesExistingNamespaceAndAdoptionRules()
    {
        var root = Path.Combine(Path.GetTempPath(), "jjdbup-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Migrations"));
        File.WriteAllText(Path.Combine(root, "Migrations", "V001_CreateTables.sql"), InitialMigration.Contents);
        var options = new DbUpOptions { ScriptsPath = root, BaseNamespace = "TestScripts" };
        try
        {
            DatabaseMigrator.Migrate(ConnectionString, Prepared(MigrationScriptService.GetScripts(options)), new NoOpUpgradeLog());
            Assert.False(DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts(options)), new NoOpUpgradeLog()).HasPendingChanges);
            Assert.Equal("TestScripts.Migrations.V001_CreateTables.sql", Scalar<string>("SELECT ScriptName FROM SchemaVersions"));

            File.WriteAllText(Path.Combine(root, "Migrations", "V002_Existing.sql"), "THROW 50000, 'must not run', 1;");
            options.ScriptsToMarkAsExecuted = ["V002_Existing.sql"];
            DatabaseMigrator.MarkAsExecuted(ConnectionString, Prepared(MigrationScriptService.GetScripts(options)), new NoOpUpgradeLog());
            Assert.False(DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts(options)), new NoOpUpgradeLog()).HasPendingChanges);
            Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
        }
        finally { Directory.Delete(root, true); }
    }

    private void CreateEmptyDatabase()
    {
        using var connection = new SqlConnection(fixture.Container.GetConnectionString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}]";
        command.ExecuteNonQuery();
    }
}
