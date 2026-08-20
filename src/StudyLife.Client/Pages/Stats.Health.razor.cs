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
        var ordered = history.OrderBy(h => h.Date).ToList();
        var min = ordered.Min(h => h.Vo2Max);
        var max = ordered.Max(h => h.Vo2Max);
        var range = max - min;

        _cardioFitnessPoints = ordered
            .Select(h => new StatsCardioFitnessTrendCard.CardioPoint(
                h.Date, h.Vo2Max,
                range > 0 ? (h.Vo2Max - min) / range * 100 : 100))
            .ToList();
    }
}
