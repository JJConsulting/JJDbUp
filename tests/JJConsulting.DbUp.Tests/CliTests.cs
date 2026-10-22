using System.Text.Json;
using JJConsulting.DbUp.Cli;

namespace JJConsulting.DbUp.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jjdbup-tests-" + Guid.NewGuid().ToString("N"));

    public CliTests() => Directory.CreateDirectory(_directory);

    private (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliApplication.Run(args, output, error, _directory, []);
        return (code, output.ToString(), error.ToString());
    }

    private string Scripts()
    {
        var root = Path.Combine(_directory, "sql");
        Directory.CreateDirectory(Path.Combine(root, "Migrations"));
        File.WriteAllText(Path.Combine(root, "Migrations", "V001.sql"), "SELECT 1;");
        return root;
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("status", "--help")]
    [InlineData("baseline", "--help")]
    public void BuiltInActions_DoNotRequireConfiguration(params string[] args)
    {
        var result = Run(args);
        Assert.Equal(0, result.Code);
        Assert.NotEmpty(result.Output);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("status", "--invalid")]
    [InlineData("new")]
    [InlineData("mark-executed")]
    [InlineData("baseline")]
    [InlineData("validate", "--output", "xml")]
    [InlineData("validate", "--config", "missing.json")]
    [InlineData("validate", "--scripts", ".")]
    [InlineData("validate", "--scripts", "missing", "--base-namespace", "Test")]
    public void InvalidArguments_ReturnUsageCode(params string[] args) => Assert.Equal(2, Run(args).Code);

    [Fact]
    public void ValidateFolder_IsOfflineAndEmitsOneJsonResult()
    {
        var result = Run("validate", "--scripts", Scripts(), "--base-namespace", "TestScripts", "--output", "json");
        Assert.Equal(0, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("validate", json.RootElement.GetProperty("command").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("data").GetProperty("scriptCount").GetInt32());
        Assert.Empty(result.Error);
    }

    [Fact]
    public void Configuration_UsesFileRelativePathsAndEnvironmentAndArgumentPrecedence()
    {
        var sub = Path.Combine(_directory, "config");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "appsettings.json"), """
            {"DatabaseMigrations":{"BaseNamespace":"File","EnsureDatabase":true,
             "ScriptsPath":"../sql"},
             "ConnectionStrings":{"Other":"file-connection"}}
            """);
        var options = new DbUpCommandOptions { Config = "config/appsettings.json", BaseNamespace = "Argument", EnsureDatabase = false, ConnectionName = "Other" };
        var config = DbUpOptionsLoader.Load(options, _directory, new Dictionary<string, string?>
        {
            ["DatabaseMigrations__BaseNamespace"] = "Environment",
            ["ConnectionStrings__Other"] = "environment-connection"
        });
        Assert.Equal(Path.Combine(_directory, "sql"), config.Migrations.ScriptsPath);
        Assert.Equal("Argument", config.Migrations.BaseNamespace);
        Assert.False(config.Migrations.EnsureDatabase);
        Assert.Equal("environment-connection", config.ConnectionString);
        var environmentOnly = DbUpOptionsLoader.Load(new DbUpCommandOptions { ScriptsPath = "relative" }, _directory,
            new Dictionary<string, string?> { ["DatabaseMigrations__BaseNamespace"] = "Environment" });
        Assert.Equal("Environment", environmentOnly.Migrations.BaseNamespace);
        Assert.True(environmentOnly.Migrations.EnsureDatabase);
        Assert.Equal(Path.Combine(_directory, "relative"), environmentOnly.Migrations.ScriptsPath);
    }

    [Fact]
    public void ExplicitSource_OverridesConfiguredOtherSource()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"BaseNamespace":"TestScripts","ScriptsPath":"missing-directory"}}
            """);
        Assert.Equal(0, Run("validate", "--scripts", Scripts()).Code);
        Assert.Equal(0, Run("validate", "--assembly", typeof(CliTests).Assembly.Location).Code);
        Assert.Equal(2, Run("validate", "--scripts", Scripts(), "--assembly", "missing.dll").Code);
    }

    [Fact]
    public void EnvironmentSource_ReplacesFileSourceAndUsesWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"BaseNamespace":"TestScripts","ScriptsPath":"file-directory"}}
            """);
        var config = DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory,
            new Dictionary<string, string?> { ["DatabaseMigrations__ScriptsPath"] = "environment-sql" });
        Assert.Equal(Path.Combine(_directory, "environment-sql"), config.Migrations.ScriptsPath);
        Assert.Null(config.Assembly);
    }

    [Fact]
    public void InvalidJsonAndInvalidBoolean_AreConfigurationErrors()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), "{ invalid json");
        Assert.Equal(2, Run("validate", "--scripts", Scripts(), "--base-namespace", "Test").Code);
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"BaseNamespace":"Test","EnsureDatabase":"invalid","ScriptsPath":"sql"}}
            """);
        Assert.Equal(2, Run("validate").Code);
        Assert.Equal(0, Run("validate", "--ensure-database", "true").Code);
    }

    [Fact]
    public void SharedScriptsPath_PreservesNamespaceAndKeepsConnectionSelectionInCli()
    {
        Scripts();
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"ScriptsPath":"sql","BaseNamespace":"TestScripts"},
             "ConnectionStrings":{"DefaultConnection":"default-connection","Other":"other-connection"}}
            """);
        Assert.Equal(0, Run("validate").Code);
        var args = new DbUpCommandOptions();
        var settings = DbUpOptionsLoader.Load(args, _directory, []);
        Assert.Equal("default-connection", settings.ConnectionString);
        Assert.True(MigrationScriptService.GetScripts(settings.Migrations).IsValid);

        args.ConnectionName = "Other";
        Assert.Equal("other-connection", DbUpOptionsLoader.Load(args, _directory, []).ConnectionString);
        args.ConnectionString = "direct-connection";
        Assert.Equal("direct-connection", DbUpOptionsLoader.Load(args, _directory, []).ConnectionString);
    }

    [Fact]
    public void ConnectionStringKey_ResolvesCustomSectionAndHonorsOverrides()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"ConnectionStringKey":"JJMasterData:ConnectionString"},
             "JJMasterData":{"ConnectionString":"custom-connection"},
             "ConnectionStrings":{"Other":"named-connection"}}
            """);
        var args = new DbUpCommandOptions();
        Assert.Equal("custom-connection", DbUpOptionsLoader.Load(args, _directory, []).RequireConnection());
        Assert.Equal("environment-connection", DbUpOptionsLoader.Load(args, _directory,
            new Dictionary<string, string?>
            {
                ["DatabaseMigrations__ConnectionStringKey"] = "Another:Connection",
                ["Another__Connection"] = "environment-connection"
            }).RequireConnection());
        Assert.Equal("overridden-value", DbUpOptionsLoader.Load(args, _directory,
            new Dictionary<string, string?> { ["JJMasterData__ConnectionString"] = "overridden-value" }).RequireConnection());
        args.ConnectionName = "Other";
        Assert.Equal("named-connection", DbUpOptionsLoader.Load(args, _directory, []).RequireConnection());
        args.ConnectionString = "direct-connection";
        Assert.Equal("direct-connection", DbUpOptionsLoader.Load(args, _directory, []).RequireConnection());
    }

    [Fact]
    public void ConnectionStringKey_MissingValueDoesNotFallBackToAnotherDatabase()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"ConnectionStringKey":"JJMasterData:ConnectionString"},
             "ConnectionStrings":{"DefaultConnection":"different-database"}}
            """);
        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory, []);
        Assert.Throws<CliException>(() => settings.RequireConnection());
    }

    [Fact]
    public void ConnectionStringKey_DefaultAndEmptyKeyAreHandled()
    {
        Assert.Equal("ConnectionStrings:DefaultConnection",
            DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory, []).Migrations.ConnectionStringKey);
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"),
            """{"DatabaseMigrations":{"ConnectionStringKey":" "}}""");
        Assert.Equal(2, Run("status").Code);
    }

    [Fact]
    public void New_RejectsAssemblySource()
    {
        Assert.Equal(2, Run("new", "V002_Test", "--assembly", typeof(CliTests).Assembly.Location,
            "--base-namespace", "TestScripts").Code);
    }

    [Fact]
    public void Baseline_RejectsAssemblyAndInvalidNamesWithoutConnecting()
    {
        Assert.Equal(2, Run("baseline", "B005", "--assembly", typeof(CliTests).Assembly.Location,
            "--base-namespace", "TestScripts", "--connection-string", "invalid").Code);
        foreach (var name in new[] { "../B005", "Nested/B005", "Nested\\B005" })
            Assert.Equal(2, Run("baseline", name, "--scripts", Scripts(), "--base-namespace", "TestScripts",
                "--connection-string", "invalid", "--dry").Code);
    }

    [Fact]
    public void New_CreatesNestedFilesAndDoesNotOverwrite()
    {
        var result = Run("new", "Views/ItemNames", "--type", "object", "--scripts", "sql", "--base-namespace", "Test");
        Assert.Equal(0, result.Code);
        var path = Path.Combine(_directory, "sql", "ProgrammableObjects", "Views", "ItemNames.sql");
        Assert.Contains("CREATE OR ALTER", File.ReadAllText(path));
        Assert.Equal(2, Run("new", "Views/ItemNames", "--type", "object", "--scripts", "sql", "--base-namespace", "Test").Code);
        Assert.Equal(0, Run("new", "V002_Test.sql", "--scripts", "sql", "--base-namespace", "Test").Code);
        Assert.True(File.Exists(Path.Combine(_directory, "sql", "Migrations", "V002_Test.sql")));
        Assert.Equal(0, Run("new", "InitialSchema", "--scripts", "sql", "--base-namespace", "Test").Code);
        Assert.Equal(0, Run("validate", "--scripts", "sql", "--base-namespace", "Test").Code);
    }

    [Theory]
    [InlineData("#requires")]
    [InlineData("#depends")]
    public void NewObject_CanBeValidatedWithoutEditingItsComment(string directive)
    {
        string[] options = ["--scripts", "sql", "--base-namespace", "Test", "--dependency-directive", directive];
        Assert.Equal(0, Run(["new", "Views/Example", "--type", "object", .. options]).Code);

        var result = Run(["validate", .. options]);

        Assert.True(result.Code == 0, result.Output + result.Error);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("Views/../../escape")]
    [InlineData("..\\escape")]
    [InlineData("/tmp/escape")]
    [InlineData("C:\\escape")]
    public void New_RejectsTraversal(string name) =>
        Assert.Equal(2, Run("new", name, "--scripts", "sql", "--base-namespace", "Test").Code);

    [Fact]
    public void New_RejectsSymlinkDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "outside"));
        Directory.CreateSymbolicLink(Path.Combine(_directory, "linked"), Path.Combine(_directory, "outside"));
        Assert.Equal(2, Run("new", "escape", "--scripts", "linked", "--base-namespace", "Test").Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_directory, "outside"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void FolderNamesThatFlattenToSameResourceName_AreRejected()
    {
        var root = Scripts();
        Directory.CreateDirectory(Path.Combine(root, "Migrations", "Nested"));
        File.WriteAllText(Path.Combine(root, "Migrations", "Nested", "V002.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(root, "Migrations", "Nested.V002.sql"), "SELECT 2;");
        Assert.Equal(2, Run("validate", "--scripts", root, "--base-namespace", "Test").Code);
    }

    [Theory]
    [InlineData("status", "--unknown")]
    [InlineData("migrate")]
    public void ParserAndConfigurationErrors_DoNotEchoSecretsAndEmitJson(string command, params string[] extra)
    {
        var result = Run([command, "--scripts", Scripts(), "--base-namespace", "TestScripts",
            "--connection-string", "secret-value-is-not-a-connection-string", "--output", "json", .. extra]);
        Assert.Equal(2, result.Code);
        Assert.DoesNotContain("secret-value", result.Output + result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void StatusTable_RendersSqlNamesAsLiteralText()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var presentation = new ConsoleHelper("status", false, output, error);
        presentation.Complete(new DatabaseMigrationStatus
        {
            DatabaseExists = true,
            PendingMigrations = ["Test.Migrations.[red].sql"],
            PendingObjects = []
        });
        Assert.Contains("[red].sql", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Discovery_ValidatesScriptsWithoutSourceOrNamespace()
    {
        Scripts();
        Assert.Equal(0, Run("validate").Code);
        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory, []);
        Assert.Equal(Path.Combine(_directory, "sql"), settings.Migrations.ScriptsPath);
        Assert.Equal("sql", settings.Migrations.BaseNamespace);
    }

    [Fact]
    public void Discovery_UsesProjectNamespaceAndParentConfigurationFromNestedDirectory()
    {
        var root = Scripts();
        File.WriteAllText(Path.Combine(_directory, "Order.Api.csproj"),
            "<Project><PropertyGroup><RootNamespace>Orders</RootNamespace></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"),
            """{"ConnectionStrings":{"DefaultConnection":"parent-connection"}}""");
        var nested = Path.Combine(root, "Migrations", "Nested");
        Directory.CreateDirectory(nested);
        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions(), nested, []);
        Assert.Equal(root, settings.Migrations.ScriptsPath);
        Assert.Equal("Orders.sql", settings.Migrations.BaseNamespace);
        Assert.Equal("parent-connection", settings.ConnectionString);
    }

    [Fact]
    public void Discovery_RejectsAmbiguousRootsAndAcceptsExplicitSelection()
    {
        Scripts();
        Directory.CreateDirectory(Path.Combine(_directory, "Database", "Migrations"));
        var result = Run("validate");
        Assert.Equal(2, result.Code);
        Assert.Contains("Multiple scripts directories", result.Output + result.Error);
        Assert.Equal(0, Run("validate", "--scripts", "sql").Code);
    }

    [Fact]
    public void Discovery_InitializesEmptyDirectoryWithNewCommand()
    {
        Assert.Equal(0, Run("new", "V001_Initial").Code);
        Assert.True(File.Exists(Path.Combine(_directory, "Migrations", "V001_Initial.sql")));
        Assert.Equal(0, Run("validate").Code);
    }

    [Fact]
    public void Discovery_UsesProjectNameWhenRootNamespaceIsAbsent()
    {
        Scripts();
        File.WriteAllText(Path.Combine(_directory, "Order.Api.csproj"), "<Project />");
        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory, []);
        Assert.Equal("Order.Api.sql", settings.Migrations.BaseNamespace);
    }

    [Theory]
    [InlineData("<Project>")]
    [InlineData("<Project><RootNamespace>$(Unknown)</RootNamespace></Project>")]
    [InlineData("<Project><RootNamespace>One</RootNamespace><RootNamespace>Two</RootNamespace></Project>")]
    public void Discovery_UnreadableOrAmbiguousProjectRequiresExplicitNamespace(string project)
    {
        Scripts();
        File.WriteAllText(Path.Combine(_directory, "Order.Api.csproj"), project);
        Assert.Equal(2, Run("validate").Code);
        Assert.Equal(0, Run("validate", "--base-namespace", "Explicit").Code);
    }

    [Fact]
    public void Discovery_DoesNotEscapeRepositoryBoundary()
    {
        Scripts();
        var repository = Path.Combine(_directory, "OtherRepository");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions(), repository, []);
        Assert.Equal(repository, settings.Migrations.ScriptsPath);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
