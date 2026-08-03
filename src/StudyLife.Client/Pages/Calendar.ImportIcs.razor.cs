using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Forms;
using StudyLife.Client.Models;
using StudyLife.Shared;

namespace StudyLife.Client.Pages;

/// <summary>
/// ICS import (POST /api/sessions/import-ics + review-then-create via the normal
/// POST /api/sessions, once per confirmed event - see SessionsController.ImportIcs).
/// Its own partial-class file instead of extending Calendar.razor.cs/SessionDialog, so the
/// concurrently running "session templates" task on the same files doesn't get merge friction.
/// </summary>
public partial class Calendar
{
    // Native ICS entry point (share menu of the app shell) - a no-op registration in the browser.
    [Microsoft.AspNetCore.Components.Inject]
    private StudyLife.Client.Services.INativeIcsIntake NativeIcs { get; set; } = default!;

    // 5 MB is far more than enough for .ics (plain text) even for very large semester schedules.
    private const long MaxIcsUploadBytes = 5L * 1024 * 1024;

    private bool _showImportModal;
    private string _importStep = "upload"; // "upload" | "review" | "done"
    private IBrowserFile? _importFile;
    private bool _importBusy;
    private string? _importError;
    private List<ImportCandidate> _importCandidates = new();
    private string? _importSummary;

    private sealed class ImportCandidate
    {
        public IcsImportEventDto Event { get; set; } = new();
        public bool Selected { get; set; } = true;
        public int CourseId { get; set; }
    }

    private void OpenImportModal()
    {
        _showImportModal = true;
        _importStep = "upload";
        _importFile = null;
        _importBusy = false;
        _importError = null;
        _importCandidates = new();
        _importSummary = null;
    }

    private void CloseImportModal() => _showImportModal = false;

    private void OnImportFileSelected(InputFileChangeEventArgs e)
    {
        _importFile = e.File;
        _importError = null;
    }

    private Task ParseImportFile()
    {
        if (_importFile == null) return Task.CompletedTask;
        return ParseImportContentAsync(
            new StreamContent(_importFile.OpenReadStream(MaxIcsUploadBytes)), _importFile.Name);
    }

    /// <summary>.ics shared from outside (native app shell, INativeIcsIntake): pick it up
    /// during page init and feed it into the same review flow as the file upload.</summary>
    private async Task ConsumeNativeIcsAsync()
    {
        var pending = NativeIcs.TakePending();
        if (pending is not var (fileName, bytes) || bytes.Length == 0) return;

        _showImportModal = true;
        _importStep = "upload";
        _importCandidates = new();
        _importSummary = null;
        StateHasChanged();
        await ParseImportContentAsync(new ByteArrayContent(bytes), fileName);
        StateHasChanged();
    }

    // Shared core of file upload and native share entry point.
    private async Task ParseImportContentAsync(HttpContent fileContent, string fileName)
    {
        _importBusy = true;
        _importError = null;
        try
        {
            using var content = new MultipartFormDataContent();
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/calendar");
            content.Add(fileContent, "file", fileName);

            var response = await Http.PostAsync("api/sessions/import-ics", content);
            if (!response.IsSuccessStatusCode)
            {
                _importError = T.ImportUploadFailed ?? "";
                return;
            }
            var result = await response.Content.ReadFromJsonAsync<IcsImportResultDto>();
            var events = result?.Events ?? new List<IcsImportEventDto>();
            if (events.Count == 0)
            {
                _importError = T.ImportNoEventsFound ?? "";
                return;
            }

            var defaultCourseId = _courses.FirstOrDefault()?.Id ?? 1;
            _importCandidates = events
                .OrderBy(ev => ev.StartTime)
                .Select(ev => new ImportCandidate { Event = ev, CourseId = defaultCourseId })
                .ToList();
            _importStep = "review";
        }
        catch
        {
            // Offline/network error on the parse request - unlike Save/Delete there's no
            // meaningful offline-queue fallback here (the user needs to see the review list
            // before anything happens), so just show the error message.
            _importError = T.ImportUploadFailed ?? "";
        }
        finally
        {
            _importBusy = false;
        }
    }

    private async Task ConfirmImport()
    {
        _importBusy = true;
        try
        {
            var imported = 0;
            foreach (var candidate in _importCandidates.Where(c => c.Selected))
            {
                var course = _courses.FirstOrDefault(c => c.Id == candidate.CourseId);
                var session = new StudySession
                {
                    CourseId = candidate.CourseId,
                    CourseName = course?.Name ?? "",
                    CourseColor = course?.Color ?? "#6C5CE7",
                    StartTime = candidate.Event.StartTime,
                    EndTime = candidate.Event.EndTime,
                    Topic = string.IsNullOrWhiteSpace(candidate.Event.Title) ? null : candidate.Event.Title,
                    Notes = candidate.Event.Description,
                    TimerModeId = new Random().Next(1, DefaultData.TimerModes.Count + 1),
                };
                await State.SaveSessionAsync(session);
                imported++;
            }
            _sessions = await State.GetSessionsAsync();
            _importSummary = string.Format(T.ImportSummary ?? "", imported);
            _importStep = "done";
        }
        finally
        {
            _importBusy = false;
        }
    }
}
