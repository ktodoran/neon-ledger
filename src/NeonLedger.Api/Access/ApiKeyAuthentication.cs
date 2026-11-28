using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NeonLedger.Api;
using NeonLedger.Data;

namespace NeonLedger.Access;

/// <summary>
/// Authenticates a request by the API key in its <c>Authorization: Bearer</c> header.
///
/// Every request looks its key up in the database. That is one indexed read per request,
/// and it is what makes a revoked key stop working at once instead of when a cache expires.
/// </summary>
public sealed class ApiKeyAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LedgerDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string CallerIdClaim = "caller_id";
    public const string AccessClaim = "access";

    private const string Bearer = "Bearer ";

    // Compared against when no key has the presented id, so that case does the same work as a wrong secret.
    private static readonly string NoSuchKeyHash = new('0', 64);

    /// <summary>
    /// The rule for every endpoint that does not say otherwise: a caller must be authenticated,
    /// and anything but a read needs a key with write access. New endpoints are covered without
    /// having to remember to ask.
    /// </summary>
    public static AuthorizationPolicy Policy { get; } = new AuthorizationPolicyBuilder(SchemeName)
        .RequireAuthenticatedUser()
        .RequireAssertion(context =>
            context.Resource is HttpContext http
            && (HttpMethods.IsGet(http.Request.Method)
                || HttpMethods.IsHead(http.Request.Method)
                || context.User.HasClaim(AccessClaim, nameof(AccessLevel.Write))))
        .Build();

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        if (!header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase)
            || !ApiKey.TryParse(header[Bearer.Length..].Trim(), out var id, out var secret))
        {
            return AuthenticateResult.Fail("The Authorization header does not hold an API key.");
        }

        var key = await db.ApiKeys.AsNoTracking()
            .Where(k => k.Id == id)
            .Select(k => new { k.SecretHash, k.Access, k.RevokedAt, k.CallerId, CallerName = k.Caller.Name })
            .SingleOrDefaultAsync(Context.RequestAborted);

        var matches = ApiKey.Matches(key?.SecretHash ?? NoSuchKeyHash, secret);
        if (key is null || !matches || key.RevokedAt is not null)
        {
            return AuthenticateResult.Fail("The API key is unknown or revoked.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(CallerIdClaim, key.CallerId.ToString()),
                new Claim(ClaimTypes.Name, key.CallerName),
                new Claim(AccessClaim, key.Access.ToString()),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // One answer for a key that is mistyped, unknown or revoked: which of them it is
        // is nobody's business but the key's owner, who can look at `keys list`.
        var detail = Request.Headers.Authorization.Count == 0
            ? "Send an API key in the Authorization header: Bearer <key>."
            : "This API key was not accepted. It may be mistyped or revoked.";
        Response.Headers.WWWAuthenticate = "Bearer";
        await Problems.Result(StatusCodes.Status401Unauthorized, "unauthenticated", detail).ExecuteAsync(Context);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Problems.Result(StatusCodes.Status403Forbidden, "forbidden", "This API key can read but not write.").ExecuteAsync(Context);
}

/// <summary>The caller behind the current request.</summary>
public sealed class CurrentCaller(IHttpContextAccessor http)
{
    public Guid Id =>
        Guid.Parse(http.HttpContext?.User.FindFirstValue(ApiKeyAuthentication.CallerIdClaim)
            ?? throw new InvalidOperationException("This request has no authenticated caller."));
}
