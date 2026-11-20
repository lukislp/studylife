// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using Microsoft.AspNetCore.Components;
using StudyLife.Client.Components.Stats;
using StudyLife.Client.Services;
using StudyLife.Client.Shared;

namespace StudyLife.Client.Tests;

/// <summary>
/// The runtime path behind the store screenshot of the English UI showing "1,66" and "19.05.26":
/// LocalDate.Language is a static defaulting to German and the layout learns the real language only
/// after its first awaits, while the page body was already rendered (and, with unchanged parameters,
/// never re-rendered). The scope must hold the body back until the language is known.
/// </summary>
[Collection(LocalDateStateCollection.Name)]
public class UiLanguageScopeTests : IDisposable
{
    private readonly string _language = LocalDate.Language;

    public void Dispose() => LocalDate.Language = _language;

    private static RenderFragment SummaryCard() => b =>
    {
        b.OpenComponent<StatsSummaryCard>(0);
        b.AddAttribute(1, nameof(StatsSummaryCard.AverageGradeLabel), "1,66");
        b.AddAttribute(2, nameof(StatsSummaryCard.AvgGradeLabel), "Avg. grade");
        b.CloseComponent();
    };

    [Fact]
    public void The_body_is_not_rendered_while_the_language_is_unknown()
    {
        LocalDate.Language = "de"; // the static default a fresh start sees
        using var ctx = new BunitContext();

        var cut = ctx.Render<UiLanguageScope>(p => p.Add(c => c.Language, null).Add(c => c.ChildContent, SummaryCard()));

        Assert.DoesNotContain("1,66", cut.Markup);
        Assert.DoesNotContain("1.66", cut.Markup);
    }

    [Fact]
    public void The_body_renders_in_english_once_the_language_arrives_after_the_first_render()
    {
        LocalDate.Language = "de";
        using var ctx = new BunitContext();
        var cut = ctx.Render<UiLanguageScope>(p => p.Add(c => c.Language, null).Add(c => c.ChildContent, SummaryCard()));

        cut.Render(p => p.Add(c => c.Language, "en").Add(c => c.ChildContent, SummaryCard()));

        Assert.Contains("1.66", cut.Markup);
        Assert.DoesNotContain("1,66", cut.Markup);
    }

    [Fact]
    public void Cards_that_were_already_rendered_follow_a_live_language_switch()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<UiLanguageScope>(p => p.Add(c => c.Language, "de").Add(c => c.ChildContent, SummaryCard()));
        Assert.Contains("1,66", cut.Markup);

        cut.Render(p => p.Add(c => c.Language, "en").Add(c => c.ChildContent, SummaryCard()));

        Assert.Contains("1.66", cut.Markup);
        Assert.DoesNotContain("1,66", cut.Markup);
    }
}
