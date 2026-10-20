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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryPreparation_PreservesNestedNamesContentsAndCaseInsensitiveExtensions(bool relativePath)
    {
        var nested = Path.Combine(_directory, "Migrations", "Nested");
        Directory.CreateDirectory(nested);
        const string sql = "-- Migração: ação\nSELECT N'ação';";
        File.WriteAllText(Path.Combine(nested, "V002.SQL"), sql, System.Text.Encoding.Unicode);
        File.WriteAllText(Path.Combine(nested, "ignored.txt"), "not SQL");
        File.WriteAllText(Path.Combine(_directory, "V999.sql"), "not a migration");
        Directory.CreateDirectory(Path.Combine(_directory, "Other", "Migrations"));
        File.WriteAllText(Path.Combine(_directory, "Other", "Migrations", "V999.sql"), "not a migration");
        var path = relativePath ? Path.GetRelativePath(Environment.CurrentDirectory, _directory) : _directory;

        var result = MigrationScriptService.GetScripts(new DbUpOptions
        {
            ScriptsPath = path + Path.DirectorySeparatorChar,
            BaseNamespace = "App.Database"
        });

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(["App.Database.Migrations.Nested.V002.SQL", "App.Database.Migrations.V001.sql"],
            result.Scripts.Migrations.Select(script => script.Name));
        Assert.Equal("App.Database.ProgrammableObjects.Views.Items.sql", Assert.Single(result.Scripts.ProgrammableObjects).Name);
        var script = Assert.Single(result.Scripts.Migrations, s => s.Name == "App.Database.Migrations.Nested.V002.SQL");
        Assert.Equal(sql, script.Contents);
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

    [Theory]
    [InlineData("V001.sql")]
    [InlineData("B005.sql")]
    public void NamespaceFilterAndAdoption_UseRelativeCaseSensitiveMigrationNames(string name)
    {
        SqlScript[] scripts =
        [
            new($"First.Migrations.{name}", "SELECT 1;"),
            new($"Second.Migrations.{name}", "SELECT 2;"),
            new("First.Other.sql", "SELECT 3;")
        ];
        var options = new DbUpOptions { BaseNamespace = "First", ScriptsToMarkAsExecuted = [name] };
        var result = MigrationScriptService.GetScripts(scripts, options);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal($"First.Migrations.{name}", Assert.Single(result.Scripts.ScriptsToMark).Name);
        Assert.Single(result.Scripts.Migrations);

        options.ScriptsToMarkAsExecuted = [$"Second.Migrations.{name}"];
        AssertInvalid(MigrationScriptService.GetScripts(scripts, options));
        options.ScriptsToMarkAsExecuted = [name.ToLowerInvariant()];
        AssertInvalid(MigrationScriptService.GetScripts(scripts, options));
        options.ScriptsToMarkAsExecuted = [];
        AssertInvalid(MigrationScriptService.GetScripts([scripts[1]], options));
        AssertInvalid(MigrationScriptService.GetScripts([scripts[2]], options));
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

    [Fact]
    public void Preparation_AcceptsAnyMigrationNamesAndOrdersByName()
    {
        var result = MigrationScriptService.GetScripts(
            new[] { "V10", "V2", "B005", "Initial", "V001_First", "V1_Second" }
                .Select(name => new SqlScript($"Test.Migrations.{name}.sql", "SELECT 1;")),
            new DbUpOptions { BaseNamespace = "Test" });

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(["B005", "Initial", "V001_First", "V10", "V1_Second", "V2"],
            result.Scripts.Migrations.Select(s => s.Name["Test.Migrations.".Length..^4]));
    }

    [Theory]
    [InlineData(null, "A scripts directory is required.")]
    [InlineData("missing", "The scripts directory does not exist.")]
    public void DirectoryPreparation_RejectsMissingPath(string? path, string expectedError)
    {
        var options = new DbUpOptions
        {
            BaseNamespace = "Test", ScriptsPath = path is null ? null : Path.Combine(_directory, path)
        };
        AssertInvalid(MigrationScriptService.GetScripts(options), expectedError);
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
