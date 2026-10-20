using DbUp.Engine.Output;
using Microsoft.Data.SqlClient;

namespace JJConsulting.DbUp.Tests;

public sealed partial class DatabaseMigratorTests
{
    [Fact]
    public void Generation_RoundTripsStructureAndReappliesSeparateProgrammableObjects()
    {
        var first = Migration("V001_Structure", """
            CREATE SCHEMA app;
            GO
            CREATE TYPE app.Code FROM nvarchar(25) NOT NULL;
            GO
            CREATE SEQUENCE app.ItemNumbers AS bigint START WITH 10;
            CREATE TABLE app.Parent (Id int PRIMARY KEY, Name app.Code);
            CREATE TABLE app.Child (Id bigint PRIMARY KEY DEFAULT NEXT VALUE FOR app.ItemNumbers,
              ParentId int NOT NULL, CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES app.Parent(Id));
            INSERT INTO app.Parent VALUES (1, 'seed-excluded');
            INSERT INTO app.Child (ParentId) VALUES (1);
            """);
        var fifth = Migration("V005_Status", """
            ALTER TABLE app.Parent ADD Status int NOT NULL CONSTRAINT DF_Parent_Status DEFAULT 0;
            CREATE INDEX IX_Child_Parent ON app.Child(ParentId);
            """);
        var sixth = Migration("V006_Later", "ALTER TABLE app.Child ADD LaterValue int;");
        var function = Object("ZFunction", "CREATE OR ALTER FUNCTION app.Value() RETURNS int AS BEGIN RETURN 1; END;");
        var view = Object("AView", "-- #requires ZFunction\nCREATE OR ALTER VIEW app.Summary AS SELECT Id, app.Value() AS Value FROM app.Parent;");
        var procedure = Object("Procedure", "-- #requires AView\nCREATE OR ALTER PROCEDURE app.ReadSummary AS SELECT * FROM app.Summary;");
        var trigger = Object("Trigger", "CREATE OR ALTER TRIGGER app.ParentChanged ON app.Parent AFTER INSERT AS BEGIN SET NOCOUNT ON; END;");
        Run(first, fifth, function, view, procedure, trigger);
        var result = BaselineService.Generate(ConnectionString, Options, "B005_Initial");
        Assert.Equal("B005_Initial.sql", result.ScriptName);
        Assert.DoesNotContain("seed-excluded", result.Sql);
        Assert.DoesNotContain("SchemaVersions", result.Sql);
        Assert.DoesNotContain("ProgrammableObjectVersions", result.Sql);
        Assert.DoesNotContain("CREATE VIEW", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE PROCEDURE", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE FUNCTION", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TRIGGER", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GO", result.Sql);
        Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM app.Parent"));
        Assert.Equal(2L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));

        var target = new DatabaseMigratorTests(fixture);
        target.Run(Migration("B005_Initial", result.Sql), sixth, function, view, procedure, trigger);
        Assert.Equal(0L, target.Scalar<long>("SELECT COUNT_BIG(*) FROM app.Parent"));
        Assert.Equal(1L, target.Scalar<long>("SELECT COUNT_BIG(*) FROM sys.foreign_keys WHERE name = 'FK_Child_Parent'"));
        Assert.Equal(1L, target.Scalar<long>("SELECT COUNT_BIG(*) FROM sys.indexes WHERE name = 'IX_Child_Parent'"));
        Assert.Equal(4L, target.Scalar<long>("SELECT COUNT_BIG(*) FROM ProgrammableObjectVersions"));
        Assert.Equal(2L, target.Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
        target.Execute("INSERT INTO app.Parent (Id, Name) VALUES (1, 'new'); INSERT INTO app.Child (ParentId) VALUES (1);");
        Assert.Equal(10L, target.Scalar<long>("SELECT Id FROM app.Child"));
        Assert.Equal(1, target.Scalar<int>("SELECT Value FROM app.Summary"));
    }

