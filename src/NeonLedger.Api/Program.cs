using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NeonLedger;
using NeonLedger.Access;
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
builder.Services.AddScoped<KeyIssuer>();
builder.Services.AddScoped<CurrentCaller>();
builder.Services.AddHttpContextAccessor();

// Every endpoint needs an API key unless it is marked as open, and a write needs a key that may write.
builder.Services.AddAuthentication(ApiKeyAuthentication.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthentication>(ApiKeyAuthentication.SchemeName, null);
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(ApiKeyAuthentication.Policy);

// Which web pages may call this service from a browser. None, unless configured. A page
// still needs an API key; this decides whether the browser lets it try.
var browserOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(browserOrigins)
    .WithMethods(HttpMethods.Get, HttpMethods.Post)
    .WithHeaders("Content-Type", "Authorization", Idempotent.KeyHeader)
    .WithExposedHeaders(Idempotent.ReplayedHeader, "Location")));

// A request that cannot be read is the caller's mistake in every environment. The default
// in Development is to throw, which turns it into a 500.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));

var command = AdminCommands.Handles(args);
if (command)
{
    // A command prints its own result. On an empty database EF also logs its failed probe
    // for the history table as an error before creating it; a migration that really fails throws.
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
}

var app = builder.Build();

// `dotnet run -- migrate` and `dotnet run -- keys ...` do one thing and exit.
// The server never migrates on its own.
if (command)
{
    Environment.ExitCode = await AdminCommands.RunAsync(args, app.Services, Console.Out);
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi().AllowAnonymous();
app.MapGet("/health", async (LedgerDbContext db, CancellationToken cancellationToken) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "ok" })
            : Results.Json(new { status = "database_unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous()
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
