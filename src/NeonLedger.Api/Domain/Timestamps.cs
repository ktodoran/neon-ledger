namespace NeonLedger.Domain;

public static class Timestamps
{
    /// <summary>
    /// PostgreSQL keeps microseconds; .NET keeps tenths of a microsecond. Dropping the
    /// extra digit before saving means the value returned when something is created is
    /// the same value every later read returns.
    /// </summary>
    public static DateTimeOffset ToStoredPrecision(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond), value.Offset);
}
