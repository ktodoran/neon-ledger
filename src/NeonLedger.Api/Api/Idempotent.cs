using System.Text.Json;
using NeonLedger.Idempotency;

namespace NeonLedger.Api;

/// <summary>The HTTP side of idempotency: the request header, and turning a stored outcome into a response.</summary>
public static class Idempotent
{
    public const string KeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    /// <summary>Returns a problem if the key is missing or too long, otherwise null.</summary>
    public static IResult? ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Problems.Result(
                StatusCodes.Status400BadRequest,
                "idempotency_key_required",
                $"Send an {KeyHeader} header. A retry with the same key will not act twice.");
        }

        return key.Length > IdempotencyRecord.MaxKeyLength
            ? Problems.Result(
                StatusCodes.Status400BadRequest,
                "idempotency_key_too_long",
                $"An {KeyHeader} can be at most {IdempotencyRecord.MaxKeyLength} characters.")
            : null;
    }

    /// <param name="result">What the executor returned.</param>
    /// <param name="createdAt">For 201 responses, the path the new resource lives under, such as <c>/v1/payments/</c>.</param>
    public static IResult ToResult(IdempotencyResult result, string? createdAt = null)
    {
        if (result.Outcome == IdempotencyOutcome.KeyReused)
        {
            return Problems.Result(
                StatusCodes.Status422UnprocessableEntity,
                "idempotency_key_reused",
                $"This {KeyHeader} was already used for a different request.");
        }

        return new StoredResult(result.Response!, createdAt);
    }

    /// <summary>Writes the stored bytes as they are, so a replay is identical to the first response.</summary>
    private sealed class StoredResult(IdempotentResponse response, string? createdAt) : IResult
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

            if (response.StatusCode == StatusCodes.Status201Created && createdAt is not null)
            {
                using var document = JsonDocument.Parse(response.Body);
                http.Headers.Location = createdAt + document.RootElement.GetProperty("id").GetString();
            }

            return http.WriteAsync(response.Body, httpContext.RequestAborted);
        }
    }
}
