using Microsoft.AspNetCore.Routing;

namespace StudyLife.Server.Services;

/// <summary>
/// Normalizes a client-reported API route (docs/ARCHITECTURE.md "Telemetry" contract) into a
/// bounded tag value for <see cref="ClientTelemetryMetrics.ApiDuration"/>/<see
/// cref="ClientTelemetryMetrics.ApiRequests"/>: strip the query string, replace any path segment
/// that is an integer, a GUID, or longer than 40 characters with <c>{id}</c>, then check the
/// result against the server's OWN route table (<see cref="EndpointDataSource"/>) - a route the
/// server doesn't actually expose collapses to <c>other</c> instead of becoming free-form
/// cardinality (a client could otherwise send literally anything as "route").
///
/// The known-route set is built once, lazily, from every mapped controller action whose pattern
/// starts with "api/" - parameter segments (e.g. "{id}", "{token:guid}") are reduced to a bare
/// placeholder before comparison, since the client's own normalization always produces "{id}"
/// regardless of what the server happened to name its own route parameter.
/// </summary>
public sealed class TelemetryRouteCatalog
{
    private const string Placeholder = "{id}";

    private readonly Lazy<HashSet<string>> _knownRoutes;

    public TelemetryRouteCatalog(IEnumerable<EndpointDataSource> dataSources) =>
        _knownRoutes = new Lazy<HashSet<string>>(() => BuildKnownRoutes(dataSources));

    public string Normalize(string? rawRoute)
    {
        if (string.IsNullOrWhiteSpace(rawRoute)) return "other";

        var path = rawRoute.Split('?', 2)[0].Trim('/');
        if (path.Length == 0) return "other";

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (IsIdLikeSegment(segments[i]))
                segments[i] = Placeholder;
        }

        var normalized = string.Join('/', segments);
        return _knownRoutes.Value.Contains(normalized) ? normalized : "other";
    }

    private static bool IsIdLikeSegment(string segment) =>
        segment.Length > 40 || long.TryParse(segment, out _) || Guid.TryParse(segment, out _);

    private static HashSet<string> BuildKnownRoutes(IEnumerable<EndpointDataSource> dataSources)
    {
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dataSource in dataSources)
        {
            foreach (var endpoint in dataSource.Endpoints)
            {
                if (endpoint is not RouteEndpoint routeEndpoint) continue;
                var rawText = routeEndpoint.RoutePattern.RawText;
                if (string.IsNullOrEmpty(rawText)) continue;
                var trimmed = rawText.Trim('/');
                if (!trimmed.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) continue;

                var segments = trimmed.Split('/');
                for (var i = 0; i < segments.Length; i++)
                {
                    if (segments[i].StartsWith('{'))
                        segments[i] = Placeholder;
                }
                routes.Add(string.Join('/', segments));
            }
        }
        return routes;
    }
}
