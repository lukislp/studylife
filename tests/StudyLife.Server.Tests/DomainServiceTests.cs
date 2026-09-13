using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Resolves a domain service and the DbContext from ONE scope, so a test can seed/assert against
/// exactly the context the service under test is using - the service-level counterpart to
/// <see cref="CustomWebApplicationFactoryDbExtensions.WithDbAsync{T}"/>. Every test here calls
/// the services directly instead of going through HTTP: the controller-level suites
/// (SessionsControllerTests, NotesControllerTests, ...) already pin the wire behavior, these pin
/// the domain behavior without a request in the way.
/// </summary>
internal static class DomainServiceTestExtensions
{
    public static async Task<T> WithServiceAsync<TService, T>(
        this CustomWebApplicationFactory factory, Func<TService, StudyLifeDb, Task<T>> action)
        where TService : notnull
    {
        using var scope = factory.Services.CreateScope();
        return await action(
            scope.ServiceProvider.GetRequiredService<TService>(),
            scope.ServiceProvider.GetRequiredService<StudyLifeDb>());
    }

    public static async Task WithServiceAsync<TService>(
        this CustomWebApplicationFactory factory, Func<TService, StudyLifeDb, Task> action)
        where TService : notnull
    {
        using var scope = factory.Services.CreateScope();
        await action(
            scope.ServiceProvider.GetRequiredService<TService>(),
            scope.ServiceProvider.GetRequiredService<StudyLifeDb>());
    }

    /// <summary>First built-in catalog course - every write path validates CourseId against the
    /// caller's course universe (audit M2, see CourseResolver), so tests need a real id.</summary>
    public static CourseDto BuiltInCourse => CourseCatalog.AppliedAICourses[0];
}

