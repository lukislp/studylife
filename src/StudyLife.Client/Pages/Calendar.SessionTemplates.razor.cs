using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

/// <summary>
/// Session templates (GET/POST/DELETE /api/sessiontemplates) for quickly creating recurring
/// sessions without retyping course/duration/topic every time. A separate partial class file instead
/// of extending Calendar.SessionDialog.razor.cs, so this feature and the ICS import feature
/// that landed in parallel (Calendar.ImportIcs.razor.cs) don't create merge friction on the same
/// file - see the task description. Accesses the private form fields
/// (_formCourseId, _formTopic, _formStart, _formEnd, ...) from Calendar.SessionDialog.razor.cs,
/// which works without issue within the same partial class.
/// </summary>
public partial class Calendar
{
    private List<SessionTemplateDto> _templates = new();
    private bool _showTemplatesModal;
    private int? _confirmingDeleteTemplateId;

    /// <summary>0 = "no template" (default option in the dropdown), bound in the session modal.</summary>
    private int _selectedTemplateId;
    private bool _showSaveAsTemplateForm;
    private string? _newTemplateName;
    private string? _templateFormError;

    private async Task LoadTemplatesAsync()
    {
        try
        {
            _templates = await State.GetJsonCachedAsync<List<SessionTemplateDto>>("api/sessiontemplates") ?? new();
        }
        catch
        {
            // Offline/flaky network: templates are a pure convenience feature, the calendar
            // works fully without them - same degradation as with _goals.
            _templates = new();
        }
    }

    /// <summary>Resets the template-related UI state - called from OnDayClick/EditSession/CloseModal
    /// in Calendar.SessionDialog.razor.cs, so re-opening the session modal doesn't leave
    /// leftovers from the previous run (e.g. an open "save as template" form).</summary>
    private void ResetTemplateFormState()
    {
        _selectedTemplateId = 0;
        _showSaveAsTemplateForm = false;
        _newTemplateName = null;
        _templateFormError = null;
    }

    private void OpenTemplatesModal()
    {
        _showTemplatesModal = true;
        _confirmingDeleteTemplateId = null;
    }

    private void CloseTemplatesModal()
    {
        _showTemplatesModal = false;
        _confirmingDeleteTemplateId = null;
    }

    private async Task DeleteTemplateAsync(int id)
    {
        try { await Http.DeleteAsync($"api/sessiontemplates/{id}"); }
        catch { /* best effort - no offline queue for this low-stakes feature, user can delete again later */ }
        await LoadTemplatesAsync();
        _confirmingDeleteTemplateId = null;
    }

    /// <summary>
    /// Applies course/topic/duration of the selected template to the open session form. The
    /// DAY the user clicked in the calendar is preserved - only the time is optionally set to
    /// the template's DefaultStartTime (if present); DefaultWeekday is purely a
    /// display aid in the template list and isn't enforced here.
    /// </summary>
    private void ApplyTemplate()
    {
        var template = _templates.FirstOrDefault(t => t.Id == _selectedTemplateId);
        if (template == null) return;

        _formCourseId = template.CourseId;
        _formTopic = template.Topic;
        var newStart = _formStart.Date + (template.DefaultStartTime ?? _formStart.TimeOfDay);
        _formStart = newStart;
        _formEnd = newStart.AddMinutes(template.DurationMinutes);
        _warningAcknowledged = false;
        _formWarning = null;
    }

    private void ToggleSaveAsTemplateForm()
    {
        _showSaveAsTemplateForm = !_showSaveAsTemplateForm;
        _newTemplateName = _showSaveAsTemplateForm ? _courses.FirstOrDefault(c => c.Id == _formCourseId)?.Name : null;
        _templateFormError = null;
    }

    private async Task ConfirmSaveAsTemplateAsync()
    {
        if (string.IsNullOrWhiteSpace(_newTemplateName))
        {
            _templateFormError = T.TemplateNameRequiredError ?? "";
            return;
        }

        var course = _courses.FirstOrDefault(c => c.Id == _formCourseId);
        var dto = new SessionTemplateDto
        {
            Name = _newTemplateName.Trim(),
            CourseId = _formCourseId,
            CourseName = course?.Name ?? "",
            CourseColor = course?.Color ?? "#6C5CE7",
            DurationMinutes = Math.Max(1, (int)(_formEnd - _formStart).TotalMinutes),
            Topic = _formTopic,
            DefaultWeekday = (int)_formStart.DayOfWeek,
            DefaultStartTime = _formStart.TimeOfDay,
        };

        try
        {
            var response = await Http.PostAsJsonAsync("api/sessiontemplates", dto);
            if (!response.IsSuccessStatusCode)
            {
                _templateFormError = T.TemplateSaveFailed ?? "";
                return;
            }
            await LoadTemplatesAsync();
            _showSaveAsTemplateForm = false;
            _newTemplateName = null;
            _templateFormError = null;
        }
        catch
        {
            _templateFormError = T.TemplateSaveFailed ?? "";
        }
    }

    /// <summary>Display helper for the template list: short form of the weekday in the current
    /// browser culture, the same convention as CalendarRepeatOptions.WeekdayShortLabel.</summary>
    private static string WeekdayShortLabel(int dayOfWeek)
    {
        var sundayRef = new DateTime(2024, 1, 7); // a Sunday
        return sundayRef.AddDays(dayOfWeek).ToString("ddd");
    }
}
