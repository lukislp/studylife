// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using StudyLife.Client.Components.Dashboard;

namespace StudyLife.Client.Tests;

/// <summary>The quota tile counts the planned week/month; it names the planned share so its hours
/// add up with the studied-only "This week" card, and shows no such line when nothing is planned.</summary>
public class DashboardQuotaCardTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private static DashboardQuotaCard.QuotaCardData Data(string plannedNote) =>
        new("card-quota-week", "This week — study quota", "17h", "/ 10 – 14 h", false, 80, 60, "0 h", "Target: 10 – 14 h", null, plannedNote);

    [Fact]
    public void The_tile_names_its_planned_share()
    {
        var cut = _ctx.Render<DashboardQuotaCard>(p => p.Add(c => c.Data, Data("incl. 3h planned")));

        Assert.Equal("incl. 3h planned", cut.Find(".quota-planned-note").TextContent.Trim());
    }

    [Fact]
    public void The_tile_shows_no_planned_line_when_nothing_is_planned()
    {
        var cut = _ctx.Render<DashboardQuotaCard>(p => p.Add(c => c.Data, Data("")));

        Assert.Empty(cut.FindAll(".quota-planned-note"));
    }
}
