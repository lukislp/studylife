using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using StudyLife.Server.Auth;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests.PropertyBased;

/// <summary>
/// Property-based tests (FsCheck) for the small pure parsers and policies that sit between
/// untrusted input and the rest of the server. Unlike the example-based tests next to them,
/// these run each property against hundreds of generated inputs - including the strings that
/// nobody would think to write down: empty, whitespace-only, unicode, absurdly long.
/// </summary>
public class ParserPropertyTests
{
    // ---------------------------------------------------------------- OutboundUrlPolicy

    [Property(MaxTest = 500)]
    public bool OutboundUrlPolicy_never_throws(string? url)
    {
        // The policies sit in front of Uri.TryCreate on user input; an exception here would
        // turn a bad URL into a 500 instead of a 400.
        OutboundUrlPolicy.IsAcceptablePushEndpoint(url);
        OutboundUrlPolicy.IsAcceptableWebhookTarget(url);
        return true;
    }

    [Property(MaxTest = 500)]
    public bool Accepted_push_endpoint_is_absolute_https_without_userinfo_and_within_length(string? url)
    {
        if (!OutboundUrlPolicy.IsAcceptablePushEndpoint(url)) return true;
        return url is not null
            && url.Length <= OutboundUrlPolicy.MaxLength
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    [Property(MaxTest = 500)]
    public bool Accepted_https_webhook_target_is_also_an_accepted_push_endpoint(string? url)
    {
        if (!OutboundUrlPolicy.IsAcceptableWebhookTarget(url)) return true;
        var isHttps = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
        return !isHttps || OutboundUrlPolicy.IsAcceptablePushEndpoint(url);
    }

    [Property(MaxTest = 300)]
    public bool Private_and_loopback_hosts_are_never_accepted(byte a, byte b, byte c, byte d, bool https)
    {
        // Generated dotted-quad hosts: anything in the private, loopback or link-local ranges
        // must be refused whatever the rest of the URL looks like.
        var ip = $"{a}.{b}.{c}.{d}";
        var isPrivate = a == 10 || a == 127 || (a == 192 && b == 168) || (a == 172 && b >= 16 && b <= 31)
                        || (a == 169 && b == 254) || a == 0;
        if (!isPrivate) return true;
        var url = $"{(https ? "https" : "http")}://{ip}/hook";
        return !OutboundUrlPolicy.IsAcceptableWebhookTarget(url) && !OutboundUrlPolicy.IsAcceptablePushEndpoint(url);
    }

    // ---------------------------------------------------------------- CommaSeparatedIds

    [Property(MaxTest = 500)]
    public bool CommaSeparatedIds_never_throws(string? raw)
    {
        var parsed = CommaSeparatedIds.Parse(raw);
        return parsed is not null;
    }

    [Property(MaxTest = 300)]
    public bool CommaSeparatedIds_round_trips_every_int_list(int[] ids)
    {
        var parsed = CommaSeparatedIds.Parse(string.Join(",", ids));
        return parsed.SequenceEqual(ids);
    }

    [Property(MaxTest = 300)]
    public bool CommaSeparatedIds_survives_garbage_between_valid_ids(int[] ids, NonEmptyString garbage)
    {
        // A malformed token must be skipped, never poison the whole value (that is the reason
        // this parser exists - see its doc comment).
        if (garbage.Get.Contains(',') || int.TryParse(garbage.Get.Trim(), out _)) return true;
        var raw = string.Join(",", ids.Select(i => i.ToString()).Append(garbage.Get));
        return CommaSeparatedIds.Parse(raw).SequenceEqual(ids);
    }

    // ---------------------------------------------------------------- ReminderSettings

    [Property(MaxTest = 500)]
    public bool ReminderSettings_always_yield_a_non_empty_list(string? raw)
    {
        return ReminderSettings.ParseSessionReminderMinutes(raw).Length > 0
            && ReminderSettings.ParseCourseGoalReminderDays(raw).Length > 0;
    }

    [Property(MaxTest = 300)]
    public bool ReminderSettings_round_trip_a_non_empty_int_list(NonEmptyArray<int> values)
    {
        var raw = string.Join(",", values.Get);
        return ReminderSettings.ParseSessionReminderMinutes(raw).SequenceEqual(values.Get);
    }

    // ---------------------------------------------------------------- ApiKeyScopes

    [Property(MaxTest = 300)]
    public bool ApiKeyScopes_serialize_then_parse_yields_the_same_set(ApiKeyScopes.Endpoint[] endpoints)
    {
        var wellFormed = endpoints
            .Where(e => !string.IsNullOrWhiteSpace(e.Controller) && !string.IsNullOrWhiteSpace(e.Action)
                        && !e.Controller.Contains(',') && !e.Controller.Contains('.')
                        && !e.Action.Contains(',')
                        && e.Controller.Trim() == e.Controller && e.Action.Trim() == e.Action)
            .ToHashSet();
        var parsed = ApiKeyScopes.Parse(ApiKeyScopes.Serialize(wellFormed));
        return parsed.SetEquals(wellFormed);
    }

    [Property(MaxTest = 500)]
    public bool ApiKeyScopes_parse_never_throws_and_never_yields_an_empty_part(string? scopes)
    {
        var parsed = ApiKeyScopes.Parse(scopes);
        return parsed.All(e => e.Controller.Length > 0 && e.Action.Length > 0);
    }
}
