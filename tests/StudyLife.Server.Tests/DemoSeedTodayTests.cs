// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StudyLife.Server.Configuration;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Server.Services.SampleData;

namespace StudyLife.Server.Tests;

/// <summary>
/// The demo's "today" (SampleDataset): a session done this morning once that lies in the past, and
/// one planned for later today while the day has room for it - at every time the demo can be
/// seeded for. Plus the two demo settings that choose that time and the language (DemoOptions,
/// DemoSeeder.SeedTime). Every fact reseeds this class's own database (a full wipe), so the facts of
/// this class run one after the other (xUnit never runs facts of one class in parallel).
/// </summary>
public class DemoSeedTodayTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public DemoSeedTodayTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.CreateClient(); // boots the host: migrations run against the temp DB
    }

    private async Task<List<StudySessionEntity>> SeedTodayAsync(DateTime now,
        SampleDataLanguage language = SampleDataLanguage.German)
    {
        await _factory.WithDbAsync(db => DemoSeeder.ReseedAsync(db, now, language));
        return await _factory.WithDbAsync(db => db.Sessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.StartTime >= now.Date && s.StartTime < now.Date.AddDays(1))
            .OrderBy(s => s.StartTime)
            .ToListAsync());
    }

    private static DateTime Today(int hour, int minute) => DateTime.Today.AddHours(hour).AddMinutes(minute);

    [Fact]
    public async Task At_0941_today_holds_a_finished_morning_session_and_one_planned_for_the_afternoon()
    {
        var now = Today(9, 41);

        var today = await SeedTodayAsync(now);

        Assert.Equal(2, today.Count);
        var done = today[0];
        Assert.True(done.IsCompleted);
        Assert.Equal(Today(7, 30), done.StartTime);
        Assert.Equal(Today(8, 30), done.EndTime);
        var planned = today[1];
        Assert.False(planned.IsCompleted);
        Assert.Equal(Today(14, 0), planned.StartTime);
        Assert.Equal(Today(15, 30), planned.EndTime);
    }

    [Fact]
    public async Task Before_the_morning_session_is_over_nothing_completed_lies_in_the_future()
    {
        var now = Today(6, 15);

        var today = await SeedTodayAsync(now);

        var planned = Assert.Single(today);
        Assert.False(planned.IsCompleted);
        Assert.Equal(Today(14, 0), planned.StartTime);
    }

    [Fact]
    public async Task In_the_afternoon_the_planned_session_moves_two_full_hours_ahead()
    {
        var now = Today(15, 20);

        var today = await SeedTodayAsync(now);

        Assert.Equal(2, today.Count);
        Assert.True(today[0].IsCompleted);
        Assert.Equal(Today(17, 0), today[1].StartTime);
        Assert.All(today.Where(s => s.IsCompleted), s => Assert.True(s.EndTime <= now));
    }

    [Fact]
    public async Task Late_in_the_evening_no_session_is_planned_for_today_any_more()
    {
        var now = Today(19, 5);

        var today = await SeedTodayAsync(now);

        var done = Assert.Single(today);
        Assert.True(done.IsCompleted);
    }

    [Fact]
    public async Task The_english_demo_has_the_same_day_with_english_courses()
    {
        var today = await SeedTodayAsync(Today(9, 41), SampleDataLanguage.English);

        Assert.Equal(2, today.Count);
        var courseNames = EnglishSampleProgram.Program.Courses.Select(c => c.Name).ToHashSet();
        Assert.All(today, s => Assert.Contains(s.CourseName, courseNames));
    }

    [Theory]
    [InlineData(null, 13, 7)]
    [InlineData("", 13, 7)]
    [InlineData("09:41", 9, 41)]
    [InlineData(" 23:59 ", 23, 59)]
    [InlineData("00:00", 0, 0)]
    public void SeedTime_is_now_or_today_at_the_configured_clock(string? clock, int hour, int minute)
    {
        var now = new DateTime(2026, 11, 19, 13, 7, 30);

        var seedTime = DemoSeeder.SeedTime(now, clock);

        Assert.Equal(clock is null || clock.Trim().Length == 0 ? now : new DateTime(2026, 11, 19, hour, minute, 0), seedTime);
    }

    [Theory]
    [InlineData("9:41")]
    [InlineData("24:00")]
    [InlineData("09:60")]
    [InlineData("09:41:00")]
    [InlineData("morning")]
    public void SeedTime_refuses_anything_but_a_24_hour_time(string clock) =>
        Assert.Throws<InvalidOperationException>(() => DemoSeeder.SeedTime(DateTime.Now, clock));

    [Fact]
    public void The_demo_section_binds_language_and_clock_from_their_environment_names()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Language"] = "en", ["Demo:Clock"] = "09:41" })
            .Build();

        var demo = config.Bind<DemoOptions>(DemoOptions.SectionName);

        Assert.Equal("en", demo.Language);
        Assert.Equal("09:41", demo.Clock);
        Assert.Equal(SampleDataLanguage.English, SampleDataContent.ParseLanguage(demo.Language, "Demo:Language"));
    }

    [Fact]
    public void An_unknown_demo_language_names_the_demo_setting()
    {
        var error = Assert.Throws<InvalidOperationException>(() => SampleDataContent.ParseLanguage("fr", "Demo:Language"));

        Assert.StartsWith("Demo:Language", error.Message);
    }
}
