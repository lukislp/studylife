// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

/// <summary>Which proofs the shared "confirm it is you" step offers.</summary>
public class FreshProofOptionsTests
{
    [Theory]
    [InlineData(true, false, true, FreshProofPrimary.BrowserSignIn)]
    [InlineData(true, false, false, FreshProofPrimary.BrowserSignIn)] // the sign-in page offers whatever the account has
    [InlineData(true, true, true, FreshProofPrimary.InAppPasskey)]
    [InlineData(true, true, false, FreshProofPrimary.None)]
    [InlineData(false, false, true, FreshProofPrimary.WebPasskey)]
    [InlineData(false, true, true, FreshProofPrimary.WebPasskey)]
    [InlineData(false, false, false, FreshProofPrimary.None)]
    public void ThePrimaryRoute_DependsOnThePlatformAndThePasskey(bool native, bool inApp, bool hasPasskey, FreshProofPrimary expected) =>
        Assert.Equal(expected, FreshProofOptions.Decide(native, inApp, hasPasskey, hasUsablePassword: false).Primary);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheCurrentPassword_IsOfferedOnlyWhenTheAccountHasOne(bool hasPassword) =>
        Assert.Equal(hasPassword, FreshProofOptions.Decide(false, false, true, hasPassword).CurrentPassword);

    [Fact]
    public void TheRecoveryCode_IsAlwaysOffered()
    {
        Assert.True(FreshProofOptions.Decide(true, false, false, false).RecoveryCode);
        Assert.True(FreshProofOptions.Decide(false, false, true, true).RecoveryCode);
    }

    [Fact]
    public void AProofWithNothingInIt_IsAFreshSession() =>
        Assert.True(new FreshProof().IsFreshSession);

    [Fact]
    public void AProofWithACode_IsNotAFreshSession() =>
        Assert.False(new FreshProof(RecoveryCode: "ABCD").IsFreshSession);
}
