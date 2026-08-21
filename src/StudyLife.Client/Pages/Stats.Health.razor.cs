using StudyLife.Client.Components.Stats;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private List<StatsCardioFitnessTrendCard.CardioPoint> _cardioFitnessPoints = new();

    private void BuildCardioFitnessTrend(IReadOnlyList<(DateTime Date, double Vo2Max)>? history)
    {
        _cardioFitnessPoints = new();
        if (history == null || history.Count == 0) return;

        // Series-relative percent (own min/max), not an absolute physiological scale - a
        // healthy VO2max range varies widely by person/age/fitness level, so a fixed scale
        // would either clip everyone's real variation or leave the chart looking flat for most
        // users. A single reading (min == max) just fills the bar completely.
        //
        // Plain loops instead of LINQ (OrderBy/Min/Max/Select) here deliberately: LINQ
        // instantiated over this value-tuple element type ((DateTime Date, double Vo2Max))
        // reproducibly crashed the app with a native SIGABRT at STARTUP on iOS - confirmed via
        // manual step-by-step bisection that the crash came from this method merely being
        // AOT-compiled into the binary, not from it ever being called (it crashed the same way
        // whether or not any code path actually invoked it, and regardless of what data flowed
        // through it). Consistent with a Mono AOT "gsharedvt" (generic-shared-code-for-value-
        // types) code-generation bug for this specific generic/tuple combination on iOS Full
        // AOT, not a bug in the logic itself. If reintroducing LINQ here, verify on a real
        // device (not just `dotnet build`) - the Blazor/browser build has no such restriction
        // and won't catch this.
        var ordered = new List<(DateTime Date, double Vo2Max)>(history);
        ordered.Sort((a, b) => a.Date.CompareTo(b.Date));

        var min = ordered[0].Vo2Max;
        var max = ordered[0].Vo2Max;
        foreach (var entry in ordered)
        {
            if (entry.Vo2Max < min) min = entry.Vo2Max;
            if (entry.Vo2Max > max) max = entry.Vo2Max;
        }
        var range = max - min;

        var points = new List<StatsCardioFitnessTrendCard.CardioPoint>(ordered.Count);
        foreach (var entry in ordered)
        {
            var percent = range > 0 ? (entry.Vo2Max - min) / range * 100 : 100;
            points.Add(new StatsCardioFitnessTrendCard.CardioPoint(entry.Date, entry.Vo2Max, percent));
        }
        _cardioFitnessPoints = points;
    }
}
