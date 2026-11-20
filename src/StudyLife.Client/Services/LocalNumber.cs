// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using StudyLife.Shared;

namespace StudyLife.Client.Services;

/// <summary>
/// Number formatting that follows the UI language, not the process culture. The client runs with
/// InvariantGlobalization, so the current culture is always invariant (decimal point) and says
/// nothing about the language the user picked; like <see cref="LocalDate"/>, this reads
/// <see cref="LocalDate.Language"/> and builds its separator from a bare NumberFormatInfo (no
/// CultureInfo construction, which would throw without ICU).
///
/// English, Irish and Maltese write a decimal point, every other supported UI language a comma.
/// Only for text a person reads: CSS values and other machine formats stay on InvariantCulture.
/// </summary>
public static class LocalNumber
{
    private static readonly NumberFormatInfo Point = new() { NumberDecimalSeparator = "." };
    private static readonly NumberFormatInfo Comma = new() { NumberDecimalSeparator = "," };

    /// <summary>The number format of the current UI language.</summary>
    public static NumberFormatInfo Format => UsesPoint(LocalDate.Language) ? Point : Comma;

    public static string Number(decimal value, string format) => value.ToString(format, Format);

    public static string Number(double value, string format) => value.ToString(format, Format);

    /// <summary>A grade in the UI language (e.g. "1,70" in German, "1.70" in English).</summary>
    public static string Grade(decimal grade) => Number(grade, "0.00");

    public static string Grade(decimal grade, string format) => Number(grade, format);

    /// <summary>
    /// Re-expresses a server-built label, which always uses the documented comma convention
    /// (see <see cref="StudyMetrics.FormatGrade(decimal)"/>), in the UI language. Only the decimal
    /// comma is touched; labels without one (a dash, whole numbers) pass through unchanged.
    /// </summary>
    public static string? Localize(string? serverLabel) =>
        serverLabel is not null && UsesPoint(LocalDate.Language) ? serverLabel.Replace(',', '.') : serverLabel;

    private static bool UsesPoint(string language)
    {
        var code = (language.Length >= 2 ? language[..2] : language).ToLowerInvariant();
        return code is "en" or "ga" or "mt";
    }
}