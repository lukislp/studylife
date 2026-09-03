using System.Globalization;
using StudyLife.Shared;

namespace StudyLife.Server.Services;

/// <summary>
/// Minimal, hand-written .ics parser for importing external calendars (e.g. a
/// university's official lecture-schedule export) - the counterpart to SessionsController.GetIcs
/// (that one writes, this one reads). Deliberately no NuGet package for this (see task scope):
/// pure VEVENT/DTSTART/DTEND/SUMMARY/DESCRIPTION parsing.
///
/// Deliberately NOT supported: full RRULE expansion (exceptions, UNTIL, BYDAY rules;
/// timezone-aware recurrence is its own large problem) - a VEVENT with RRULE
/// only yields its first occurrence, marked with HasUnexpandedRecurrence=true.
/// </summary>
public static class IcsImportParser
{
    private sealed record PropertyValue(Dictionary<string, string> Params, string Value);

    public static List<IcsImportEventDto> Parse(string icsContent)
    {
        var events = new List<IcsImportEventDto>();
        Dictionary<string, PropertyValue>? current = null;

        foreach (var line in Unfold(icsContent))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, PropertyValue>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null)
                {
                    var dto = BuildEvent(current);
                    if (dto != null) events.Add(dto);
                }
                current = null;
                continue;
            }
            // Ignore anything outside a VEVENT (VCALENDAR header, VTIMEZONE block) - for the
            // deliberately bounded scope, best-effort timezone handling via TZID directly
            // on DTSTART/DTEND (see ParseDateTime) is enough, no VTIMEZONE evaluation.
            if (current == null) continue;

            var property = SplitProperty(line);
            if (property == null) continue;
            var (name, value) = property.Value;
            current[name] = value;
        }

        return events;
    }

    /// <summary>
    /// Unfolds folded lines (RFC 5545: a continuation line starts with a
    /// space or tab and belongs to the previous logical line) and normalizes
    /// line endings.
    /// </summary>
    private static List<string> Unfold(string content)
    {
        var rawLines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var result = new List<string>();
        // Accumulate each logical line in a StringBuilder and flush it once the next non-
        // continuation line starts. The previous `result[^1] += raw[1..]` re-copied the whole
        // growing string per continuation line - O(n²) over a 10 MB upload made of millions of
        // two-character continuation lines pinned a request thread for minutes (2026-09 audit S3).
        var current = new System.Text.StringBuilder();
        var hasCurrent = false;
        foreach (var raw in rawLines)
        {
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && hasCurrent)
            {
                current.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (hasCurrent)
            {
                result.Add(current.ToString());
                current.Clear();
                hasCurrent = false;
            }
            if (raw.Length > 0)
            {
                current.Append(raw);
                hasCurrent = true;
            }
        }
        if (hasCurrent) result.Add(current.ToString());
        return result;
    }

    /// <summary>Splits "NAME;PARAM=VALUE;...:VALUE" into name + parameter dictionary + value.</summary>
    private static (string Name, PropertyValue Value)? SplitProperty(string line)
    {
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return null;
        var head = line[..colonIdx];
        var value = line[(colonIdx + 1)..];
        var parts = head.Split(';');
        var name = parts[0].Trim().ToUpperInvariant();
        if (name.Length == 0) return null;

        var paramsDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq < 0) continue;
            paramsDict[parts[i][..eq]] = parts[i][(eq + 1)..];
        }
        return (name, new PropertyValue(paramsDict, value));
    }

    private static IcsImportEventDto? BuildEvent(Dictionary<string, PropertyValue> props)
    {
        if (!props.TryGetValue("DTSTART", out var dtstartProp)) return null; // unusable without a start
        var start = ParseDateTime(dtstartProp);
        if (start == null) return null;

        DateTime end;
        if (props.TryGetValue("DTEND", out var dtendProp))
        {
            end = ParseDateTime(dtendProp) ?? start.Value.AddHours(1);
        }
        else
        {
            // No DTEND: all-day events (VALUE=DATE) get a full day, everything
            // else a 1h default - both more sensible than a 0-minute session.
            var isDateOnly = dtstartProp.Params.TryGetValue("VALUE", out var v)
                && v.Equals("DATE", StringComparison.OrdinalIgnoreCase);
            end = isDateOnly ? start.Value.AddDays(1) : start.Value.AddHours(1);
        }
        if (end <= start.Value) end = start.Value.AddHours(1); // defensive against broken third-party exports

        var title = props.TryGetValue("SUMMARY", out var summaryProp) ? Unescape(summaryProp.Value).Trim() : "";
        var description = props.TryGetValue("DESCRIPTION", out var descProp) ? Unescape(descProp.Value).Trim() : null;

        return new IcsImportEventDto
        {
            Title = title,
            StartTime = start.Value,
            EndTime = end,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            HasUnexpandedRecurrence = props.ContainsKey("RRULE"),
        };
    }

    /// <summary>
    /// Parses DTSTART/DTEND. Three cases: all-day (VALUE=DATE, "yyyyMMdd"), UTC ("...Z",
    /// converted via ToLocalTime to the server timezone), and "floating" (no Z, no
    /// resolvable TZID) - the latter is already naive local time and fits 1:1 with the
    /// app convention (see docs/ARCHITECTURE.md, timezone section: StartTime/EndTime
    /// are naive local timestamps, compared against the server's local time).
    /// A TZID parameter is resolved best-effort via TimeZoneInfo.FindSystemTimeZoneById
    /// (works for IANA ids on modern .NET/Windows and Linux) - an unknown or
    /// unresolvable TZID (e.g. exotic legacy Windows ids) deliberately degrades silently to
    /// "floating" instead of building a dedicated IANA/Windows alias table.
    /// </summary>
    private static DateTime? ParseDateTime(PropertyValue prop)
    {
        var value = prop.Value.Trim();
        if (value.Length == 0) return null;

        var isDateOnly = (prop.Params.TryGetValue("VALUE", out var v) && v.Equals("DATE", StringComparison.OrdinalIgnoreCase))
            || (value.Length == 8 && !value.Contains('T'));
        if (isDateOnly)
        {
            return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly)
                ? dateOnly
                : null;
        }

        var isUtc = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        var raw = isUtc ? value[..^1] : value;
        if (!DateTime.TryParseExact(raw, "yyyyMMddTHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return null;

        if (isUtc)
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc).ToLocalTime();

        if (prop.Params.TryGetValue("TZID", out var tzid) && !string.IsNullOrWhiteSpace(tzid))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(tzid);
                var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), tz);
                return utc.ToLocalTime();
            }
            catch (TimeZoneNotFoundException) { /* unknown TZID: best effort, treat as floating */ }
            catch (InvalidTimeZoneException) { }
        }

        return parsed; // floating
    }

    /// <summary>Reverses SessionsController.IcsEscape (same order, backwards).</summary>
    private static string Unescape(string value) =>
        value.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\;", ";").Replace("\\,", ",").Replace("\\\\", "\\");
}
