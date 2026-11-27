using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NeonLedger.Data;
using NeonLedger.Domain;
using NeonLedger.Ledger;

namespace NeonLedger.Api;

public static partial class AccountEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    public static void MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        var accounts = routes.MapGroup("/v1/accounts").WithTags("Accounts");

        accounts.MapPost("/", CreateAsync)
            .Produces<AccountResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        accounts.MapGet("/", ListAsync)
            .Produces<AccountPage>()
            .ProducesValidationProblem();
        accounts.MapGet("/{id:guid}", GetAsync)
            .WithName("GetAccount")
            .Produces<AccountResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        accounts.MapGet("/{id:guid}/entries", ListEntriesAsync)
            .Produces<EntryPage>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> CreateAsync(
        CreateAccountRequest request,
        LedgerDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors["name"] = ["A name is required."];
        }
        else if (name.Length > Account.MaxNameLength)
        {
            errors["name"] = [$"A name can be at most {Account.MaxNameLength} characters."];
        }

        if (request.Currency is null || !CurrencyCode().IsMatch(request.Currency))
        {
            errors["currency"] = ["A currency is a three-letter ISO 4217 code in capitals, such as USD."];
        }

        if (request.NormalBalance is null)
        {
            errors["normalBalance"] = ["normalBalance is required: debit or credit."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var account = Account.Open(name!, request.Currency!, request.NormalBalance!.Value, request.AllowNegative, clock.GetUtcNow());
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);

        return Results.CreatedAtRoute("GetAccount", new { id = account.Id }, AccountResponse.From(account));
    }

    private static async Task<IResult> ListAsync(
        LedgerDbContext db,
        CancellationToken cancellationToken,
        int limit = DefaultPageSize,
        string? after = null)
    {
        var errors = new Dictionary<string, string[]>();
        Guid? cursor = null;
        if (!string.IsNullOrEmpty(after))
        {
            if (Guid.TryParse(after, out var id))
            {
                cursor = id;
            }
            else
            {
                errors["after"] = ["after must be an account id returned by this endpoint."];
            }
        }

        if (limit is < 1 or > MaxPageSize)
        {
            errors["limit"] = [$"limit must be between 1 and {MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        // Keyset pagination on the primary key. Ids are version 7 UUIDs, which PostgreSQL
        // orders by the time inside them, so this is also the order the accounts were opened in.
        var rows = await db.Accounts.AsNoTracking()
            .Where(a => cursor == null || a.Id > cursor)
            .OrderBy(a => a.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        var items = (hasMore ? rows[..limit] : rows).Select(AccountResponse.From).ToList();
        return Results.Ok(new AccountPage(items, hasMore ? items[^1].Id : null));
    }

    private static async Task<IResult> GetAsync(Guid id, LedgerDbContext db, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        return account is null
            ? Problems.Result(LedgerError.AccountNotFound(id) with { Status = StatusCodes.Status404NotFound })
            : Results.Ok(AccountResponse.From(account));
    }

    private static async Task<IResult> ListEntriesAsync(
        Guid id,
        LedgerDbContext db,
        CancellationToken cancellationToken,
        int limit = DefaultPageSize,
        long? before = null)
    {
        if (limit is < 1 or > MaxPageSize)
        {
            return Problems.Validation(new() { ["limit"] = [$"limit must be between 1 and {MaxPageSize}."] });
        }

        if (!await db.Accounts.AnyAsync(a => a.Id == id, cancellationToken))
        {
            return Problems.Result(LedgerError.AccountNotFound(id) with { Status = StatusCodes.Status404NotFound });
        }

        // Keyset pagination on the entry id: stable while new entries arrive, and one index seek per page.
        var query =
            from entry in db.Entries.AsNoTracking()
            join transaction in db.Transactions.AsNoTracking() on entry.TransactionId equals transaction.Id
            where entry.AccountId == id && (before == null || entry.Id < before)
            orderby entry.Id descending
            select new AccountEntryResponse(
                entry.Id,
                entry.TransactionId,
                transaction.Description,
                entry.Direction,
                entry.Amount,
                entry.BalanceAfter,
                transaction.PostedAt);

        var rows = await query.Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        var items = hasMore ? rows[..limit] : rows;
        return Results.Ok(new EntryPage(items, hasMore ? items[^1].Id : null));
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();
}
