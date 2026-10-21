using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace JJConsulting.DbUp.Tests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly List<string> _connectionStrings = [];

    public MsSqlContainer Container { get; } = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    public string CreateDatabaseConnectionString()
    {
        var connectionString = new SqlConnectionStringBuilder(Container.GetConnectionString())
        {
            InitialCatalog = "JJMigrations_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        _connectionStrings.Add(connectionString);
        return connectionString;
    }

    public async ValueTask InitializeAsync() => await Container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        // Databases remain isolated and are removed together with the container,
        // avoiding an ALTER/DROP DATABASE round trip after every test.
        foreach (var connectionString in _connectionStrings)
        {
            await using var connection = new SqlConnection(connectionString);
            SqlConnection.ClearPool(connection);
        }

        await Container.DisposeAsync();
    }
}
