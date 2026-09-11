// Localized weekday/month names via the browser's own Intl data - its own JS module instead of
// a global in index.html, because index.html is intentionally left untouched (same reasoning as
// accent.js/cachepurge.js). Dynamic import() from LocalDateNames (IJSObjectReference).
//
// The client is built with InvariantGlobalization=true (no ICU payload, see
// StudyLife.Client.csproj), so .NET itself only knows English names. Intl.DateTimeFormat is
// already present in every browser and covers all 26 UI languages for free, which is why the
// names are fetched from here once per language instead of being shipped as .NET culture data
// or hand-written tables.
export function dateNames(lang) {
    // Weekdays are indexed by .NET's DayOfWeek (Sunday = 0), so anchor on a known Sunday.
    // UTC everywhere: the reference dates carry no meaning beyond their weekday/month, and a
    // local-time render could shift them across a day boundary.
    const sunday = Date.UTC(2021, 0, 3);
    const weekdays = (style) => {
        const fmt = new Intl.DateTimeFormat(lang, { weekday: style, timeZone: 'UTC' });
        return Array.from({ length: 7 }, (_, i) => fmt.format(new Date(sunday + i * 86400000)));
    };
    const months = (style) => {
        const fmt = new Intl.DateTimeFormat(lang, { month: style, timeZone: 'UTC' });
        return Array.from({ length: 12 }, (_, i) => fmt.format(new Date(Date.UTC(2021, i, 15))));
    };
    return {
        weekdaysShort: weekdays('short'),
        weekdaysLong: weekdays('long'),
        monthsShort: months('short'),
        monthsLong: months('long'),
    };
}
