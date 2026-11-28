using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeonLedger.Access;
using NeonLedger.Data;
using NeonLedger.Idempotency;
using NeonLedger.Ledger;
using NeonLedger.Payments;

namespace NeonLedger.Api;

public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this IEndpointRouteBuilder routes)
    {
        var payments = routes.MapGroup("/v1/payments").WithTags("Payments");

        payments.MapPost("/", CreateAsync)
            .Produces<PaymentResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        payments.MapGet("/{id:guid}", GetAsync)
            .Produces<PaymentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        payments.MapPost("/{id:guid}/settle", SettleAsync).ProducesTransition();
        payments.MapPost("/{id:guid}/cancel", CancelAsync).ProducesTransition();
        payments.MapPost("/{id:guid}/return", ReturnAsync).ProducesTransition().ProducesValidationProblem();
    }

    private static async Task<IResult> CreateAsync(
        CreatePaymentRequest request,
        [FromHeader(Name = Idempotent.KeyHeader)] string? idempotencyKey,
        PaymentService payments,
        IdempotentExecutor idempotency,
        CurrentCaller caller,
        CancellationToken cancellationToken)
    {
        if (Idempotent.ValidateKey(idempotencyKey) is { } keyProblem)
        {
            return keyProblem;
        }

        var errors = new Dictionary<string, string[]>();
        var description = request.Description?.Trim() ?? string.Empty;
        if (request.Rail is null)
        {
            errors["rail"] = ["rail is required: instant or ach."];
        }

        if (request.SourceAccountId == Guid.Empty)
        {
            errors["sourceAccountId"] = ["A source account id is required."];
        }

        if (request.DestinationAccountId == Guid.Empty)
        {
            errors["destinationAccountId"] = ["A destination account id is required."];
        }
        else if (request.DestinationAccountId == request.SourceAccountId)
        {
            errors["destinationAccountId"] = ["A payment needs two different accounts."];
        }

        if (request.Amount is <= 0 or > LedgerService.MaxAmount)
        {
            errors["amount"] = [$"An amount is a whole number of minor units between 1 and {LedgerService.MaxAmount}."];
        }

        if (description.Length == 0)
        {
            errors["description"] = ["A description is required."];
        }
        else if (description.Length > Payment.MaxDescriptionLength)
        {
            errors["description"] = [$"A description can be at most {Payment.MaxDescriptionLength} characters."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var command = new CreatePayment(request.Rail!.Value, request.SourceAccountId, request.DestinationAccountId, request.Amount, description);
        var result = await idempotency.RunAsync(
            caller.Id,
            idempotencyKey!,
            "create_payment",
            command,
            async ct => Describe(await payments.CreateAsync(command, ct), StatusCodes.Status201Created),
            cancellationToken);
        return Idempotent.ToResult(result, createdAt: "/v1/payments/");
    }

    private static Task<IResult> SettleAsync(
        Guid id,
        [FromHeader(Name = Idempotent.KeyHeader)] string? idempotencyKey,
        PaymentService payments,
        IdempotentExecutor idempotency,
        CurrentCaller caller,
        CancellationToken cancellationToken) =>
        TransitionAsync(idempotencyKey, "settle_payment", id, idempotency, caller, ct => payments.SettleAsync(id, ct), cancellationToken);

    private static Task<IResult> CancelAsync(
        Guid id,
        [FromHeader(Name = Idempotent.KeyHeader)] string? idempotencyKey,
        PaymentService payments,
        IdempotentExecutor idempotency,
        CurrentCaller caller,
        CancellationToken cancellationToken) =>
        TransitionAsync(idempotencyKey, "cancel_payment", id, idempotency, caller, ct => payments.CancelAsync(id, ct), cancellationToken);

    private static Task<IResult> ReturnAsync(
        Guid id,
        ReturnPaymentRequest request,
        [FromHeader(Name = Idempotent.KeyHeader)] string? idempotencyKey,
        PaymentService payments,
        IdempotentExecutor idempotency,
        CurrentCaller caller,
        CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > Payment.MaxReturnReasonLength)
        {
            return Task.FromResult(Problems.Validation(new()
            {
                ["reason"] = [$"A reason of 1 to {Payment.MaxReturnReasonLength} characters is required, such as an ACH return code."],
            }));
        }

        return TransitionAsync(
            idempotencyKey,
            "return_payment",
            new { id, reason },
            idempotency,
            caller,
            ct => payments.ReturnAsync(id, reason, ct),
            cancellationToken);
    }

    private static async Task<IResult> TransitionAsync<TRequest>(
        string? idempotencyKey,
        string operation,
        TRequest request,
        IdempotentExecutor idempotency,
        CurrentCaller caller,
        Func<CancellationToken, Task<Result<Payment>>> action,
        CancellationToken cancellationToken)
    {
        if (Idempotent.ValidateKey(idempotencyKey) is { } keyProblem)
        {
            return keyProblem;
        }

        var result = await idempotency.RunAsync(
            caller.Id,
            idempotencyKey!,
            operation,
            request,
            async ct => Describe(await action(ct), StatusCodes.Status200OK),
            cancellationToken);
        return Idempotent.ToResult(result);
    }

    private static async Task<IResult> GetAsync(Guid id, LedgerDbContext db, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return payment is null
            ? Problems.Result(LedgerError.PaymentNotFound(id))
            : Results.Ok(PaymentResponse.From(payment));
    }

    private static (int StatusCode, object Body) Describe(Result<Payment> result, int successStatus) =>
        result.IsSuccess
            ? (successStatus, PaymentResponse.From(result.Value))
            : (result.Error.Status, Problems.From(result.Error));

    private static RouteHandlerBuilder ProducesTransition(this RouteHandlerBuilder builder) =>
        builder
            .Produces<PaymentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
}
