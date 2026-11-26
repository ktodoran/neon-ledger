using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(NeonLedger.Tests.Infrastructure.PostgresServer))]

namespace NeonLedger.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL server for the whole test run. By default it is a throwaway
/// container, so the only requirement is Docker. Set NEON_LEDGER_TEST_POSTGRES to a
/// connection string (a user that may create databases) to use a server you already have.
/// </summary>
public sealed class PostgresServer : IAsyncLifetime
{
    public const string ConnectionStringVariable = "NEON_LEDGER_TEST_POSTGRES";

    private PostgreSqlContainer? _container;
    private string _adminConnectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _adminConnectionString = configured;
            return;
        }

        _container = new PostgreSqlBuilder("postgres:17")
            .WithCommand("-c", "max_connections=300")
            .Build();
        await _container.StartAsync();
        _adminConnectionString = _container.GetConnectionString();
    }

    /// <summary>Creates an empty database and returns a connection string for it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"ledger_test_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();

        // A small pool per database keeps the run inside the server's connection limit
        // while test classes run in parallel. Requests beyond it wait for a connection.
        return new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = name,
            MaxPoolSize = 15,
            Timeout = 60,
        }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
