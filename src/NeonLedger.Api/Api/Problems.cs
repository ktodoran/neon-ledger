using Microsoft.AspNetCore.Mvc;
using NeonLedger.Ledger;

namespace NeonLedger.Api;

/// <summary>RFC 9457 problem bodies. Every refusal carries a stable <c>code</c> a client can branch on.</summary>
public static class Problems
{
    public static ProblemDetails From(LedgerError error) => Create(error.Status, error.Code, error.Message);

    public static ProblemDetails Create(int status, string code, string detail) =>
        new()
        {
            Type = Type(status),
            Status = status,
            Title = Title(status),
            Detail = detail,
            Extensions = { ["code"] = code },
        };

    public static IResult Result(LedgerError error) => Results.Problem(From(error));

    public static IResult Result(int status, string code, string detail) => Results.Problem(Create(status, code, detail));

    public static IResult Validation(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" });

    private static string Type(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        StatusCodes.Status404NotFound => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        StatusCodes.Status409Conflict => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        StatusCodes.Status422UnprocessableEntity => "https://tools.ietf.org/html/rfc9110#section-15.5.21",
        _ => "about:blank",
    };

    private static string Title(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Bad request",
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status422UnprocessableEntity => "Request refused",
        _ => "Error",
    };
}