    [Fact]
    public void Generation_RejectsStructureDependingOnDeferredFunction()
    {
        Run(Migration("V001_Dependent", """
            CREATE FUNCTION dbo.CheckValue(@value int) RETURNS int AS BEGIN RETURN @value; END;
            GO
            CREATE TABLE dbo.Dependent (Id int, ComputedValue AS dbo.CheckValue(Id));
            """));
        var exception = Assert.Throws<InvalidOperationException>(() => BaselineService.Generate(ConnectionString, Options, "B001"));
        Assert.Contains("programmable objects", exception.Message);
    }

    [Fact]
    public void Generation_HandlesCyclicForeignKeysAndCustomJournals()
    {
        var options = Options;
        options.Schema = "tracking";
        options.MigrationJournal = "Migration \" history";
        options.ProgrammableObjectJournal = "Object history";
        // DbUp creates its journal before executing the first script; provision a custom schema first.
        CreateEmptyDatabase();
        Execute("CREATE SCHEMA tracking;");
        var first = Migration("V001_Structure", """
            CREATE TABLE dbo.A (Id int PRIMARY KEY, BId int NULL);
            CREATE TABLE dbo.B (Id int PRIMARY KEY, AId int NULL,
                CONSTRAINT FK_B_A FOREIGN KEY(AId) REFERENCES dbo.A(Id));
            ALTER TABLE dbo.A ADD CONSTRAINT FK_A_B FOREIGN KEY(BId) REFERENCES dbo.B(Id);
            """);
        var prepared = Prepared(MigrationScriptService.GetScripts([first], options));
        DatabaseMigrator.Migrate(ConnectionString, prepared, options, new NoOpUpgradeLog());
        var result = BaselineService.Generate(ConnectionString, options, "B001");
        Assert.DoesNotContain("Migration \" history", result.Sql);
        Assert.DoesNotContain("Object history", result.Sql);
        var target = new DatabaseMigratorTests(fixture);
        target.Run(Migration("B001", result.Sql));
        Assert.Equal(2L, target.Scalar<long>("SELECT COUNT_BIG(*) FROM sys.foreign_keys WHERE name IN ('FK_A_B', 'FK_B_A')"));
    }

    [Fact]
    public void Generation_MissingDatabaseIsNeverCreated()
    {
        var prepared = Prepared(MigrationScriptService.GetScripts([InitialMigration], Options));
        Assert.Throws<SqlException>(() => BaselineService.Generate(ConnectionString, Options, "B001"));
        Assert.False(DatabaseMigrator.GetStatus(ConnectionString, prepared, Options, new NoOpUpgradeLog()).DatabaseExists);
    }

    [Fact]
    public void Generation_RequiresDatabaseDefinitionVisibility()
    {
        Run(InitialMigration);
        var login = "baseline_reader_" + Guid.NewGuid().ToString("N");
        using var admin = new SqlConnection(fixture.Container.GetConnectionString());
        admin.Open();
        using var command = admin.CreateCommand();
        command.CommandText = $"CREATE LOGIN [{login}] WITH PASSWORD = 'TestOnly#123456789!';";
        command.ExecuteNonQuery();
        try
        {
            Execute($"CREATE USER [{login}] FOR LOGIN [{login}]; GRANT SELECT ON dbo.SchemaVersions TO [{login}];");
            var restricted = new SqlConnectionStringBuilder(ConnectionString)
            {
                UserID = login, Password = "TestOnly#123456789!", Pooling = false
            }.ConnectionString;
            var exception = Assert.Throws<InvalidOperationException>(() => BaselineService.Generate(restricted, Options, "B001"));
            Assert.Contains("VIEW DEFINITION", exception.Message);
            Assert.Equal(1L, Scalar<long>("SELECT COUNT_BIG(*) FROM SchemaVersions"));
        }
        finally
        {
            command.CommandText = $"DROP LOGIN [{login}];";
            command.ExecuteNonQuery();
        }
    }
}