public class SessionServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SessionServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static StudySessionDto ValidSession(DateTime start) => new()
    {
        CourseId = DomainServiceTestExtensions.BuiltInCourse.Id,
        // Deliberately junk: the service derives name/color from the resolved course instead.
        CourseName = "Client-Supplied-Junk-Name",
        CourseColor = "#000000",
        StartTime = start,
        EndTime = start.AddHours(1),
    };

    [Fact]
    public async Task CreateAsync_ValidSession_PersistsItAndDerivesTheCourseNameFromTheCatalog()
    {
        var result = await _factory.WithServiceAsync<ISessionService, ServiceResult<StudySessionDto>>(
            (sessions, _) => sessions.CreateAsync(ValidSession(DateTime.Now.AddDays(1))));

        Assert.Equal(ServiceOutcome.Success, result.Outcome);
        Assert.True(result.Value!.Id > 0);
        Assert.Equal(DomainServiceTestExtensions.BuiltInCourse.Name, result.Value.CourseName);

        var stored = await _factory.WithDbAsync(db => db.Sessions.AsNoTracking().FirstAsync(s => s.Id == result.Value!.Id));
        Assert.Equal(DomainServiceTestExtensions.BuiltInCourse.Name, stored.CourseName);
    }

    [Fact]
    public async Task CreateAsync_UnknownCourseId_IsInvalidWithTheStableMessage()
    {
        var dto = ValidSession(DateTime.Now.AddDays(1));
        dto.CourseId = 987654;

        var result = await _factory.WithServiceAsync<ISessionService, ServiceResult<StudySessionDto>>(
            (sessions, _) => sessions.CreateAsync(dto));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal(CourseValidationMessages.UnknownCourseId(987654), result.Error);
    }

    [Fact]
    public async Task CreateAsync_SessionLongerThanADay_IsInvalid()
    {
        var dto = ValidSession(DateTime.Now.AddDays(1));
        dto.EndTime = dto.StartTime.AddHours(25);

        var result = await _factory.WithServiceAsync<ISessionService, ServiceResult<StudySessionDto>>(
            (sessions, _) => sessions.CreateAsync(dto));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal("A session cannot last longer than 24 hours.", result.Error);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_IsNotFound()
    {
        var result = await _factory.WithServiceAsync<ISessionService, ServiceResult<StudySessionDto>>(
            (sessions, _) => sessions.UpdateAsync(999999, ValidSession(DateTime.Now.AddDays(1))));

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task UpdateAsync_ChangedTopic_IsPersisted()
    {
        var created = await _factory.WithServiceAsync<ISessionService, ServiceResult<StudySessionDto>>(
            (sessions, _) => sessions.CreateAsync(ValidSession(DateTime.Now.AddDays(2))));
        var dto = created.Value!;
        dto.Topic = "Kapitel 4";

        var updated = await _factory.WithServiceAsync<ISessionService, ServiceResult<StudySessionDto>>(
            (sessions, _) => sessions.UpdateAsync(dto.Id, dto));

        Assert.Equal(ServiceOutcome.Success, updated.Outcome);
        Assert.Equal("Kapitel 4", updated.Value!.Topic);
    }

    [Fact]
    public async Task DeleteAsync_ExistingThenAgain_IsSuccessThenNotFound()
    {
        var created = await _factory.WithServiceAsync<ISessionService, ServiceResult<StudySessionDto>>(
            (sessions, _) => sessions.CreateAsync(ValidSession(DateTime.Now.AddDays(3))));
        var id = created.Value!.Id;

        var first = await _factory.WithServiceAsync<ISessionService, ServiceResult>((sessions, _) => sessions.DeleteAsync(id));
        var second = await _factory.WithServiceAsync<ISessionService, ServiceResult>((sessions, _) => sessions.DeleteAsync(id));

        Assert.Equal(ServiceOutcome.Success, first.Outcome);
        Assert.Equal(ServiceOutcome.NotFound, second.Outcome);
    }

    [Fact]
    public void ClampHistoryDays_ClampsTheHostileInputsTheEndpointUsedToPassStraightThrough()
    {
        Assert.Equal(3650, SessionService.ClampHistoryDays(int.MinValue)); // Math.Abs would throw
        Assert.Equal(3650, SessionService.ClampHistoryDays(100_000));
        Assert.Equal(1, SessionService.ClampHistoryDays(0));
        Assert.Equal(7, SessionService.ClampHistoryDays(-7));
        Assert.Equal(365, SessionService.ClampHistoryDays(365));
    }

    [Fact]
    public async Task BuildIcsAsync_ContainsTheCreatedSessionAsAVevent()
    {
        var created = await _factory.WithServiceAsync<ISessionService, ServiceResult<StudySessionDto>>(
            (sessions, _) => sessions.CreateAsync(ValidSession(DateTime.Now.AddDays(4))));

        var ics = await _factory.WithServiceAsync<ISessionService, string>((sessions, _) => sessions.BuildIcsAsync());

        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.Contains($"UID:studylife-session-{created.Value!.Id}@studylife", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
    }
}

public class SessionTemplateServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SessionTemplateServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static SessionTemplateDto ValidTemplate(string name) => new()
    {
        Name = name,
        CourseId = DomainServiceTestExtensions.BuiltInCourse.Id,
        CourseName = "Client-Supplied-Junk-Name",
        CourseColor = "#000000",
        DurationMinutes = 90,
    };

    [Fact]
    public async Task CreateAsync_ValidTemplate_DerivesTheCourseNameAndShowsUpInGetAll()
    {
        var created = await _factory.WithServiceAsync<ISessionTemplateService, ServiceResult<SessionTemplateDto>>(
            (templates, _) => templates.CreateAsync(ValidTemplate("Vorlesung")));

        Assert.Equal(ServiceOutcome.Success, created.Outcome);
        Assert.Equal(DomainServiceTestExtensions.BuiltInCourse.Name, created.Value!.CourseName);

        var all = await _factory.WithServiceAsync<ISessionTemplateService, List<SessionTemplateDto>>(
            (templates, _) => templates.GetAllAsync());
        Assert.Contains(all, t => t.Id == created.Value.Id);
    }

    [Fact]
    public async Task CreateAsync_EmptyName_IsInvalid()
    {
        var result = await _factory.WithServiceAsync<ISessionTemplateService, ServiceResult<SessionTemplateDto>>(
            (templates, _) => templates.CreateAsync(ValidTemplate("  ")));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal("Name must not be empty.", result.Error);
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_IsNotFound()
    {
        var result = await _factory.WithServiceAsync<ISessionTemplateService, ServiceResult>(
            (templates, _) => templates.DeleteAsync(999999));

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
    }
}

public class NoteServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public NoteServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateAsync_WithoutACourse_IsAcceptedAndPersisted()
    {
        var result = await _factory.WithServiceAsync<INoteService, ServiceResult<NoteDto>>(
            (notes, _) => notes.CreateAsync(new NoteDto { Title = "Freie Notiz", Content = "Inhalt" }));

        Assert.Equal(ServiceOutcome.Success, result.Outcome);
        Assert.True(result.Value!.Id > 0);
        Assert.Null(result.Value.CourseId);
    }

    [Fact]
    public async Task CreateAsync_UnknownSessionId_IsInvalidWithTheStableMessage()
    {
        var result = await _factory.WithServiceAsync<INoteService, ServiceResult<NoteDto>>(
            (notes, _) => notes.CreateAsync(new NoteDto { Title = "X", Content = "", SessionId = 424242 }));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal(SessionValidationMessages.UnknownSessionId(424242), result.Error);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_IsNotFound()
    {
        var result = await _factory.WithServiceAsync<INoteService, ServiceResult<NoteDto>>(
            (notes, _) => notes.UpdateAsync(999999, new NoteDto { Title = "X", Content = "" }));

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task UpdateThenDeleteAsync_RoundTrips()
    {
        var created = await _factory.WithServiceAsync<INoteService, ServiceResult<NoteDto>>(
            (notes, _) => notes.CreateAsync(new NoteDto { Title = "Alt", Content = "A" }));
        var dto = created.Value!;
        dto.Title = "Neu";

        var updated = await _factory.WithServiceAsync<INoteService, ServiceResult<NoteDto>>(
            (notes, _) => notes.UpdateAsync(dto.Id, dto));
        Assert.Equal("Neu", updated.Value!.Title);

        var deleted = await _factory.WithServiceAsync<INoteService, ServiceResult>((notes, _) => notes.DeleteAsync(dto.Id));
        Assert.Equal(ServiceOutcome.Success, deleted.Outcome);
        Assert.Equal(ServiceOutcome.NotFound,
            (await _factory.WithServiceAsync<INoteService, ServiceResult>((notes, _) => notes.DeleteAsync(dto.Id))).Outcome);
    }

    [Fact]
    public async Task SearchAsync_BlankQuery_ReturnsNothingWithoutTouchingTheIndex()
    {
        var result = await _factory.WithServiceAsync<INoteService, List<NoteDto>>((notes, _) => notes.SearchAsync("   "));

        Assert.Empty(result);
    }
}

public class CourseGoalServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CourseGoalServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task SaveAsync_FirstCallCreates_SecondCallUpdatesTheSameRow()
    {
        var courseId = DomainServiceTestExtensions.BuiltInCourse.Id;

        var created = await _factory.WithServiceAsync<ICourseGoalService, ServiceResult<CourseGoalDto>>(
            (goals, _) => goals.SaveAsync(courseId, new CourseGoalDto { CourseName = "ignored", Grade = 2.0m }));
        Assert.Equal(ServiceOutcome.Success, created.Outcome);
        // The name is derived from the resolved course, not taken from the client (audit M2).
        Assert.Equal(DomainServiceTestExtensions.BuiltInCourse.Name, created.Value!.CourseName);

        var updated = await _factory.WithServiceAsync<ICourseGoalService, ServiceResult<CourseGoalDto>>(
            (goals, _) => goals.SaveAsync(courseId, new CourseGoalDto { CourseName = "ignored", Grade = 1.3m }));
        Assert.Equal(1.3m, updated.Value!.Grade);

        var rows = await _factory.WithDbAsync(db => db.CourseGoals.AsNoTracking().CountAsync(g => g.CourseId == courseId));
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task SaveAsync_GradeOutOfRange_IsInvalid()
    {
        var result = await _factory.WithServiceAsync<ICourseGoalService, ServiceResult<CourseGoalDto>>(
            (goals, _) => goals.SaveAsync(DomainServiceTestExtensions.BuiltInCourse.Id,
                new CourseGoalDto { CourseName = "X", Grade = 6.0m }));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal("Grade must be between 1.0 and 5.0.", result.Error);
    }

    [Fact]
    public async Task SaveAsync_UnknownCourseIdOnFirstCreation_IsInvalid()
    {
        var result = await _factory.WithServiceAsync<ICourseGoalService, ServiceResult<CourseGoalDto>>(
            (goals, _) => goals.SaveAsync(987654, new CourseGoalDto { CourseName = "X" }));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal(CourseValidationMessages.UnknownCourseId(987654), result.Error);
    }

    [Fact]
    public async Task DeleteAsync_WithoutAGoal_IsNotFound()
    {
        var result = await _factory.WithServiceAsync<ICourseGoalService, ServiceResult>(
            (goals, _) => goals.DeleteAsync(CourseCatalog.AppliedAICourses[^1].Id));

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
    }
}

public class CourseResourceServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CourseResourceServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CourseResourceDto ValidResource(string title) => new()
    {
        CourseId = DomainServiceTestExtensions.BuiltInCourse.Id,
        Title = title,
        Url = "https://example.com/script.pdf",
    };

    [Fact]
    public async Task CreateAsync_ValidResource_IsReturnedByGetByCourse()
    {
        var created = await _factory.WithServiceAsync<ICourseResourceService, ServiceResult<CourseResourceDto>>(
            (resources, _) => resources.CreateAsync(ValidResource("Skript")));

        Assert.Equal(ServiceOutcome.Success, created.Outcome);
        var byCourse = await _factory.WithServiceAsync<ICourseResourceService, List<CourseResourceDto>>(
            (resources, _) => resources.GetByCourseAsync(DomainServiceTestExtensions.BuiltInCourse.Id));
        Assert.Contains(byCourse, r => r.Id == created.Value!.Id);
    }

    [Fact]
    public async Task CreateAsync_NonHttpUrl_IsInvalid()
    {
        var dto = ValidResource("Skript");
        dto.Url = "javascript:alert(1)";

        var result = await _factory.WithServiceAsync<ICourseResourceService, ServiceResult<CourseResourceDto>>(
            (resources, _) => resources.CreateAsync(dto));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal("Url must be a valid http(s) address.", result.Error);
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_IsNotFound()
    {
        var result = await _factory.WithServiceAsync<ICourseResourceService, ServiceResult>(
            (resources, _) => resources.DeleteAsync(999999));

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
    }
}

public class StudyProgramServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public StudyProgramServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CreateStudyProgramRequestDto ValidProgram(string name) => new()
    {
        Name = name,
        Groups = new List<CreateStudyProgramGroupDto> { new() { Name = "Wahlpflicht", EctsQuota = 10 } },
        Courses = new List<CreateStudyProgramCourseDto>
        {
            new() { Name = "Kurs A", Semester = 1, Ects = 5, Group = "Wahlpflicht", Topics = new List<string> { "T1", "T2" } },
        },
    };

    [Fact]
    public async Task CreateAsync_ValidProgram_CreatesProgramGroupAndCourse()
    {
        var created = await _factory.WithServiceAsync<IStudyProgramService, ServiceResult<StudyProgramSummaryDto>>(
            (programs, _) => programs.CreateAsync(ValidProgram("Mein Studiengang")));

        Assert.Equal(ServiceOutcome.Success, created.Outcome);
        var id = created.Value!.Id!.Value;
        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.CourseGroups.AsNoTracking().CountAsync(g => g.StudyProgramId == id));
            Assert.Equal(1, await db.CustomCourses.AsNoTracking().CountAsync(c => c.StudyProgramId == id));
        });

        var summaries = await _factory.WithServiceAsync<IStudyProgramService, List<StudyProgramSummaryDto>>(
            (programs, _) => programs.GetSummariesAsync());
        Assert.Contains(summaries, s => s.Id == id && !s.IsBuiltIn);
        // The synthetic built-in entry is part of the same list as long as it isn't dismissed.
        Assert.Contains(summaries, s => s.Id == null && s.IsBuiltIn);
    }

    [Fact]
    public async Task CreateAsync_CourseInAnUndefinedGroup_IsInvalid()
    {
        var request = ValidProgram("Kaputt");
        request.Courses[0].Group = "Gibt es nicht";

        var result = await _factory.WithServiceAsync<IStudyProgramService, ServiceResult<StudyProgramSummaryDto>>(
            (programs, _) => programs.CreateAsync(request));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Contains("is not defined", result.Error!);
    }

    [Fact]
    public async Task GetAsync_And_SetCompletedAsync_UnknownId_AreNotFound()
    {
        var detail = await _factory.WithServiceAsync<IStudyProgramService, ServiceResult<StudyProgramDetailDto>>(
            (programs, _) => programs.GetAsync(999999));
        var completed = await _factory.WithServiceAsync<IStudyProgramService, ServiceResult<StudyProgramSummaryDto>>(
            (programs, _) => programs.SetCompletedAsync(999999, new SetStudyProgramCompletedDto { IsCompleted = true }));

        Assert.Equal(ServiceOutcome.NotFound, detail.Outcome);
        Assert.Equal(ServiceOutcome.NotFound, completed.Outcome);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheProgramWithItsGroupsAndCourses()
    {
        var created = await _factory.WithServiceAsync<IStudyProgramService, ServiceResult<StudyProgramSummaryDto>>(
            (programs, _) => programs.CreateAsync(ValidProgram("Zu löschen")));
        var id = created.Value!.Id!.Value;

        var deleted = await _factory.WithServiceAsync<IStudyProgramService, ServiceResult>(
            (programs, _) => programs.DeleteAsync(id));

        Assert.Equal(ServiceOutcome.Success, deleted.Outcome);
        await _factory.WithDbAsync(async db =>
        {
            Assert.False(await db.StudyPrograms.AsNoTracking().AnyAsync(p => p.Id == id));
            Assert.Equal(0, await db.CourseGroups.AsNoTracking().CountAsync(g => g.StudyProgramId == id));
            Assert.Equal(0, await db.CustomCourses.AsNoTracking().CountAsync(c => c.StudyProgramId == id));
        });
    }
}

