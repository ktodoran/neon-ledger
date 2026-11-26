using System.Globalization;

namespace NeonLedger.Events;

/// <summary>A position in the event feed: everything up to and including one event.</summary>
public readonly record struct EventCursor(long Txid, long Id)
{
    /// <summary>Before the first event.</summary>
    public static readonly EventCursor Start = new(0, 0);

    public static bool TryParse(string? text, out EventCursor cursor)
    {
        cursor = Start;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var parts = text.Split('-');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var txid)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            cursor = new EventCursor(txid, id);
            return true;
        }

        return false;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Txid}-{Id}");
}
