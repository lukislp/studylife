// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>The app publisher's privacy policy on legal.lktec.org: the one place for its addresses.
/// The policy says the operator of a StudyLife server is responsible for that server.</summary>
public static class PrivacyPolicyLink
{
    public const string German = "https://legal.lktec.org/studylife/";

    public const string English = "https://legal.lktec.org/en/studylife/";

    /// <summary>The German page for a German UI, the English page for every other language.</summary>
    public static string For(string? language) =>
        language is not null && language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? German : English;
}
