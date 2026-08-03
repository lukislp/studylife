using StudyLife.Client.Models;

namespace StudyLife.Client.Pages;

public partial class Calendar
{
    private string _searchQuery = "";
    private HashSet<int> _hiddenCourseIds = new();

    private void ToggleCourseFilter(int courseId)
    {
        if (!_hiddenCourseIds.Add(courseId))
            _hiddenCourseIds.Remove(courseId);
    }

    private List<StudySession> SessionsForDay(DateTime day) =>
        _sessions
            .Where(s => s.StartTime.Date == day.Date)
            .Where(s => !_hiddenCourseIds.Contains(s.CourseId))
            .Where(s => string.IsNullOrWhiteSpace(_searchQuery)
                || s.CourseName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
                || (s.Topic?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(s => s.StartTime)
            .ToList();
}
