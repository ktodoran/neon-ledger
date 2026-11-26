using System.Diagnostics.CodeAnalysis;

namespace NeonLedger.Ledger;

/// <summary>Either a value or the reason the ledger refused the request.</summary>
public readonly struct Result<T>
{
    private Result(T? value, LedgerError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public LedgerError? Error { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static implicit operator Result<T>(T value) => new(value, null);

    public static implicit operator Result<T>(LedgerError error) => new(default, error);
}
