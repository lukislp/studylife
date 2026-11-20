// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Client.Components.Focus;
using StudyLife.Client.Models;
using StudyLife.Client.Shared;

namespace StudyLife.Client.Tests;

/// <summary>The motivational quote belongs to the Focus page header; the "up next" block must not
/// repeat it (the store screenshots showed the same quote twice).</summary>
[Collection(LocalDateStateCollection.Name)]
public class FocusSessionStatusTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public FocusSessionStatusTests() => _ctx.Services.AddSingleton<TimeProvider>(new FixedLocalClock(new DateTime(2026, 11, 19, 9, 41, 0)));

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void The_up_next_block_shows_the_session_without_a_quote()
    {
        var session = new StudySession
        {
            CourseName = "Algorithms",
            StartTime = new DateTime(2026, 11, 19, 11, 0, 0),
            EndTime = new DateTime(2026, 11, 19, 12, 0, 0),
        };

        var cut = _ctx.Render<FocusSessionStatus>(p => p
            .Add(c => c.UpcomingSession, session)
            .Add(c => c.UpNextInFormat, "Up next in {0}"));

        Assert.Equal("Algorithms", cut.Find(".fu-name").TextContent);
        Assert.Empty(cut.FindAll("p"));
        Assert.DoesNotContain("motivation", cut.Markup);
    }
}
