// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Shared;

namespace StudyLife.Server.Services.SampleData;

/// <summary>Language of the sample dataset (operator options <c>SampleData:Language</c> and
/// <c>Demo:Language</c>).</summary>
public enum SampleDataLanguage
{
    /// <summary>The built-in study program and German texts - the demo's default.</summary>
    German,
    /// <summary>An English copy of the study program (as the account's own program) and English texts.</summary>
    English,
}

/// <summary>A study program the sample data creates as the account's own (custom) program.</summary>
public sealed record SampleProgram(string Name, IReadOnlyList<CourseDto> Courses);

/// <summary>A note of the sample dataset; <c>CourseIndex</c> points into the active courses.</summary>
public sealed record SampleNote(int CourseIndex, string Title, string Content);

/// <summary>
/// Everything language-specific in the sample dataset: where the courses come from and the few
/// free texts. The structure (which courses are completed or active, dates, durations, counts) is
/// the same for every language and lives in <see cref="SampleDataset"/>.
/// </summary>
public sealed record SampleDataContent(SampleProgram? Program, string CompletionNote, IReadOnlyList<SampleNote> Notes)
{
    /// <summary>Accepted values of <c>SampleData:Language</c> and <c>Demo:Language</c>
    /// (<paramref name="setting"/> names the one in the error): "de" (also unset/blank) and "en",
    /// case-insensitive. Anything else is refused, so a typo cannot silently seed the wrong language.</summary>
    public static SampleDataLanguage ParseLanguage(string? value, string setting = "SampleData:Language") =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "de" => SampleDataLanguage.German,
            "en" => SampleDataLanguage.English,
            _ => throw new InvalidOperationException(
                $"{setting} must be \"de\" or \"en\" (got \"{value}\")."),
        };

    public static SampleDataContent For(SampleDataLanguage language) => language switch
    {
        SampleDataLanguage.English => English,
        _ => German,
    };

    /// <summary>The built-in catalog (Program null) with German texts - the demo's default.</summary>
    public static readonly SampleDataContent German = new(
        Program: null,
        CompletionNote: "Klausur lief besser als erwartet.",
        Notes:
        [
            new(0, "Merkzettel Eigenwerte", "Eigenwerte über det(A - λI) = 0 bestimmen.\nEigenvektoren: (A - λI)x = 0 lösen.\nWichtig für die Klausur: Diagonalisierbarkeit prüfen!"),
            new(1, "Verteilungen Übersicht", "Binomial: diskret, n Versuche, Trefferwahrscheinlichkeit p.\nNormal: stetig, μ und σ².\nPoisson: seltene Ereignisse pro Intervall.\nFaustregel: n·p > 5 und n·(1-p) > 5 → Normalapproximation ok."),
            new(2, "Fragen an Tutor", "- Unterschied Konfidenzintervall vs. Prognoseintervall nochmal durchgehen\n- Übungsblatt 4, Aufgabe 3b\n- Klausurzulassung: reicht das Testat?"),
        ]);

    /// <summary>English program (<see cref="EnglishSampleProgram"/>) with English texts.</summary>
    public static readonly SampleDataContent English = new(
        Program: EnglishSampleProgram.Program,
        CompletionNote: "The exam went better than expected.",
        Notes:
        [
            new(0, "Eigenvalues cheat sheet", "Find the eigenvalues with det(A - λI) = 0.\nEigenvectors: solve (A - λI)x = 0.\nFor the exam: check whether the matrix is diagonalizable!"),
            new(1, "Distributions at a glance", "Binomial: discrete, n trials, success probability p.\nNormal: continuous, μ and σ².\nPoisson: rare events per interval.\nRule of thumb: n·p > 5 and n·(1-p) > 5 → the normal approximation is fine."),
            new(2, "Questions for the tutor", "- Go over confidence interval vs. prediction interval once more\n- Problem set 4, exercise 3b\n- Exam admission: is the lab test enough?"),
        ]);
}
