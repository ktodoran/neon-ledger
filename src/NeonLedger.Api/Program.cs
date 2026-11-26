using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NeonLedger.Api;
using NeonLedger.Data;
using NeonLedger.Events;
using NeonLedger.Idempotency;
using NeonLedger.Ledger;
using NeonLedger.Payments;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<LedgerDbContext>((services, options) =>
{
    var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Ledger")
        ?? throw new InvalidOperationException("Set ConnectionStrings:Ledger (environment variable ConnectionStrings__Ledger).");
    options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<LedgerService>();
builder.Services.AddScoped<EventLog>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<IdempotentExecutor>();
builder.Services.AddScoped<IntegrityCheck>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));

var migrateOnly = args is ["migrate"];
if (migrateOnly)
{
    // On an empty database EF probes for its history table and logs the failed query
    // as an error before creating the table. A migration that really fails throws.
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
}

var app = builder.Build();

// `dotnet run -- migrate` applies pending migrations and exits. The server never migrates on its own.
if (migrateOnly)
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database;
    var before = (await database.GetAppliedMigrationsAsync()).Count();
    await database.MigrateAsync();
    var applied = (await database.GetAppliedMigrationsAsync()).Count() - before;
    Console.WriteLine(applied == 0 ? "Database is up to date." : $"Applied {applied} migration(s).");
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapGet("/health", async (LedgerDbContext db, CancellationToken cancellationToken) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "ok" })
            : Results.Json(new { status = "database_unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable))
    .ExcludeFromDescription();
app.MapGet("/v1/integrity", async (IntegrityCheck check, CancellationToken cancellationToken) =>
        Results.Ok(await check.RunAsync(cancellationToken)))
    .WithTags("Operations")
    .Produces<IntegrityReport>();
app.MapAccountEndpoints();
app.MapTransactionEndpoints();
app.MapPaymentEndpoints();
app.MapEventEndpoints();

await app.RunAsync();