public class ExamPlanServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ExamPlanServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GenerateAsync_BuiltInCourse_CreatesSessionsThatEndBeforeTheExam()
    {
        var examDate = DateTime.Today.AddDays(10);

        var result = await _factory.WithServiceAsync<IExamPlanService, ServiceResult<List<StudySessionDto>>>(
            (planner, _) => planner.GenerateAsync(new ExamPlanRequestDto
            {
                CourseId = DomainServiceTestExtensions.BuiltInCourse.Id,
                ExamDate = examDate,
                TotalHours = 3,
                SessionLengthMinutes = 90,
            }));

        Assert.Equal(ServiceOutcome.Success, result.Outcome);
        Assert.NotEmpty(result.Value!);
        Assert.All(result.Value!, s =>
        {
            Assert.True(s.Id > 0); // actually saved, not merely proposed
            Assert.True(s.EndTime <= examDate);
            Assert.Equal(90, (s.EndTime - s.StartTime).TotalMinutes);
        });
    }

    [Fact]
    public async Task GenerateAsync_PastExamDate_IsInvalid()
    {
        var result = await _factory.WithServiceAsync<IExamPlanService, ServiceResult<List<StudySessionDto>>>(
            (planner, _) => planner.GenerateAsync(new ExamPlanRequestDto
            {
                CourseId = DomainServiceTestExtensions.BuiltInCourse.Id,
                ExamDate = DateTime.Today,
            }));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Equal("ExamDate must be in the future.", result.Error);
    }

    [Fact]
    public async Task GenerateAsync_ExamTooFarAhead_IsInvalid()
    {
        var result = await _factory.WithServiceAsync<IExamPlanService, ServiceResult<List<StudySessionDto>>>(
            (planner, _) => planner.GenerateAsync(new ExamPlanRequestDto
            {
                CourseId = DomainServiceTestExtensions.BuiltInCourse.Id,
                ExamDate = DateTime.Today.AddYears(5),
                TotalHours = 10,
            }));

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Contains("within the next", result.Error!);
    }
}

