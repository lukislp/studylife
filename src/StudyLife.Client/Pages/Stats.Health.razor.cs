using StudyLife.Client.Components.Stats;
using StudyLife.Client.Services;

namespace StudyLife.Client.Pages;

public partial class Stats
{
    private List<StatsCardioFitnessTrendCard.CardioPoint> _cardioFitnessPoints = new();

    private void BuildCardioFitnessTrend(IReadOnlyList<CardioFitnessPoint>? history)
    {
        _cardioFitnessPoints = new();
        if (history == null || history.Count == 0) return;

        // Series-relative percent (own min/max), not an absolute physiological scale - a
        // healthy VO2max range varies widely by person/age/fitness level, so a fixed scale
        // would either clip everyone's real variation or leave the chart looking flat for most
        // users. A single reading (min == max) just fills the bar completely.
        //
        // LINQ is safe again here: `history` is now IReadOnlyList<CardioFitnessPoint> (a record
        // struct), not the old (DateTime Date, double Vo2Max) value tuple. That value-tuple
        // element type reproducibly crashed the app with a native SIGABRT at STARTUP on iOS when
        // LINQ (OrderBy/Min/Max/Select) was generic-instantiated over it - confirmed via manual
        // step-by-step bisection that the crash came from the method merely being AOT-compiled
        // into the binary, not from it ever being called. That was a Mono AOT "gsharedvt"
        // (generic-shared-code-for-value-types) code-generation bug for that specific
        // generic/tuple combination on iOS Full AOT, not a bug in the logic itself - it did not
        // reproduce for record structs, which is why the contract was migrated to one (see
        // INativeHealthData.CardioFitnessPoint). If a similar crash resurfaces, verify on a real
        // device (not just `dotnet build`) - the Blazor/browser build has no such restriction
        // and won't catch this.
        var ordered = history.OrderBy(p => p.Date).ToList();

        var min = ordered.Min(p => p.Vo2Max);
        var max = ordered.Max(p => p.Vo2Max);
        var range = max - min;

        _cardioFitnessPoints = ordered
            .Select(p =>
            {
                var percent = range > 0 ? (p.Vo2Max - min) / range * 100 : 100;
                return new StatsCardioFitnessTrendCard.CardioPoint(p.Date, p.Vo2Max, percent);
            })
            .ToList();
    }
}
