using JJConsulting.DbUp.Cli;
using JJConsulting.DbUp.Configuration;

namespace JJConsulting.DbUp.Tests;

public sealed class BaselineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jjdbup-baseline-files-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("B005_InitialState", "B005_InitialState.sql")]
    [InlineData("B005_InitialState.SQL", "B005_InitialState.sql")]
    [InlineData("InitialState", "InitialState.sql")]
    [InlineData("001_Initial", "001_Initial.sql")]
    public void BaselineName_NormalizesOptionalExtension(string name, string expected)
    {
        Assert.Equal(expected, BaselineService.NormalizeName(name));
    }

    [Theory]
    [InlineData("../B005")]
    [InlineData("Nested/B005")]
    [InlineData("Nested\\B005")]
    [InlineData("/B005")]
    public void BaselineName_RejectsInvalidNamesBeforeConnecting(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            BaselineService.Generate("invalid", new DbUpOptions { BaseNamespace = "Test" }, name));
    }

    [Fact]
    public void StaticValidation_ChecksSqlWithoutExecutingIt()
    {
        BaselineService.ValidateSql("CREATE TABLE dbo.Example (Id int PRIMARY KEY);\nGO\nCREATE INDEX IX_Example ON dbo.Example(Id);\nGO");
        Assert.Throws<InvalidOperationException>(() => BaselineService.ValidateSql("CREATE TABLE ;"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Save_PreservesExistingMigrations(bool dryRun)
    {
        CreateExistingScripts();
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllText);
        var files = new BaselineFiles(_root, "B005_Initial");
        var result = files.Save(Generated, dryRun);
        Assert.Equal(dryRun, result.DryRun);
        Assert.Equal(!dryRun, File.Exists(Destination));
        foreach (var (path, contents) in before)
            Assert.Equal(contents, File.ReadAllText(path));
        if (!dryRun)
            Assert.Equal(Generated.Sql, File.ReadAllText(Destination));
    }

    [Fact]
    public void ExistingDestination_IsNeverOverwrittenIncludingDuringPreview()
    {
        CreateExistingScripts();
        var files = new BaselineFiles(_root, "B005_Initial");
        File.WriteAllText(Destination, "existing");
        Assert.Throws<CliException>(() => files.Save(Generated, false));
        Assert.Throws<CliException>(() => files.Save(Generated, true));
        Assert.Equal("existing", File.ReadAllText(Destination));
        Assert.Throws<CliException>(() => new BaselineFiles(_root, "B005_Initial"));
    }

    [Fact]
    public void SymlinkedDestinationDirectory_IsRejected()
    {
        CreateExistingScripts();
        Directory.Move(Path.Combine(_root, "Migrations"), Path.Combine(_root, "Outside"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "Migrations"), Path.Combine(_root, "Outside"));
        Assert.Throws<CliException>(() => new BaselineFiles(_root, "B005_Initial"));
    }

    private void CreateExistingScripts()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Migrations", "Nested"));
        Directory.CreateDirectory(Path.Combine(_root, "ProgrammableObjects"));
        File.WriteAllText(Path.Combine(_root, "Migrations", "V001.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(_root, "Migrations", "Nested", "V005.sql"), "SELECT 5;");
        File.WriteAllText(Path.Combine(_root, "Migrations", "V006.sql"), "SELECT 6;");
        File.WriteAllText(Path.Combine(_root, "Migrations", "B002.sql"), "SELECT 2;");
        File.WriteAllText(Path.Combine(_root, "ProgrammableObjects", "Example.sql"), "SELECT 1;");
    }

    private static BaselineGenerationResult Generated => new()
    {
        Sql = "SELECT 1;\nGO\n",
        ScriptName = "B005_Initial.sql"
    };
    private string Destination => Path.Combine(_root, "Migrations", Generated.ScriptName);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
