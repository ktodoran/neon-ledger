using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NeonLedger.Data;
using NeonLedger.Tests.Infrastructure;
using Npgsql;

namespace NeonLedger.Tests;

/// <summary>What the migration that adds callers does to a ledger that was already in use.</summary>
public sealed class AuthenticationMigrationTests(PostgresServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Idempotency_keys_used_before_callers_existed_are_kept_under_a_caller_nobody_can_sign_in_as()
    {
        var connectionString = await server.CreateDatabaseAsync();
        await using var factory = Start(connectionString);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database;
            await database.GetService<IMigrator>().MigrateAsync("Payments", Ct);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await ExecuteAsync(
            connection,
            $"""
            INSERT INTO idempotency_keys (key, operation, request_hash, status_code, response_body, created_at)
            VALUES ('an-old-key', 'post_transaction', '{new string('a', 64)}', 201, '{"{}"}', now())
            """);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync(Ct);
        }

        Assert.Equal("before-authentication", await ScalarAsync(connection, "SELECT c.name FROM idempotency_keys k JOIN callers c ON c.id = k.caller_id WHERE k.key = 'an-old-key'"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM api_keys"));
    }

    [Fact]
    public async Task A_new_ledger_starts_with_no_callers_at_all()
    {
        var connectionString = await server.CreateDatabaseAsync();
        await using var factory = Start(connectionString);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync(Ct);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM callers"));
    }

    private static WebApplicationFactory<Program> Start(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Ledger", connectionString);
        });

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }
}
