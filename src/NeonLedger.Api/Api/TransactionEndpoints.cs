using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeonLedger.Access;
using NeonLedger.Data;
using NeonLedger.Domain;
using NeonLedger.Idempotency;
using NeonLedger.Ledger;

namespace NeonLedger.Api;

public static class TransactionEndpoints
{
    public static void MapTransactionEndpoints(this IEndpointRouteBuilder routes)
    {
        var transactions = routes.MapGroup("/v1/transactions").WithTags("Transactions");

        transactions.MapPost("/", PostAsync)
            .Produces<TransactionResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        transactions.MapGet("/{id:guid}", GetAsync)
            .Produces<TransactionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        transactions.MapPost("/{id:guid}/reversal", ReverseAsync)
            .Produces<TransactionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> PostAsync(
        PostTransactionRequest request,
        [FromHeader(Name = Idempotent.KeyHeader)] string? idempotencyKey,
        LedgerService ledger,
        IdempotentExecutor idempotency,
        CurrentCaller caller,
        CancellationToken cancellationToken)
    {
        if (Idempotent.ValidateKey(idempotencyKey) is { } keyProblem)
        {
            return keyProblem;
        }

        var errors = new Dictionary<string, string[]>();
        var entries = request.Entries ?? [];
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is null)
            {
                errors[$"entries[{i}]"] = ["An entry cannot be null."];
            }
            else if (entries[i]!.Direction is null)
            {
                errors[$"entries[{i}].direction"] = ["direction is required: debit or credit."];
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var command = new PostTransaction(
            request.Description?.Trim() ?? string.Empty,
            [.. entries.Select(e => new EntryLine(e!.AccountId, e.Direction!.Value, e.Amount))]);
        errors = PostTransactionValidator.Validate(command);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var result = await idempotency.RunAsync(
            caller.Id,
            idempotencyKey!,
            "post_transaction",
            command,
            async ct => Describe(await ledger.PostAsync(command, ct)),
            cancellationToken);
        return Idempotent.ToResult(result, createdAt: "/v1/transactions/");
    }

    private static async Task<IResult> ReverseAsync(
        Guid id,
        [FromHeader(Name = Idempotent.KeyHeader)] string? idempotencyKey,
        LedgerService ledger,
        IdempotentExecutor idempotency,
        CurrentCaller caller,
        CancellationToken cancellationToken)
    {
        if (Idempotent.ValidateKey(idempotencyKey) is { } keyProblem)
        {
            return keyProblem;
        }

        var result = await idempotency.RunAsync(
            caller.Id,
            idempotencyKey!,
            "reverse_transaction",
            id,
            async ct => Describe(await ledger.ReverseAsync(id, ct)),
            cancellationToken);
        return Idempotent.ToResult(result, createdAt: "/v1/transactions/");
    }

    private static async Task<IResult> GetAsync(Guid id, LedgerDbContext db, CancellationToken cancellationToken)
    {
        var transaction = await db.Transactions
            .AsNoTracking()
            .Include(t => t.Entries)
            .SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        return transaction is null
            ? Problems.Result(LedgerError.TransactionNotFound(id))
            : Results.Ok(TransactionResponse.From(transaction));
    }

    private static (int StatusCode, object Body) Describe(Result<JournalTransaction> result) =>
        result.IsSuccess
            ? (StatusCodes.Status201Created, TransactionResponse.From(result.Value))
            : (result.Error.Status, Problems.From(result.Error));
}
