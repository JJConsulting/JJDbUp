using DbUp.Engine;
using DbUp.Engine.Output;
using JJConsulting.DbUp.Configuration;
using JJConsulting.DbUp.Exceptions;
using Microsoft.Data.SqlClient;

namespace JJConsulting.DbUp.Tests;

// One Testcontainers SQL Server per class; isolated databases are cleaned up by the fixture.
public sealed partial class DatabaseMigratorTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private string ConnectionString { get; } = fixture.CreateDatabaseConnectionString();
    private string DatabaseName => new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

    private static DbUpOptions Options => new();

    private static SqlScript InitialMigration => Migration("V001_CreateTables", """
        CREATE TABLE Items (Id int PRIMARY KEY, Name nvarchar(100) NOT NULL);
        CREATE TABLE Executions (Sequence int IDENTITY PRIMARY KEY, ObjectName nvarchar(100) NOT NULL);
        INSERT INTO Items VALUES (1, 'Order');
        """);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmbeddedScripts_LegacyOverloadsCreateTablesAndSkipAppliedScripts(bool useOptions)
    {
        for (var run = 0; run < 2; run++)
        {
            if (useOptions)
                DatabaseMigrator.Migrate(ConnectionString, typeof(DatabaseMigratorTests).Assembly, Options);
            else
                DatabaseMigrator.Migrate(ConnectionString, typeof(DatabaseMigratorTests).Assembly);
        }

        Assert.Equal("Order", Scalar<string>("SELECT Name FROM ItemNames"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM ProgrammableObjectVersions"));
    }

    [Fact]
    public void ExplicitScripts_LegacyOverloadAppliesMigrationsAndObjects()
    {
        SqlScript[] scripts = [InitialMigration, View("ItemNames", "Name")];

        DatabaseMigrator.Migrate(ConnectionString, scripts, Options);
        DatabaseMigrator.Migrate(ConnectionString, scripts, Options);

        Assert.Equal("Order", Scalar<string>("SELECT Name FROM ItemNames"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
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

    [Theory]
    [InlineData("V002_Rename")]
    [InlineData("B005_Baseline")]
    public void NewMigrations_RunInNameOrderAndOnlyOnce(string secondName)
    {
        Run(InitialMigration);
        var second = Migration(secondName, "UPDATE Items SET Name = 'Second';");
        var third = Migration("V003_Append", "UPDATE Items SET Name = Name + ' Third';");

        Run(third, InitialMigration, second);
        Run(third, InitialMigration, second);

        Assert.Equal("Second Third", Scalar<string>("SELECT Name FROM Items"));
        Assert.Equal(3L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
    }

    [Fact]
    public void Objects_SkipUnchangedDefinitionsAndApplyOnlyChangedOnes()
    {
        var original = View("ItemNames", "Name");
        var unchanged = View("OtherNames", "Name");
        Run(InitialMigration, original, unchanged);
        var originalHash = HashFor("ItemNames");

        Run(InitialMigration, original, unchanged);
        Assert.Equal(originalHash, HashFor("ItemNames"));
        Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));

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

    [Theory]
    [InlineData("V001_CreateTables")]
    [InlineData("InitialSchema")]
    public void Adoption_RecordsExistingMigrationWithoutExecutingItAndRunsRemainingMigrations(string name)
    {
        global::DbUp.EnsureDatabase.For.SqlDatabase(ConnectionString);
        Execute(InitialMigration.Contents);
        var options = Options;
        options.ScriptsToMarkAsExecuted = [name + ".sql"];
        var pending = Migration("V002_Insert", "INSERT INTO Items VALUES (2, 'New');");

        DatabaseMigrator.Migrate(ConnectionString, Prepared(MigrationScriptService.GetScripts([Migration(name, InitialMigration.Contents), pending], options)), options);

        Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM Items"));
        Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
    }

    [Theory]
    [InlineData("V002_Insert", "V003_Broken")]
    [InlineData("B004_Insert", "B005_Broken")]
    public void FailedMigration_RollsBackTheEntireMigrationStageAndCanBeRetried(string secondName, string brokenName)
    {
        Run(InitialMigration);
        var second = Migration(secondName, "CREATE TABLE dbo.Pending (Id int); INSERT INTO Items VALUES (2, 'Rolled back');");
        var broken = Migration(brokenName, "INSERT INTO MissingTable VALUES (1);");

        var error = Assert.Throws<DatabaseMigrationException>(() => Run(InitialMigration, second, broken));

        Assert.Equal(0L, Scalar<long>("SELECT COUNT_BIG(*) FROM sys.tables WHERE name = 'Pending'"));
        Assert.Equal("Migrations", error.Stage);
        Assert.Contains(brokenName + ".sql", error.ScriptName);
        Assert.IsType<SqlException>(error.InnerException);
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM Items"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));

        Run(InitialMigration, second, Migration(brokenName, "UPDATE Items SET Name = 'Fixed' WHERE Id = 2;"));
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
    public void ExplicitSettings_UseQuotedJournalNamesAcrossRuns()
    {
        var options = Options;
        options.MigrationJournal = "Migration \" history";
        options.ProgrammableObjectJournal = "Object history";
        var scripts = new[] { InitialMigration, View("ItemNames", "Name") };

        var prepared = Prepared(MigrationScriptService.GetScripts(scripts, Options));

        DatabaseMigrator.Migrate(ConnectionString, prepared, options);
        DatabaseMigrator.Migrate(ConnectionString, prepared, options);

        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM \"Migration \"\" history\""));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM \"Object history\""));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));
    }

    [Fact]
    public void Status_OnMissingDatabase_DoesNotCreateIt()
    {
        var status = DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts([InitialMigration, View("ItemNames", "Name")], Options)), Options, new NoOpUpgradeLog());
        Assert.False(status.DatabaseExists);
        Assert.True(status.HasPendingChanges);
        using var connection = new SqlConnection(fixture.Container.GetConnectionString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@name)";
        command.Parameters.AddWithValue("@name", DatabaseName);
        Assert.IsType<DBNull>(command.ExecuteScalar());
    }

    [Fact]
    public void Status_OnDatabaseWithoutJournals_DoesNotCreateTables()
    {
        CreateEmptyDatabase();
        var status = DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts([InitialMigration, View("ItemNames", "Name")], Options)), Options, new NoOpUpgradeLog());
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
        Assert.False(DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts([InitialMigration, view], Options)), Options, new NoOpUpgradeLog()).HasPendingChanges);
        var changed = View("ItemNames", "upper(Name) AS Name");
        var status = DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts([InitialMigration, changed], Options)), Options, new NoOpUpgradeLog());
        Assert.Empty(status.PendingMigrations);
        Assert.Equal("TestScripts.ProgrammableObjects.ItemNames.sql", Assert.Single(status.PendingObjects));
        var hash = HashFor("ItemNames");
        var pendingMigration = Migration("V002_AddStatus", "ALTER TABLE Items ADD Status int;");
        status = DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts([InitialMigration, pendingMigration, view], Options)), Options, new NoOpUpgradeLog());
        Assert.Single(status.PendingMigrations);
        Assert.Single(status.PendingObjects);
        Assert.Equal(hash, HashFor("ItemNames"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM Executions"));
    }

    [Theory]
    [InlineData("V001_CreateTables")]
    [InlineData("InitialSchema")]
    public void Adoption_OnlyRecordsSelectedMigrationsAndIsIdempotent(string name)
    {
        var options = Options;
        options.ScriptsToMarkAsExecuted = [name + ".sql"];
        var scripts = new[] { Migration(name, InitialMigration.Contents), Migration("V002_Broken", "THROW 50000, 'must not run', 1;"), View("ItemNames", "Name") };
        DatabaseMigrator.MarkAsExecuted(ConnectionString, Prepared(MigrationScriptService.GetScripts(scripts, options)), options, new NoOpUpgradeLog());
        DatabaseMigrator.MarkAsExecuted(ConnectionString, Prepared(MigrationScriptService.GetScripts(scripts, options)), options, new NoOpUpgradeLog());
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
        Assert.Equal(0L, Scalar<long>("SELECT COUNT_BIG(*) FROM sys.tables WHERE name IN ('Items', 'ProgrammableObjectVersions')"));
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
                ALTER DATABASE [{DatabaseName}] SET CONTAINMENT = PARTIAL;
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
        DatabaseMigrator.Migrate(settings.ConnectionString, scripts, options, new NoOpUpgradeLog());

        var status = DatabaseMigrator.GetStatus(settings.ConnectionString, scripts, options, new NoOpUpgradeLog());

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
            Assert.Throws<SqlException>(() => DatabaseMigrator.GetStatus(restricted, Prepared(MigrationScriptService.GetScripts([InitialMigration], Options)), Options, new NoOpUpgradeLog()));
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
        DatabaseMigrator.Migrate(ConnectionString, Prepared(MigrationScriptService.GetScripts(assembly, new DbUpOptions())), Options);
        var options = Options;
        options.ScriptsPath = "missing-directory-must-not-be-used";

        Assert.False(DatabaseMigrator.GetStatus(ConnectionString, Prepared(MigrationScriptService.GetScripts(assembly, options)), options, new NoOpUpgradeLog()).HasPendingChanges);
        DatabaseMigrator.Migrate(ConnectionString, Prepared(MigrationScriptService.GetScripts(assembly, options)), options, new NoOpUpgradeLog());
        Assert.Equal("TestScripts.Migrations.V001_CreateTable.sql", Scalar<string>("SELECT ScriptName FROM SchemaVersions"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
        Assert.Equal("Order", Scalar<string>("SELECT Name FROM ItemNames"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM ProgrammableObjectVersions"));
    }

    private void CreateEmptyDatabase()
    {
        using var connection = new SqlConnection(fixture.Container.GetConnectionString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{DatabaseName}]";
        command.ExecuteNonQuery();
    }

    private void Run(params SqlScript[] scripts) => DatabaseMigrator.Migrate(ConnectionString, Prepared(MigrationScriptService.GetScripts(scripts, Options)), Options);
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
}