public class TimerStateServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TimerStateServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task SaveAsync_ThenGetAsync_RoundTripsTheStateAndStampsServerNow()
    {
        await _factory.WithServiceAsync<ITimerStateService>((timer, _) => timer.SaveAsync(new TimerStateDto
        {
            IsRunning = true,
            IsBreak = false,
            CurrentRound = 2,
            TimerModeId = 1,
            ClientSequence = 100,
        }));

        var current = await _factory.WithServiceAsync<ITimerStateService, TimerStateDto>((timer, _) => timer.GetAsync());

        Assert.True(current.IsRunning);
        Assert.Equal(2, current.CurrentRound);
        Assert.Equal(100, current.ClientSequence);
        Assert.NotNull(current.ServerNow);
    }

    [Fact]
    public async Task SaveAsync_OlderClientSequence_IsDroppedAndTheCurrentRowIsReturned()
    {
        await _factory.WithServiceAsync<ITimerStateService>((timer, _) => timer.SaveAsync(new TimerStateDto
        {
            IsRunning = true,
            CurrentRound = 5,
            TimerModeId = 1,
            ClientSequence = 200,
        }));

        var stale = await _factory.WithServiceAsync<ITimerStateService, TimerStateDto>((timer, _) => timer.SaveAsync(new TimerStateDto
        {
            IsRunning = false,
            CurrentRound = 1,
            TimerModeId = 1,
            ClientSequence = 199,
        }));

        // Dropped silently (no exception, no 409) and answered with the row as it stands.
        Assert.True(stale.IsRunning);
        Assert.Equal(5, stale.CurrentRound);
        Assert.Equal(200, stale.ClientSequence);
    }

    [Fact]
    public async Task SetLiveActivityPushTokenAsync_LeavesTheRestOfTheRowAlone()
    {
        await _factory.WithServiceAsync<ITimerStateService>((timer, _) => timer.SaveAsync(new TimerStateDto
        {
            IsRunning = true,
            CurrentRound = 3,
            TimerModeId = 1,
        }));

        await _factory.WithServiceAsync<ITimerStateService>(
            (timer, _) => timer.SetLiveActivityPushTokenAsync(new LiveActivityPushTokenDto { Token = "tok-abc" }));

        var stored = await _factory.WithDbAsync(db => db.TimerState.AsNoTracking().FirstAsync());
        Assert.Equal("tok-abc", stored.LiveActivityPushToken);
        Assert.True(stored.IsRunning);
        Assert.Equal(3, stored.CurrentRound);
    }
}
