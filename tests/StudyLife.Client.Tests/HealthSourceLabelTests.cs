// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Bunit;
using StudyLife.Client.Components;
using StudyLife.Client.Components.Dashboard;
using StudyLife.Client.Components.Stats;
using Toolbelt.Blazor.Extensions.DependencyInjection;

namespace StudyLife.Client.Tests;

/// <summary>App Review guideline 2.5.1: every place that shows HealthKit data names Apple Health
/// as its source, through the one shared HealthSourceLabel (the Focus page's movement nudge uses
/// it as well). bUnit has no text loader for the I18nText tables, so the markup is checked here
/// and the product name (HealthHeading, shared with Settings) is checked against the language files.</summary>
public class HealthSourceLabelTests : ConsentBunitTest
{
    public HealthSourceLabelTests()
    {
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddI18nText();
    }

    [Fact]
    public void The_label_renders_a_caption_with_a_decorative_heart()
    {
        var cut = Render<HealthSourceLabel>();

        cut.WaitForElement(".health-source");
        Assert.Equal("true", cut.Find(".health-source-icon").GetAttribute("aria-hidden"));
    }

    [Fact]
    public void The_readiness_tile_carries_the_label() =>
        Render<DashboardReadinessScoreCard>().WaitForElement(".dash-card .health-source");

    [Fact]
    public void The_sleep_tile_carries_the_label() =>
        Render<DashboardSleepConsistencyCard>().WaitForElement(".dash-card .health-source");

    [Fact]
    public void The_cardio_fitness_trend_carries_the_label_with_and_without_data()
    {
        Render<StatsCardioFitnessTrendCard>().WaitForElement(".setup-section .health-source");
        Render<StatsCardioFitnessTrendCard>(p => p
            .Add(c => c.ValueFormat, "{0}")
            .Add(c => c.Points, [new(new DateTime(2026, 9, 1), 41, 40), new(new DateTime(2026, 10, 1), 42, 60)]))
            .WaitForElement(".setup-section .health-source");
    }

    [Theory]
    [MemberData(nameof(PluralTests.AllLanguages), MemberType = typeof(PluralTests))]
    public void The_product_name_the_label_shows_is_set_in_every_language(string language) =>
        Assert.Contains("Apple", PluralTests.ReadValue("NativeAppSettingsText", language, "HealthHeading"));
}
