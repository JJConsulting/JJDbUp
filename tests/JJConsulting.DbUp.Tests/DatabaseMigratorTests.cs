using DbUp.Engine;
using JJConsulting.DbUp.Configuration;
using JJConsulting.DbUp.Exceptions;
using Microsoft.Data.SqlClient;

namespace JJConsulting.DbUp.Tests;

// One Testcontainers SQL Server per class; a separate database for each test.
public sealed partial class DatabaseMigratorTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>, IDisposable
{
    private readonly string databaseName = "JJMigrations_" + Guid.NewGuid().ToString("N");
    private string ConnectionString => new SqlConnectionStringBuilder(fixture.Container.GetConnectionString())
    {
        InitialCatalog = databaseName
    }.ConnectionString;

    private static DbUpOptions Options => new() { BaseNamespace = "TestScripts" };

    private static SqlScript InitialMigration => Migration("V001_CreateTables", """
        CREATE TABLE Items (Id int PRIMARY KEY, Name nvarchar(100) NOT NULL);
        CREATE TABLE Executions (Sequence int IDENTITY PRIMARY KEY, ObjectName nvarchar(100) NOT NULL);
        INSERT INTO Items VALUES (1, 'Order');
        """);

    [Fact]
    public void EmbeddedScripts_CreateTablesAndQueryableView()
    {
        DatabaseMigrator.Migrate(ConnectionString, Prepared(MigrationScriptService.GetScripts(typeof(DatabaseMigratorTests).Assembly, Options)));

        Assert.Equal("Order", Scalar<string>("SELECT Name FROM ItemNames"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM ProgrammableObjectVersions"));
    }

    [Fact]
    public void AppliedMigration_IsNotRunAgainEvenIfItsContentsChange()
    {
        Run(InitialMigration);
        var editedMigration = Migration("V001_CreateTables", "UPDATE Items SET Name = 'Must not run';");

        Run(editedMigration);

        Assert.Equal("Order", Scalar<string>("SELECT Name FROM Items"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
    }

    [Fact]
    public void NewMigrations_RunInNameOrderAndOnlyOnce()
    {
        Run(InitialMigration);
        var second = Migration("V002_Rename", "UPDATE Items SET Name = 'Second';");
        var third = Migration("V003_Append", "UPDATE Items SET Name = Name + ' Third';");

        Run(third, InitialMigration, second);
        Run(third, InitialMigration, second);

        Assert.Equal("Second Third", Scalar<string>("SELECT Name FROM Items"));
        Assert.Equal(3L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
    }

    [Fact]
    public void UnchangedObjects_AreSkippedAcrossSeparateRuns()
    {
        var view = View("ItemNames", "Name");
        Run(InitialMigration, view);
        var originalHash = Scalar<string>("SELECT ContentsHash FROM ProgrammableObjectVersions");

        Run(InitialMigration, view);

        Assert.Equal("Order", Scalar<string>("SELECT Name FROM ItemNames"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));
        Assert.Equal(originalHash, Scalar<string>("SELECT ContentsHash FROM ProgrammableObjectVersions"));
    }

    [Fact]
    public void ChangedObject_UpdatesItsDefinitionAndHashWithoutRunningOtherObjects()
    {
        var original = View("ItemNames", "Name");
        var unchanged = View("OtherNames", "Name");
        Run(InitialMigration, original, unchanged);
        var originalHash = HashFor("ItemNames");

        Run(InitialMigration, View("ItemNames", "upper(Name) AS Name"), unchanged);

        Assert.Equal("ORDER", Scalar<string>("SELECT Name FROM ItemNames"));
        Assert.Equal("Order", Scalar<string>("SELECT Name FROM OtherNames"));
        Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions WHERE ObjectName = 'ItemNames'"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions WHERE ObjectName = 'OtherNames'"));
        Assert.NotEqual(originalHash, HashFor("ItemNames"));
        Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM ProgrammableObjectVersions"));
    }

    [Fact]
    public void RevertingObjectToAnOlderDefinition_AppliesThatDefinitionAgain()
    {
        var original = View("ItemNames", "Name");
        Run(InitialMigration, original);
        Run(InitialMigration, View("ItemNames", "upper(Name) AS Name"));

        Run(InitialMigration, original);

        Assert.Equal("Order", Scalar<string>("SELECT Name FROM ItemNames"));
        Assert.Equal(3L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM ProgrammableObjectVersions"));
    }

    [Fact]
    public void ChangedProcedure_ChangesTheRowsReturnedByExecute()
    {
        var original = Object("GetItems", "CREATE OR ALTER PROCEDURE dbo.GetItems AS SELECT Name FROM Items;");
        Run(InitialMigration, original);
        Assert.Equal("Order", Scalar<string>("EXEC dbo.GetItems"));

        var changed = Object("GetItems", "CREATE OR ALTER PROCEDURE dbo.GetItems AS SELECT upper(Name) AS Name FROM Items;");
        Run(InitialMigration, changed);

        Assert.Equal("ORDER", Scalar<string>("EXEC dbo.GetItems"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM ProgrammableObjectVersions"));
    }

    [Fact]
    public void Dependencies_RunBeforeConsumersEvenWhenNamesSortTheOtherWay()
    {
        var consumer = Object("AConsumer", """
            -- #requires ZProvider
            CREATE OR ALTER VIEW ItemNames AS SELECT Name FROM SourceNames;
            GO
            INSERT INTO Executions (ObjectName) SELECT Name FROM ItemNames;
            """);
        var provider = Object("ZProvider", """
            CREATE OR ALTER VIEW SourceNames AS SELECT upper(Name) AS Name FROM Items;
            GO
            INSERT INTO Executions (ObjectName) VALUES ('provider');
            """);

        Run(InitialMigration, consumer, provider);

        Assert.Equal("ORDER", Scalar<string>("SELECT Name FROM ItemNames"));
        Assert.Equal("provider,ORDER",
            Scalar<string>(
                "SELECT STRING_AGG(ObjectName, ',') WITHIN GROUP (ORDER BY Sequence) FROM Executions"));
    }

    [Fact]
    public void SchemaChange_ReappliesAllObjectsEvenWhenTheirHashesHaveNotChanged()
    {
        var first = View("ItemNames", "Name");
        var second = View("OtherNames", "Name");
        Run(InitialMigration, first, second);
        var originalHash = HashFor("ItemNames");
        var newMigration = Migration("V002_AddStatus",
            "ALTER TABLE Items ADD Status nvarchar(20) NOT NULL DEFAULT 'Active';");

        Run(InitialMigration, newMigration, first, second);
        Run(InitialMigration, newMigration, first, second);

        Assert.Equal("Active", Scalar<string>("SELECT Status FROM Items"));
        Assert.Equal("Order", Scalar<string>("SELECT Name FROM ItemNames"));
        Assert.Equal(originalHash, HashFor("ItemNames"));
        Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions WHERE ObjectName = 'ItemNames'"));
        Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions WHERE ObjectName = 'OtherNames'"));
    }

    [Fact]
    public void Adoption_RecordsExistingMigrationWithoutExecutingItAndRunsRemainingMigrations()
    {
        global::DbUp.EnsureDatabase.For.SqlDatabase(ConnectionString);
        Execute(InitialMigration.Contents);
        var options = Options;
        options.ScriptsToMarkAsExecuted = ["V001_CreateTables.sql"];
        var pending = Migration("V002_Insert", "INSERT INTO Items VALUES (2, 'New');");

        DatabaseMigrator.Migrate(ConnectionString, Prepared(MigrationScriptService.GetScripts([InitialMigration, pending], options)));

        Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM Items"));
        Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
    }

    [Fact]
    public void FailedMigration_RollsBackTheEntireMigrationStageAndCanBeRetried()
    {
        Run(InitialMigration);
        var second = Migration("V002_Insert", "INSERT INTO Items VALUES (2, 'Rolled back');");
        var broken = Migration("V003_Broken", "INSERT INTO MissingTable VALUES (1);");

        var error = Assert.Throws<DatabaseMigrationException>(() => Run(InitialMigration, second, broken));

        Assert.Equal("Migrations", error.Stage);
        Assert.Contains("V003_Broken.sql", error.ScriptName);
        Assert.IsType<SqlException>(error.InnerException);
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM Items"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));

        Run(InitialMigration, second, Migration("V003_Broken", "UPDATE Items SET Name = 'Fixed' WHERE Id = 2;"));
        Assert.Equal("Fixed", Scalar<string>("SELECT Name FROM Items WHERE Id = 2"));
        Assert.Equal(3L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
    }

    [Fact]
    public void FailedObject_RollsBackObjectDefinitionsAndHashesButKeepsCommittedMigrations()
    {
        var view = View("ItemNames", "Name");
        Run(InitialMigration, view);
        var migration = Migration("V002_AddStatus", "ALTER TABLE Items ADD Status nvarchar(20) NOT NULL DEFAULT 'New';");
        var changedView = View("ItemNames", "upper(Name) AS Name");
        var broken = Object("ZBroken", "INSERT INTO MissingTable VALUES (1);");

        var error = Assert.Throws<DatabaseMigrationException>(() =>
            Run(InitialMigration, migration, changedView, broken));

        Assert.Equal("Programmable Objects", error.Stage);
        Assert.Equal("New", Scalar<string>("SELECT Status FROM Items"));
        Assert.Equal("Order", Scalar<string>("SELECT Name FROM ItemNames"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));
        Assert.Equal(0L, Scalar<long>("SELECT COUNT_BIG(*) FROM ProgrammableObjectVersions"));

        // The schema has already committed, but the cleared hashes force every object on retry.
        Run(InitialMigration, migration, changedView);
        Assert.Equal("ORDER", Scalar<string>("SELECT Name FROM ItemNames"));
        Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));
    }

    [Fact]
    public void FailedObjectWithoutSchemaChanges_PreservesThePreviouslyStoredHashForRetry()
    {
        var original = View("ItemNames", "Name");
        Run(InitialMigration, original);
        var previousHash = HashFor("ItemNames");
        var changed = View("ItemNames", "upper(Name) AS Name");

        Assert.Throws<DatabaseMigrationException>(() =>
            Run(InitialMigration, changed, Object("ZBroken", "INSERT INTO MissingTable VALUES (1);")));

        Assert.Equal("Order", Scalar<string>("SELECT Name FROM ItemNames"));
        Assert.Equal(previousHash, HashFor("ItemNames"));
        Run(InitialMigration, changed);
        Assert.Equal("ORDER", Scalar<string>("SELECT Name FROM ItemNames"));
    }

    [Fact]
    public void PreparedSettings_PreserveQuotedJournalNamesAcrossRuns()
    {
        var options = Options;
        options.MigrationJournal = "Migration \" history";
        options.ProgrammableObjectJournal = "Object history";
        var scripts = new[] { InitialMigration, View("ItemNames", "Name") };

        var prepared = Prepared(MigrationScriptService.GetScripts(scripts, options));
        options.MigrationJournal = "Changed after preparation";
        options.ProgrammableObjectJournal = "Changed after preparation";
        options.DependencyDirective = "";
        options.EnsureDatabase = false;

        DatabaseMigrator.Migrate(ConnectionString, prepared);
        DatabaseMigrator.Migrate(ConnectionString, prepared);

        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM \"Migration \"\" history\""));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM \"Object history\""));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));
    }

    private void Run(params SqlScript[] scripts) => DatabaseMigrator.Migrate(ConnectionString, Prepared(MigrationScriptService.GetScripts(scripts, Options)));
    private static MigrationScripts Prepared(MigrationScriptResult preparation)
    {
        Assert.True(preparation.IsValid, preparation.Error);
        return preparation.Scripts;
    }

    private static SqlScript Migration(string name, string sql) => new($"TestScripts.Migrations.{name}.sql", sql);
    private static SqlScript Object(string name, string sql) => new($"TestScripts.ProgrammableObjects.{name}.sql", sql);

    private static SqlScript View(string name, string projection) => Object(name, $"""
        CREATE OR ALTER VIEW {name} AS SELECT {projection} FROM Items;
        GO
        INSERT INTO Executions (ObjectName) VALUES ('{name}');
        """);

    private SqlConnection OpenConnection()
    {
        var connection = new SqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private T Scalar<T>(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string HashFor(string objectName) => Scalar<string>(
        $"SELECT ContentsHash FROM ProgrammableObjectVersions WHERE ScriptName = 'TestScripts.ProgrammableObjects.{objectName}.sql'");

    public void Dispose()
    {
        using var connection = new SqlConnection(fixture.Container.GetConnectionString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID('{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END";
        command.ExecuteNonQuery();
    }
}
