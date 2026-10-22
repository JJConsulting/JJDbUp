using JJConsulting.DbUp.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JJConsulting.DbUp.Tests;

public sealed class DbUpServiceCollectionExtensionsTests
{
    [Fact]
    public void AddDbUpMigrations_BindsConfigurationToResolvedOptions()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatabaseMigrations:ConnectionStringKey"] = "App:Connection",
            ["DatabaseMigrations:ScriptsPath"] = "Database",
            ["DatabaseMigrations:Schema"] = "migrations",
            ["DatabaseMigrations:MigrationJournal"] = "AppliedMigrations",
            ["DatabaseMigrations:ProgrammableObjectJournal"] = "AppliedObjects",
            ["DatabaseMigrations:DependencyDirective"] = "#depends",
            ["DatabaseMigrations:EnsureDatabase"] = "false",
            ["DatabaseMigrations:ScriptsToMarkAsExecuted:0"] = "V001_CreateTable.sql"
        }).Build();
        var services = new ServiceCollection();

        services.AddDbUpMigrations(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DbUpOptions>>().Value;

        Assert.Equal("App:Connection", options.ConnectionStringKey);
        Assert.Equal("Database", options.ScriptsPath);
        Assert.Equal("migrations", options.Schema);
        Assert.Equal("AppliedMigrations", options.MigrationJournal);
        Assert.Equal("AppliedObjects", options.ProgrammableObjectJournal);
        Assert.Equal("#depends", options.DependencyDirective);
        Assert.False(options.EnsureDatabase);
        Assert.Equal(["V001_CreateTable.sql"], options.ScriptsToMarkAsExecuted);
    }

    [Fact]
    public void AddDbUpMigrations_PreservesDefaultsWhenSectionIsMissing()
    {
        var services = new ServiceCollection();
        services.AddDbUpMigrations(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<DbUpOptions>>().Value;

        Assert.Equal("ConnectionStrings:DefaultConnection", options.ConnectionStringKey);
        Assert.Null(options.ScriptsPath);
        Assert.Equal("dbo", options.Schema);
        Assert.Equal("SchemaVersions", options.MigrationJournal);
        Assert.Equal("ProgrammableObjectVersions", options.ProgrammableObjectJournal);
        Assert.Equal("#requires", options.DependencyDirective);
        Assert.True(options.EnsureDatabase);
        Assert.Empty(options.ScriptsToMarkAsExecuted);
    }
}
