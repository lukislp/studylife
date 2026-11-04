// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Shared;

namespace StudyLife.Server.Tests;

/// <summary>
/// The numeric bounds of PUT /api/settings (SettingsService.SaveAsync), checked at both edges
/// of every range: the study window (0-23, end after start), the weekly goal (1-100, max above
/// min) and the monthly goal (1-400, max above min). The existing tests only cover one
/// representative out-of-range value per field.
/// </summary>
public class SettingsBoundsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public SettingsBoundsTests(CustomWebApplicationFactory factory) => _client = factory.CreateClient();

    private static UserSettingsDto Valid() => new()
    {
        StudyWindowStartHour = 8,
        StudyWindowEndHour = 21,
        WeeklyGoalMinHours = 25,
        WeeklyGoalMaxHours = 30,
        MonthlyGoalMinHours = 100,
        MonthlyGoalMaxHours = 130,
    };

    public static TheoryData<string, int, int> OutOfRange => new()
    {
        { "window", -1, 21 },
        { "window", 24, 23 },
        { "window", 8, -1 },
        { "window", 8, 24 },
        { "window", 12, 12 },
        { "window", 13, 12 },
        { "weekly", 0, 30 },
        { "weekly", 101, 102 },
        { "weekly", 25, 0 },
        { "weekly", 25, 101 },
        { "weekly", 30, 30 },
        { "monthly", 0, 130 },
        { "monthly", 401, 402 },
        { "monthly", 100, 0 },
        { "monthly", 100, 401 },
        { "monthly", 130, 100 },
    };

    public static TheoryData<string, int, int> Edges => new()
    {
        { "window", 0, 23 },
        { "window", 22, 23 },
        { "weekly", 1, 100 },
        { "weekly", 99, 100 },
        { "monthly", 1, 400 },
        { "monthly", 399, 400 },
    };

    private static UserSettingsDto With(string field, int min, int max)
    {
        var dto = Valid();
        switch (field)
        {
            case "window": dto.StudyWindowStartHour = min; dto.StudyWindowEndHour = max; break;
            case "weekly": dto.WeeklyGoalMinHours = min; dto.WeeklyGoalMaxHours = max; break;
            case "monthly": dto.MonthlyGoalMinHours = min; dto.MonthlyGoalMaxHours = max; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
        return dto;
    }

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public async Task Put_OutOfRangeOrInvertedPair_IsRejected(string field, int min, int max)
    {
        var response = await _client.PutAsJsonAsync("/api/settings", With(field, min, max));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Edges))]
    public async Task Put_ValuesAtTheEdgesOfTheRange_AreAcceptedAndStored(string field, int min, int max)
    {
        var response = await _client.PutAsJsonAsync("/api/settings", With(field, min, max));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await _client.GetFromJsonAsync<UserSettingsDto>("/api/settings");
        Assert.NotNull(stored);
        var (storedMin, storedMax) = field switch
        {
            "window" => (stored!.StudyWindowStartHour, stored.StudyWindowEndHour),
            "weekly" => (stored!.WeeklyGoalMinHours, stored.WeeklyGoalMaxHours),
            _ => (stored!.MonthlyGoalMinHours, stored.MonthlyGoalMaxHours),
        };
        Assert.Equal((min, max), (storedMin, storedMax));
    }
}

/// <summary>
/// SettingsService.ToDto is also the read path for rows that never went through SaveAsync's
/// validation (backup imports, rows written by older versions): out-of-range or empty stored
/// values must come back as the documented defaults, never as values the client cannot handle.
/// </summary>
public class SettingsToDtoNormalisationTests
{
    [Fact]
    public void CorruptStoredValues_FallBackToTheDefaults()
    {
        var dto = SettingsService.ToDto(new UserSettingsEntity
        {
            SelectedCourseIds = "",
            AccentColor = "  ",
            SessionReminderMinutes = " ",
            CourseGoalReminderDays = "",
            InactivityThresholdDays = 0,
            StudyWindowStartHour = 24,
            StudyWindowEndHour = 0,
            StudyDays = "",
            WeeklyGoalMinHours = 0,
            WeeklyGoalMaxHours = 101,
            MonthlyGoalMinHours = -5,
            MonthlyGoalMaxHours = 401,
        });

        Assert.Equal(new List<int> { 1, 2, 3, 4 }, dto.SelectedCourseIds);
        Assert.Equal("coral", dto.AccentColor);
        Assert.Equal("60,30,10,5,3,2,1", dto.SessionReminderMinutes);
        Assert.Equal("14,7,3,1,0", dto.CourseGoalReminderDays);
        Assert.Equal(5, dto.InactivityThresholdDays);
        Assert.Equal(8, dto.StudyWindowStartHour);
        Assert.Equal(21, dto.StudyWindowEndHour);
        Assert.Equal("0,1,2,3,4,5,6", dto.StudyDays);
        Assert.Equal(25, dto.WeeklyGoalMinHours);
        Assert.Equal(30, dto.WeeklyGoalMaxHours);
        Assert.Equal(100, dto.MonthlyGoalMinHours);
        Assert.Equal(130, dto.MonthlyGoalMaxHours);
    }

    [Fact]
    public void ValidStoredValues_ArePassedThroughUnchanged()
    {
        var dto = SettingsService.ToDto(new UserSettingsEntity
        {
            SelectedCourseIds = "7,9",
            AccentColor = "teal",
            SessionReminderMinutes = "15",
            CourseGoalReminderDays = "2",
            InactivityThresholdDays = 9,
            StudyWindowStartHour = 0,
            StudyWindowEndHour = 23,
            StudyDays = "1,3",
            WeeklyGoalMinHours = 1,
            WeeklyGoalMaxHours = 100,
            MonthlyGoalMinHours = 1,
            MonthlyGoalMaxHours = 400,
        });

        Assert.Equal(new List<int> { 7, 9 }, dto.SelectedCourseIds);
        Assert.Equal("teal", dto.AccentColor);
        Assert.Equal("15", dto.SessionReminderMinutes);
        Assert.Equal("2", dto.CourseGoalReminderDays);
        Assert.Equal(9, dto.InactivityThresholdDays);
        Assert.Equal(0, dto.StudyWindowStartHour);
        Assert.Equal(23, dto.StudyWindowEndHour);
        Assert.Equal("1,3", dto.StudyDays);
        Assert.Equal(1, dto.WeeklyGoalMinHours);
        Assert.Equal(100, dto.WeeklyGoalMaxHours);
        Assert.Equal(1, dto.MonthlyGoalMinHours);
        Assert.Equal(400, dto.MonthlyGoalMaxHours);
    }
}
