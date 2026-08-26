using System.Net.Http.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// Shared helper for tests that need a real CUSTOM course to exercise the "valid create with a
/// CUSTOM course" side of audit finding M2's CourseId validation (see CourseResolver) - a bare
/// int like 100042 is NOT enough on its own since it must actually resolve, i.e. a matching
/// CustomCourseEntity row must exist for the calling (test) user. Deliberately does NOT
/// activate the created study program (UserSettings.ActiveStudyProgramId stays untouched): the
/// validation set must include a user's custom courses across EVERY study program they own,
/// not just the currently active one (see CourseResolver's doc comment) - creating the course
/// without activating its program is itself a regression test for that.
/// </summary>
internal static class CustomCourseTestHelper
{
    /// <summary>Creates a new custom study program with exactly one course and returns its
    /// externally-shifted CourseId (CourseDto.Id, offset already applied) plus the course's
    /// name, so callers can assert against server-derived CourseName without hardcoding it.</summary>
    public static async Task<(int CourseId, string CourseName)> CreateAsync(HttpClient client, string? courseName = null)
    {
        var name = courseName ?? $"Custom Test Course {Guid.NewGuid():N}";
        var response = await client.PostAsJsonAsync("/api/studyprograms", new CreateStudyProgramRequestDto
        {
            Name = $"Test Program {Guid.NewGuid():N}",
            Courses = new List<CreateStudyProgramCourseDto>
            {
                new() { Name = name, Code = "CT-1", Topics = new List<string>() },
            },
        });
        response.EnsureSuccessStatusCode();
        var program = await response.Content.ReadFromJsonAsync<StudyProgramSummaryDto>();

        var courses = await client.GetFromJsonAsync<List<CourseDto>>($"/api/courses?program={program!.Id}");
        var course = Assert.Single(courses!);
        return (course.Id, course.Name);
    }
}
