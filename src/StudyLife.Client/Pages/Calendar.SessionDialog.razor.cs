using Microsoft.AspNetCore.Components.Web;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

public partial class Calendar
{
    private bool _showModal;
    private StudySession? _editSession;
    private int _formCourseId;
    private string? _formTopic;
    private DateTime _formStart;
    private DateTime _formEnd;
    private string? _formError;
    private string? _formWarning;
    private bool _warningAcknowledged;
    private bool _confirmingDelete;

    private void OnDayClick(DateTime day, MouseEventArgs e)
    {
        if (day.Date < DateTime.Today) return;
        var clickedHour = (int)(e.OffsetY / 60);
        var start = new DateTime(day.Year, day.Month, day.Day, Math.Max(0, Math.Min(23, clickedHour)), 0, 0);
        if (start < DateTime.Now)
            start = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, DateTime.Now.Hour, 0, 0).AddHours(1);
        _editSession = new StudySession();
        _formCourseId = _courses.FirstOrDefault()?.Id ?? 1;
        _formTopic = null;
        SuggestTopic();
        _formError = null;
        _formWarning = null;
        _warningAcknowledged = false;
        _confirmingDelete = false;
        _formStart = start;
        _formEnd = start.AddHours(1);
        _formRepeatWeekly = false;
        _formRepeatUntil = start.AddDays(7).Date;
        _formRepeatIntervalWeeks = 1;
        _formRepeatWeekdays = new HashSet<DayOfWeek> { start.DayOfWeek };
        ResetTemplateFormState();
        _showModal = true;
    }

    private void EditSession(StudySession s)
    {
        _editSession = s;
        _formCourseId = s.CourseId;
        _formTopic = s.Topic;
        _formStart = s.StartTime;
        _formEnd = s.EndTime;
        _formError = null;
        _formWarning = null;
        _warningAcknowledged = false;
        _confirmingDelete = false;
        _formRepeatWeekly = false;
        ResetTemplateFormState();
        _showModal = true;
    }

    private void OnFormFieldChanged()
    {
        _warningAcknowledged = false;
        _formWarning = null;
        SuggestTopic();
    }

    private void SuggestTopic()
    {
        if (!(_editSession == null || _editSession.Id == 0) || !string.IsNullOrWhiteSpace(_formTopic)) return;
        var course = _courses.FirstOrDefault(c => c.Id == _formCourseId);
        if (course == null || course.Topics.Count == 0) return;
        var goal = _goals.FirstOrDefault(g => g.CourseId == _formCourseId);
        var completedTopics = goal == null || string.IsNullOrWhiteSpace(goal.CompletedTopics)
            ? new HashSet<string>()
            : goal.CompletedTopics.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        _formTopic = course.Topics.FirstOrDefault(t => !completedTopics.Contains(t));
    }

    private async Task SaveSession()
    {
        if (_formStart < DateTime.Now && (_editSession == null || _editSession.Id == 0))
        {
            _formError = T.PastSessionError;
            return;
        }
        _formError = null;

        if (!_warningAcknowledged)
        {
            var editId = _editSession?.Id ?? 0;
            var conflict = _sessions.FirstOrDefault(s =>
                s.Id != editId &&
                s.StartTime < _formEnd && s.EndTime > _formStart);
            if (conflict != null)
            {
                _formWarning = string.Format(T.SessionOverlapWarning,
                    $"{conflict.CourseName} ({conflict.StartTime:HH:mm}–{conflict.EndTime:HH:mm})");
                _warningAcknowledged = true;
                return;
            }
        }
        _formWarning = null;

        var isNew = _editSession == null || _editSession.Id == 0;
        var course = _courses.FirstOrDefault(c => c.Id == _formCourseId);
        var session = _editSession ?? new StudySession();
        session.CourseId = _formCourseId;
        session.CourseName = course?.Name ?? "";
        session.CourseColor = course?.Color ?? "#6C5CE7";
        session.StartTime = _formStart;
        session.EndTime = _formEnd;
        session.Topic = _formTopic;
        session.TimerModeId = new Random().Next(1, DefaultData.TimerModes.Count + 1);

        var isRepeating = isNew && _formRepeatWeekly && _formRepeatUntil.Date >= _formStart.Date;
        if (isRepeating) session.RecurrenceGroupId = Guid.NewGuid().ToString();
        await State.SaveSessionAsync(session);

        if (isRepeating)
        {
            var duration = _formEnd - _formStart;
            var weekdays = _formRepeatWeekdays.Count > 0 ? _formRepeatWeekdays : new HashSet<DayOfWeek> { _formStart.DayOfWeek };
            var intervalWeeks = Math.Max(1, _formRepeatIntervalWeeks);
            // Monday-anchored week blocks, so "every N weeks" counts consistently regardless of the start's weekday.
            var startWeekMonday = _formStart.Date.AddDays(-(((int)_formStart.DayOfWeek + 6) % 7));
            var occurrenceDate = _formStart.Date.AddDays(1);
            var count = 0;
            while (occurrenceDate <= _formRepeatUntil.Date && count < MaxRepeatOccurrences)
            {
                if (weekdays.Contains(occurrenceDate.DayOfWeek))
                {
                    var occurrenceWeekMonday = occurrenceDate.AddDays(-(((int)occurrenceDate.DayOfWeek + 6) % 7));
                    var weeksSinceStart = (int)(occurrenceWeekMonday - startWeekMonday).TotalDays / 7;
                    if (weeksSinceStart % intervalWeeks == 0)
                    {
                        var occurrenceStart = occurrenceDate + _formStart.TimeOfDay;
                        var repeated = new StudySession
                        {
                            CourseId = _formCourseId,
                            CourseName = course?.Name ?? "",
                            CourseColor = course?.Color ?? "#6C5CE7",
                            StartTime = occurrenceStart,
                            EndTime = occurrenceStart + duration,
                            Topic = _formTopic,
                            TimerModeId = new Random().Next(1, DefaultData.TimerModes.Count + 1),
                            RecurrenceGroupId = session.RecurrenceGroupId,
                        };
                        await State.SaveSessionAsync(repeated);
                        count++;
                    }
                }
                occurrenceDate = occurrenceDate.AddDays(1);
            }
        }

        _sessions = await State.GetSessionsAsync();
        CloseModal();
    }

    private async Task DeleteSession()
    {
        if (_editSession?.Id > 0)
        {
            await State.DeleteSessionAsync(_editSession.Id);
            _sessions = await State.GetSessionsAsync();
        }
        CloseModal();
    }

    private async Task DeleteSeries(DateTime? fromDate)
    {
        if (_editSession?.RecurrenceGroupId != null)
        {
            await State.DeleteSeriesAsync(_editSession.RecurrenceGroupId, fromDate);
            _sessions = await State.GetSessionsAsync();
        }
        CloseModal();
    }

    private void CloseModal()
    {
        _showModal = false;
        _editSession = null;
        _confirmingDelete = false;
        _formWarning = null;
        _warningAcknowledged = false;
        ResetTemplateFormState();
    }
}
