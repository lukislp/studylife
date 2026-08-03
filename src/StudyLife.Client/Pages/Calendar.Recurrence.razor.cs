namespace StudyLife.Client.Pages;

public partial class Calendar
{
    private bool _formRepeatWeekly;
    private DateTime _formRepeatUntil = DateTime.Today.AddDays(7);
    private int _formRepeatIntervalWeeks = 1;
    private HashSet<DayOfWeek> _formRepeatWeekdays = new();
    private const int MaxRepeatOccurrences = 52;

    private void ToggleRepeatWeekday(DayOfWeek day)
    {
        if (!_formRepeatWeekdays.Remove(day)) _formRepeatWeekdays.Add(day);
    }
}
