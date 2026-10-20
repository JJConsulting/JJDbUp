using DbUp.Engine;
using JJConsulting.DbUp.Configuration;

namespace JJConsulting.DbUp.Tests;

public sealed class ScriptTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jjdbup-sources-" + Guid.NewGuid().ToString("N"));

    public ScriptTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Migrations"));
        Directory.CreateDirectory(Path.Combine(_directory, "ProgrammableObjects", "Views"));
        File.WriteAllText(Path.Combine(_directory, "Migrations", "V001.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(_directory, "ProgrammableObjects", "Views", "Items.sql"), "SELECT 2;");
    }

    [Fact]
    public void DirectoryPreparation_PreservesNamespaceAndScriptNames()
    {
        var result = MigrationScriptService.GetScripts(new DbUpOptions { ScriptsPath = _directory, BaseNamespace = "App.Database" });

        Assert.True(result.IsValid, result.Error);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Scripts.Count);
        Assert.Equal("App.Database.Migrations.V001.sql", Assert.Single(result.Scripts.Migrations).Name);
        Assert.Equal("App.Database.ProgrammableObjects.Views.Items.sql", Assert.Single(result.Scripts.ProgrammableObjects).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void InvalidNamespace_ReturnsErrorForEverySource(string baseNamespace)
    {
        var options = new DbUpOptions { ScriptsPath = _directory, BaseNamespace = baseNamespace };
        AssertInvalid(MigrationScriptService.GetScripts(options), "BaseNamespace is required.");
        AssertInvalid(MigrationScriptService.GetScripts(typeof(ScriptTests).Assembly, options), "BaseNamespace is required.");
        AssertInvalid(MigrationScriptService.GetScripts([new SqlScript("Migrations.V001.sql", "SELECT 1;")], options), "BaseNamespace is required.");
    }

    [Fact]
    public void AssemblyPreparation_IgnoresScriptsPathAndPreservesResourceNames()
    {
        var options = new DbUpOptions { BaseNamespace = "TestScripts", ScriptsPath = "missing-directory" };
        var result = MigrationScriptService.GetScripts(typeof(ScriptTests).Assembly, options);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("TestScripts.Migrations.V001_CreateTable.sql", Assert.Single(result.Scripts.Migrations).Name);
        Assert.Equal("TestScripts.ProgrammableObjects.ItemNames.sql", Assert.Single(result.Scripts.ProgrammableObjects).Name);

        options.BaseNamespace = "Unrelated";
        AssertInvalid(MigrationScriptService.GetScripts(typeof(ScriptTests).Assembly, options));
    }

    [Fact]
    public void NamespaceFilterAndAdoption_UseRelativeCaseSensitiveMigrationNames()
    {
        SqlScript[] scripts =
        [
            new("First.Migrations.V001.sql", "SELECT 1;"),
            new("Second.Migrations.V001.sql", "SELECT 2;"),
            new("First.Other.sql", "SELECT 3;")
        ];
        var options = new DbUpOptions { BaseNamespace = "First", ScriptsToMarkAsExecuted = ["V001.sql"] };
        var result = MigrationScriptService.GetScripts(scripts, options);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("First.Migrations.V001.sql", Assert.Single(result.Scripts.ScriptsToMark).Name);
        Assert.Single(result.Scripts.Migrations);

        options.ScriptsToMarkAsExecuted = ["Second.Migrations.V001.sql"];
        AssertInvalid(MigrationScriptService.GetScripts(scripts, options));
        options.ScriptsToMarkAsExecuted = ["v001.sql"];
        AssertInvalid(MigrationScriptService.GetScripts(scripts, options));
        options.ScriptsToMarkAsExecuted = [];
        AssertInvalid(MigrationScriptService.GetScripts([scripts[1]], options));
        AssertInvalid(MigrationScriptService.GetScripts([scripts[2]], options));
    }

    [Fact]
    public void ScriptsPath_IsOnlyRequiredForDirectoryPreparation()
    {
        var options = new DbUpOptions { BaseNamespace = "TestScripts" };
        AssertInvalid(MigrationScriptService.GetScripts(options), "A scripts directory is required.");
        Assert.True(MigrationScriptService.GetScripts(typeof(ScriptTests).Assembly, options).IsValid);
    }

    [Fact]
    public void MissingDirectory_ReturnsError()
    {
        var options = new DbUpOptions { BaseNamespace = "Test", ScriptsPath = Path.Combine(_directory, "missing") };
        AssertInvalid(MigrationScriptService.GetScripts(options), "The scripts directory does not exist.");
    }

    [Fact]
    public void EmptyOrDuplicateScripts_ReturnError()
    {
        var options = new DbUpOptions { BaseNamespace = "Test" };
        AssertInvalid(MigrationScriptService.GetScripts([], options));
        AssertInvalid(MigrationScriptService.GetScripts(
            [new SqlScript("Test.Migrations.V001.sql", "SELECT 1;"),
             new SqlScript("Test.Migrations.v001.sql", "SELECT 2;")], options), "Duplicate SQL script names were found.");
    }

    [Theory]
    [InlineData("-- #requires Missing\nSELECT 1;", "SELECT 1;")]
    [InlineData("-- #requires B\nSELECT 1;", "-- #requires A\nSELECT 1;")]
    public void InvalidDependencies_ReturnSafeError(string first, string second)
    {
        var result = MigrationScriptService.GetScripts(
            [new SqlScript("Test.ProgrammableObjects.A.sql", first),
             new SqlScript("Test.ProgrammableObjects.B.sql", second)], new DbUpOptions { BaseNamespace = "Test" });

        AssertInvalid(result, "Programmable object dependencies are missing or cyclic.");
    }

    [Fact]
    public void UnknownMigrationToMark_DoesNotExposeItsNameInError()
    {
        var options = new DbUpOptions { BaseNamespace = "Test", ScriptsToMarkAsExecuted = ["secret-do-not-print.sql"] };
        var result = MigrationScriptService.GetScripts([new SqlScript("Test.Migrations.V001.sql", "SELECT 1;")], options);

        AssertInvalid(result);
        Assert.DoesNotContain("secret-do-not-print", result.Error);
    }

    [Fact]
    public void Preparation_EnumeratesSourceOnceAndOrdersDependencies()
    {
        var enumerations = 0;
        IEnumerable<SqlScript> Source()
        {
            Assert.Equal(1, ++enumerations);
            yield return new SqlScript("Test.ProgrammableObjects.A.sql", "-- #requires Z\nSELECT 1;");
            yield return new SqlScript("Test.ProgrammableObjects.Z.sql", "SELECT 1;");
        }

        var result = MigrationScriptService.GetScripts(Source(), new DbUpOptions { BaseNamespace = "Test" });

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(["Test.ProgrammableObjects.Z.sql", "Test.ProgrammableObjects.A.sql"],
            result.Scripts.ProgrammableObjects.Select(s => s.Name));
        Assert.Equal(1, enumerations);
    }

    private static void AssertInvalid(MigrationScriptResult result, string? expectedError = null)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Scripts);
        Assert.NotEmpty(result.Error);
        if (expectedError is not null)
            Assert.Equal(expectedError, result.Error);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
