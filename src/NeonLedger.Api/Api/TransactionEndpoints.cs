using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeonLedger.Data;
using NeonLedger.Domain;
using NeonLedger.Idempotency;
using NeonLedger.Ledger;

namespace NeonLedger.Api;

public static class TransactionEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

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
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        LedgerService ledger,
        IdempotentExecutor idempotency,
        CancellationToken cancellationToken)
    {
        if (ValidateKey(idempotencyKey) is { } keyProblem)
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
            idempotencyKey!,
            "post_transaction",
            command,
            async ct => Describe(await ledger.PostAsync(command, ct)),
            cancellationToken);
        return ToResult(result);
    }

    private static async Task<IResult> ReverseAsync(
        Guid id,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        LedgerService ledger,
        IdempotentExecutor idempotency,
        CancellationToken cancellationToken)
    {
        if (ValidateKey(idempotencyKey) is { } keyProblem)
        {
            return keyProblem;
        }

        var result = await idempotency.RunAsync(
            idempotencyKey!,
            "reverse_transaction",
            id,
            async ct => Describe(await ledger.ReverseAsync(id, ct)),
            cancellationToken);
        return ToResult(result);
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

    private static IResult? ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Problems.Result(
                StatusCodes.Status400BadRequest,
                "idempotency_key_required",
                $"Send an {IdempotencyKeyHeader} header. A retry with the same key will not post twice.");
        }

        return key.Length > IdempotencyRecord.MaxKeyLength
            ? Problems.Result(
                StatusCodes.Status400BadRequest,
                "idempotency_key_too_long",
                $"An {IdempotencyKeyHeader} can be at most {IdempotencyRecord.MaxKeyLength} characters.")
            : null;
    }

    private static IResult ToResult(IdempotencyResult result)
    {
        if (result.Outcome == IdempotencyOutcome.KeyReused)
        {
            return Problems.Result(
                StatusCodes.Status422UnprocessableEntity,
                "idempotency_key_reused",
                $"This {IdempotencyKeyHeader} was already used for a different request.");
        }

        return new StoredResult(result.Response!);
    }

    /// <summary>Writes the stored bytes as they are, so a replay is identical to the first response.</summary>
    private sealed class StoredResult(IdempotentResponse response) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var http = httpContext.Response;
            http.StatusCode = response.StatusCode;
            http.ContentType = response.StatusCode >= 400 ? "application/problem+json" : "application/json";
            if (response.Replayed)
            {
                http.Headers[ReplayedHeader] = "true";
            }

            if (response.StatusCode == StatusCodes.Status201Created)
            {
                using var document = JsonDocument.Parse(response.Body);
                http.Headers.Location = $"/v1/transactions/{document.RootElement.GetProperty("id").GetString()}";
            }

            return http.WriteAsync(response.Body, httpContext.RequestAborted);
        }
    }
}
