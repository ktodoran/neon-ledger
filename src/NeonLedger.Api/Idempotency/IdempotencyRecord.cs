namespace NeonLedger.Idempotency;

public sealed class IdempotencyRecord
{
    public const int MaxKeyLength = 255;

    /// <summary>Whose key this is. Two callers may use the same key text without meeting.</summary>
    public Guid CallerId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    /// <summary>SHA-256 of the operation and request, so a key cannot be reused for a different request.</summary>
    public string RequestHash { get; set; } = string.Empty;

    public int? StatusCode { get; set; }

    public string? ResponseBody { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
