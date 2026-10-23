using System.Text.Json;
using JJConsulting.DbUp.Cli;

namespace JJConsulting.DbUp.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jjdbup-tests-" + Guid.NewGuid().ToString("N"));

    public CliTests() => Directory.CreateDirectory(Path.Combine(_directory, ".git"));

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
    [InlineData("validate", "--scripts", "missing")]
    public void InvalidArguments_ReturnUsageCode(params string[] args) => Assert.Equal(2, Run(args).Code);

    [Fact]
    public void ValidateFolder_IsOfflineAndEmitsOneJsonResult()
    {
        var result = Run("validate", "--scripts", Scripts(), "--output", "json");
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
            {"DatabaseMigrations":{"Schema":"File","EnsureDatabase":true,
             "ScriptsPath":"../sql"},
             "ConnectionStrings":{"Other":"file-connection"}}
            """);
        var options = new DbUpCommandOptions { Config = "config/appsettings.json", Schema = "Argument", EnsureDatabase = false, ConnectionName = "Other" };
        var config = DbUpOptionsLoader.Load(options, _directory, new Dictionary<string, string?>
        {
            ["DatabaseMigrations__Schema"] = "Environment",
            ["ConnectionStrings__Other"] = "environment-connection"
        });
        Assert.Equal(Path.Combine(_directory, "sql"), config.Migrations.ScriptsPath);
        Assert.Equal("Argument", config.Migrations.Schema);
        Assert.False(config.Migrations.EnsureDatabase);
        Assert.Equal("environment-connection", config.ConnectionString);
        var environmentOnly = DbUpOptionsLoader.Load(new DbUpCommandOptions { ScriptsPath = "relative" }, _directory,
            new Dictionary<string, string?> { ["DatabaseMigrations__Schema"] = "Environment" });
        Assert.Equal("Environment", environmentOnly.Migrations.Schema);
        Assert.True(environmentOnly.Migrations.EnsureDatabase);
        Assert.Equal(Path.Combine(_directory, "relative"), environmentOnly.Migrations.ScriptsPath);
    }

    [Fact]
    public void Configuration_EnvironmentJsonOverridesBaseAndKeepsUnchangedValues()
    {
        var directory = Path.Combine(_directory, "config");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), """
            {"DatabaseMigrations":{"ConnectionStringKey":"JJMasterData:ConnectionString",
             "Schema":"base_schema","MigrationJournal":"AppVersions","EnsureDatabase":true,
             "ScriptsPath":"../base-sql","ScriptsToMarkAsExecuted":["Old.sql","Another.sql"]},
             "JJMasterData":{"ConnectionString":"base-connection"}}
            """);
        File.WriteAllText(Path.Combine(directory, "appsettings.Homolog.json"), """
            {"DatabaseMigrations":{"Schema":"homolog_schema","EnsureDatabase":false,
             "ScriptsPath":"../homolog-sql","ScriptsToMarkAsExecuted":["Baseline.sql"]},
             "JJMasterData":{"ConnectionString":"homolog-connection"}}
            """);

        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions { Config = "config/appsettings.json" }, _directory,
            new Dictionary<string, string?> { ["DOTNET_ENVIRONMENT"] = "Homolog" });

        Assert.Equal("homolog-connection", settings.RequireConnection());
        Assert.Equal("homolog_schema", settings.Migrations.Schema);
        Assert.Equal("AppVersions", settings.Migrations.MigrationJournal);
        Assert.False(settings.Migrations.EnsureDatabase);
        Assert.Equal(["Baseline.sql"], settings.Migrations.ScriptsToMarkAsExecuted);
        Assert.Equal(Path.Combine(_directory, "homolog-sql"), settings.Migrations.ScriptsPath);
    }

    [Theory]
    [InlineData("Local", "Homolog", "Local")]
    [InlineData(null, "Homolog", "Homolog")]
    [InlineData(" ", "Homolog", "Homolog")]
    [InlineData("Production", null, "Production")]
    [InlineData(null, null, "Base")]
    [InlineData("", "", "Base")]
    [InlineData(" ", " ", "Base")]
    public void Configuration_EnvironmentSelectionUsesDotnetThenAspNetAndOtherwiseOnlyBase(string? dotnet, string? aspnet, string expected)
    {
        foreach (var name in new[] { "Local", "Homolog", "Production" })
            File.WriteAllText(Path.Combine(_directory, $"appsettings.{name}.json"),
                JsonSerializer.Serialize(new { JJMasterData = new { ConnectionString = name } }));
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"),
            """
            {"DatabaseMigrations":{"ConnectionStringKey":"JJMasterData:ConnectionString"},
             "JJMasterData":{"ConnectionString":"Base"}}
            """);

        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory, new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = dotnet,
            ["ASPNETCORE_ENVIRONMENT"] = aspnet
        });

        Assert.Equal(expected, settings.RequireConnection());
    }

    [Fact]
    public void Configuration_EnvironmentVariablesAndArgumentsOverrideEnvironmentJson()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.Homolog.json"), """
            {"DatabaseMigrations":{"Schema":"json_schema","EnsureDatabase":true,"ScriptsPath":"json-sql"},
             "ConnectionStrings":{"DefaultConnection":"json-connection"}}
            """);
        var environment = new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Homolog",
            ["DatabaseMigrations__Schema"] = "environment_schema",
            ["DatabaseMigrations__EnsureDatabase"] = "false",
            ["DatabaseMigrations__ScriptsPath"] = "environment-sql",
            ["ConnectionStrings__DefaultConnection"] = "environment-connection"
        };
        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory, environment);
        Assert.Equal("environment_schema", settings.Migrations.Schema);
        Assert.False(settings.Migrations.EnsureDatabase);
        Assert.Equal("environment-connection", settings.RequireConnection());
        Assert.Equal(Path.Combine(_directory, "environment-sql"), settings.Migrations.ScriptsPath);

        settings = DbUpOptionsLoader.Load(new DbUpCommandOptions
        {
            Schema = "argument_schema", EnsureDatabase = true, ScriptsPath = "argument-sql", ConnectionString = "argument-connection"
        }, _directory, environment);
        Assert.Equal("argument_schema", settings.Migrations.Schema);
        Assert.True(settings.Migrations.EnsureDatabase);
        Assert.Equal("argument-connection", settings.RequireConnection());
        Assert.Equal(Path.Combine(_directory, "argument-sql"), settings.Migrations.ScriptsPath);
    }

    [Fact]
    public void Configuration_LocalEmptyMigrationSelectionClearsInheritedBaseline()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"EnsureDatabase":false,"ScriptsToMarkAsExecuted":["Baseline.sql"]}}
            """);
        File.WriteAllText(Path.Combine(_directory, "appsettings.Local.json"), """
            {"DatabaseMigrations":{"EnsureDatabase":true,"ScriptsToMarkAsExecuted":[]}}
            """);
        var environment = new Dictionary<string, string?> { ["DOTNET_ENVIRONMENT"] = "Local" };

        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory, environment);

        Assert.True(settings.Migrations.EnsureDatabase);
        Assert.Empty(settings.Migrations.ScriptsToMarkAsExecuted);
        environment["DatabaseMigrations__ScriptsToMarkAsExecuted__0"] = "Environment.sql";
        settings = DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory, environment);
        Assert.Equal(["Environment.sql"], settings.Migrations.ScriptsToMarkAsExecuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configuration_EnvironmentSelectionReplacesEveryLowerPriorityArrayEntry(bool useEnvironmentFile)
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"ScriptsToMarkAsExecuted":["Base.sql","BaseExtra.sql"]}}
            """);
        if (useEnvironmentFile)
            File.WriteAllText(Path.Combine(_directory, "appsettings.Local.json"), """
                {"DatabaseMigrations":{"ScriptsToMarkAsExecuted":["Local.sql","LocalExtra.sql","LocalLast.sql"]}}
                """);
        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory, new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Local",
            ["DatabaseMigrations__ScriptsToMarkAsExecuted__0"] = "Environment.sql"
        });

        Assert.Equal(["Environment.sql"], settings.Migrations.ScriptsToMarkAsExecuted);
    }

    [Fact]
    public void Configuration_AbsentSelectionInHigherLayersKeepsBaseSelection()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"ScriptsToMarkAsExecuted":["Base.sql","BaseExtra.sql"]}}
            """);
        File.WriteAllText(Path.Combine(_directory, "appsettings.Local.json"), """
            {"DatabaseMigrations":{"Schema":"local_schema"}}
            """);
        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory, new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Local",
            ["DatabaseMigrations__EnsureDatabase"] = "false"
        });

        Assert.Equal(["Base.sql", "BaseExtra.sql"], settings.Migrations.ScriptsToMarkAsExecuted);
    }

    [Fact]
    public void Configuration_CustomFilenameUsesEnvironmentOverlayBesideSelectedFile()
    {
        var directory = Path.Combine(_directory, "config");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "migrations.json"),
            """{"DatabaseMigrations":{"ScriptsPath":"../sql","Schema":"base_schema"}}""");
        File.WriteAllText(Path.Combine(directory, "migrations.Homolog.json"),
            """{"DatabaseMigrations":{"Schema":"homolog_schema"}}""");
        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions { Config = "config/migrations.json" }, _directory,
            new Dictionary<string, string?> { ["DOTNET_ENVIRONMENT"] = "Homolog" });
        Assert.Equal("homolog_schema", settings.Migrations.Schema);
        Assert.Equal(Path.Combine(_directory, "sql"), settings.Migrations.ScriptsPath);
    }

    [Fact]
    public void Configuration_ValidateUsesEnvironmentFileFromDiscoveredParent()
    {
        var scripts = Scripts();
        var nested = Path.Combine(scripts, "Migrations", "Nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"),
            """{"DatabaseMigrations":{"ScriptsPath":"missing-base-sql"}}""");
        File.WriteAllText(Path.Combine(_directory, "appsettings.Local.json"),
            """{"DatabaseMigrations":{"ScriptsPath":"sql"}}""");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliApplication.Run(["validate", "--output", "json"], output, error, nested,
            new Dictionary<string, string?> { ["DOTNET_ENVIRONMENT"] = "Local" });
        Assert.Equal(0, code);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("data").GetProperty("scriptCount").GetInt32());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Configuration_MissingEnvironmentFileKeepsBaseConfiguration()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"),
            """{"DatabaseMigrations":{"Schema":"base_schema"},"ConnectionStrings":{"DefaultConnection":"base-connection"}}""");
        var settings = DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory,
            new Dictionary<string, string?> { ["DOTNET_ENVIRONMENT"] = "Missing" });
        Assert.Equal("base_schema", settings.Migrations.Schema);
        Assert.Equal("base-connection", settings.RequireConnection());
    }

    [Fact]
    public void Configuration_InvalidEnvironmentJsonFailsInsteadOfUsingBaseConnection()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.Production.json"), "{ invalid json");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliApplication.Run(["validate", "--scripts", Scripts()], output, error, _directory,
            new Dictionary<string, string?> { ["DOTNET_ENVIRONMENT"] = "Production" });
        Assert.Equal(2, code);
    }

    [Fact]
    public void Configuration_NoEnvironmentDoesNotReadProductionJson()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.Production.json"), "{ invalid json");
        var result = Run("validate", "--scripts", Scripts(), "--output", "json");
        Assert.Equal(0, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(1, json.RootElement.GetProperty("data").GetProperty("scriptCount").GetInt32());
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("../Other")]
    [InlineData("..\\Other")]
    [InlineData("C:Other")]
    public void Configuration_EnvironmentNameCannotSelectAnotherDirectory(string name)
    {
        Assert.Throws<CliException>(() => DbUpOptionsLoader.Load(new DbUpCommandOptions(), _directory,
            new Dictionary<string, string?> { ["DOTNET_ENVIRONMENT"] = name }));
    }

    [Fact]
    public void ExplicitSource_OverridesConfiguredOtherSource()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"ScriptsPath":"missing-directory"}}
            """);
        Assert.Equal(0, Run("validate", "--scripts", Scripts()).Code);
        Assert.Equal(0, Run("validate", "--assembly", typeof(CliTests).Assembly.Location).Code);
        Assert.Equal(2, Run("validate", "--scripts", Scripts(), "--assembly", "missing.dll").Code);
    }

    [Fact]
    public void EnvironmentSource_ReplacesFileSourceAndUsesWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"ScriptsPath":"file-directory"}}
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
        Assert.Equal(2, Run("validate", "--scripts", Scripts()).Code);
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"EnsureDatabase":"invalid","ScriptsPath":"sql"}}
            """);
        Assert.Equal(2, Run("validate").Code);
        Assert.Equal(0, Run("validate", "--ensure-database", "true").Code);
    }

    [Fact]
    public void SharedScriptsPath_PreservesNamespaceAndKeepsConnectionSelectionInCli()
    {
        Scripts();
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            {"DatabaseMigrations":{"ScriptsPath":"sql"},
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
        Assert.Equal(2, Run("new", "V002_Test", "--assembly", typeof(CliTests).Assembly.Location).Code);
    }

    [Fact]
    public void Baseline_RejectsAssemblyAndInvalidNamesWithoutConnecting()
    {
        Assert.Equal(2, Run("baseline", "B005", "--assembly", typeof(CliTests).Assembly.Location,
            "--connection-string", "invalid").Code);
        foreach (var name in new[] { "../B005", "Nested/B005", "Nested\\B005" })
            Assert.Equal(2, Run("baseline", name, "--scripts", Scripts(), "--connection-string", "invalid", "--dry").Code);
    }

    [Fact]
    public void New_CreatesNestedFilesAndDoesNotOverwrite()
    {
        var result = Run("new", "Views/ItemNames", "--type", "object", "--scripts", "sql");
        Assert.Equal(0, result.Code);
        var path = Path.Combine(_directory, "sql", "ProgrammableObjects", "Views", "ItemNames.sql");
        Assert.Contains("CREATE OR ALTER", File.ReadAllText(path));
        Assert.Equal(2, Run("new", "Views/ItemNames", "--type", "object", "--scripts", "sql").Code);
        Assert.Equal(0, Run("new", "V002_Test.sql", "--scripts", "sql").Code);
        Assert.True(File.Exists(Path.Combine(_directory, "sql", "Migrations", "V002_Test.sql")));
        Assert.Equal(0, Run("new", "InitialSchema", "--scripts", "sql").Code);
        Assert.Equal(0, Run("validate", "--scripts", "sql").Code);
    }

    [Theory]
    [InlineData("#requires")]
    [InlineData("#depends")]
    public void NewObject_CanBeValidatedWithoutEditingItsComment(string directive)
    {
        string[] options = ["--scripts", "sql", "--dependency-directive", directive];
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
        Assert.Equal(2, Run("new", name, "--scripts", "sql").Code);

    [Fact]
    public void New_RejectsSymlinkDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "outside"));
        Directory.CreateSymbolicLink(Path.Combine(_directory, "linked"), Path.Combine(_directory, "outside"));
        Assert.Equal(2, Run("new", "escape", "--scripts", "linked").Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_directory, "outside"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void FolderNamesThatFlattenToSameResourceName_AreRejected()
    {
        var root = Scripts();
        Directory.CreateDirectory(Path.Combine(root, "Migrations", "Nested"));
        File.WriteAllText(Path.Combine(root, "Migrations", "Nested", "V002.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(root, "Migrations", "Nested.V002.sql"), "SELECT 2;");
        Assert.Equal(2, Run("validate", "--scripts", root).Code);
    }

    [Theory]
    [InlineData("status", "--unknown")]
    [InlineData("migrate")]
    public void ParserAndConfigurationErrors_DoNotEchoSecretsAndEmitJson(string command, params string[] extra)
    {
        var result = Run([command, "--scripts", Scripts(), "--connection-string", "secret-value-is-not-a-connection-string", "--output", "json", .. extra]);
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
        var scripts = MigrationScriptService.GetScripts(settings.Migrations);
        Assert.True(scripts.IsValid, scripts.Error);
        Assert.Equal("sql.Migrations.V001.sql", Assert.Single(scripts.Scripts.Migrations).Name);
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
        var scripts = MigrationScriptService.GetScripts(settings.Migrations);
        Assert.True(scripts.IsValid, scripts.Error);
        Assert.Equal("Orders.sql.Migrations.V001.sql", Assert.Single(scripts.Scripts.Migrations).Name);
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
        var scripts = MigrationScriptService.GetScripts(settings.Migrations);
        Assert.True(scripts.IsValid, scripts.Error);
        Assert.Equal("Order.Api.sql.Migrations.V001.sql", Assert.Single(scripts.Scripts.Migrations).Name);
    }

    [Theory]
    [InlineData("<Project>")]
    [InlineData("<Project><RootNamespace>$(Unknown)</RootNamespace></Project>")]
    [InlineData("<Project><RootNamespace>One</RootNamespace><RootNamespace>Two</RootNamespace></Project>")]
    public void Discovery_UnreadableOrAmbiguousProjectFailsWithoutInventingJournalNames(string project)
    {
        Scripts();
        File.WriteAllText(Path.Combine(_directory, "Order.Api.csproj"), project);
        Assert.Equal(2, Run("validate").Code);
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
