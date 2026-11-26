using System.Text.Json;
using NeonLedger.Events;

namespace NeonLedger.Api;

public static class EventEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    public static void MapEventEndpoints(this IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/events", ListAsync)
            .WithTags("Events")
            .Produces<EventPage>()
            .ProducesValidationProblem();

    private static async Task<IResult> ListAsync(
        EventLog events,
        CancellationToken cancellationToken,
        string? after = null,
        int limit = DefaultPageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (!EventCursor.TryParse(after, out var cursor))
        {
            errors["after"] = ["after must be a cursor returned by this endpoint."];
        }

        if (limit is < 1 or > MaxPageSize)
        {
            errors["limit"] = [$"limit must be between 1 and {MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var stored = await events.ReadAsync(cursor, limit, cancellationToken);
        var items = stored.Select(e => new EventResponse(e.Cursor.ToString(), e.Type, e.OccurredAt, Parse(e.Payload))).ToList();
        var next = stored.Count > 0 ? stored[^1].Cursor : cursor;
        return Results.Ok(new EventPage(items, next.ToString()));
    }

    private static JsonElement Parse(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }
}
