// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Models;
using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

public class AutoFocusSwitchPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0);

    private static UserSettings Settings(bool on = true, int before = 2) =>
        new() { AutoSwitchFocus = on, AutoSwitchMinutesBefore = before };

    private static StudySession Session(int id, double startsInMinutes = -5) =>
        new() { Id = id, StartTime = Now.AddMinutes(startsInMinutes), EndTime = Now.AddMinutes(startsInMinutes + 60) };

    [Fact]
    public void ActiveSession_OnOrdinaryPage_Navigates()
    {
        var d = AutoFocusSwitchPolicy.Decide(Settings(), Session(1), null, Now, "/calendar", null);
        Assert.True(d.Navigate);
        Assert.Equal(1, d.HandledSessionId);
    }

    [Fact]
    public void SettingOff_DoesNothing()
    {
        var d = AutoFocusSwitchPolicy.Decide(Settings(on: false), Session(1), null, Now, "/calendar", null);
        Assert.False(d.Navigate);
        Assert.Null(d.HandledSessionId);
    }

    [Fact]
    public void NoSession_DoesNothing()
    {
        var d = AutoFocusSwitchPolicy.Decide(Settings(), null, null, Now, "/calendar", null);
        Assert.False(d.Navigate);
        Assert.Null(d.HandledSessionId);
    }

    [Fact]
    public void SameSessionAlreadyHandled_DoesNotPullBack()
    {
        var d = AutoFocusSwitchPolicy.Decide(Settings(), Session(1), null, Now, "/calendar", 1);
        Assert.False(d.Navigate);
    }

    [Fact]
    public void NewSession_FiresAgain()
    {
        var d = AutoFocusSwitchPolicy.Decide(Settings(), Session(2), null, Now, "/calendar", 1);
        Assert.True(d.Navigate);
        Assert.Equal(2, d.HandledSessionId);
    }

    [Theory]
    [InlineData("/focus")]
    [InlineData("/focus/")]
    [InlineData("/Focus?x=1")]
    public void AlreadyOnFocus_DoesNotNavigateButMarksHandled(string path)
    {
        var d = AutoFocusSwitchPolicy.Decide(Settings(), Session(1), null, Now, path, null);
        Assert.False(d.Navigate);
        Assert.Equal(1, d.HandledSessionId);
    }

    [Fact]
    public void FocusRouteCheckIsExact()
    {
        Assert.False(AutoFocusSwitchPolicy.IsFocus("/focusguard-help"));
        Assert.False(AutoFocusSwitchPolicy.IsFocus("/connect/focusguard"));
        var d = AutoFocusSwitchPolicy.Decide(Settings(), Session(1), null, Now, "/focusguard-help", null);
        Assert.True(d.Navigate);
    }

    [Theory]
    [InlineData("/connect/capture")]
    [InlineData("/connect/mcp?redirect_uri=https%3A%2F%2Fx.example%2Fcb&state=s")]
    [InlineData("/connect/client/abc")]
    [InlineData("/connect/focusguard")]
    [InlineData("/login?returnUrl=%2Ffocus")]
    [InlineData("/register")]
    [InlineData("/link")]
    [InlineData("/setup")]
    [InlineData("/setup?forgotPassword=true#password")]
    [InlineData("/notes")]
    [InlineData("/shared/token123")]
    [InlineData("/authorize")]
    [InlineData("/oauth/authorize")]
    public void FlowPages_AreNeverInterrupted_AndSessionIsSkipped(string path)
    {
        var d = AutoFocusSwitchPolicy.Decide(Settings(), Session(1), null, Now, path, null);
        Assert.False(d.Navigate);
        Assert.Equal(1, d.HandledSessionId);

        // After leaving the flow page, the skipped session must not fire out of the blue.
        var later = AutoFocusSwitchPolicy.Decide(Settings(), Session(1), null, Now.AddSeconds(30), "/calendar", d.HandledSessionId);
        Assert.False(later.Navigate);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/calendar")]
    [InlineData("/planner")]
    [InlineData("/report")]
    [InlineData("/auswertung")]
    [InlineData("/weekplan")]
    [InlineData("/wrapped")]
    public void OrdinaryPages_AreNotProtected(string path) =>
        Assert.False(AutoFocusSwitchPolicy.IsProtectedFlowPath(path));

    [Fact]
    public void Upcoming_InsideWindow_Navigates()
    {
        var d = AutoFocusSwitchPolicy.Decide(Settings(before: 2), null, Session(7, startsInMinutes: 1.5), Now, "/calendar", null);
        Assert.True(d.Navigate);
        Assert.Equal(7, d.HandledSessionId);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-1)]
    public void Upcoming_OutsideWindow_DoesNothing(double minutes)
    {
        var d = AutoFocusSwitchPolicy.Decide(Settings(before: 2), null, Session(7, minutes), Now, "/calendar", null);
        Assert.False(d.Navigate);
        Assert.Null(d.HandledSessionId);
    }

    [Fact]
    public void UpcomingThenActive_SameSession_FiresOnlyOnce()
    {
        var first = AutoFocusSwitchPolicy.Decide(Settings(), null, Session(7, 1), Now, "/calendar", null);
        Assert.True(first.Navigate);
        var second = AutoFocusSwitchPolicy.Decide(Settings(), Session(7, -1), null, Now.AddMinutes(2), "/calendar", first.HandledSessionId);
        Assert.False(second.Navigate);
    }

    [Fact]
    public void Upcoming_InsideWindow_OnFlowPage_IsSkipped()
    {
        var d = AutoFocusSwitchPolicy.Decide(Settings(), null, Session(7, 1), Now, "/connect/capture", null);
        Assert.False(d.Navigate);
        Assert.Equal(7, d.HandledSessionId);
    }
}