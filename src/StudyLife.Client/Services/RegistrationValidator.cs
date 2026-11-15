// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>The fields of the registration form that can be left empty.</summary>
public enum RegistrationField
{
    DisplayName,
    Username,
    Password,
}

/// <summary>
/// Client-side check of the required registration fields, shared by the passkey and the password
/// path so the submit button can stay enabled and still tell the user what is missing. Only
/// presence is checked here; every real rule (name format, password strength, gates) stays on the
/// server.
/// </summary>
public static class RegistrationValidator
{
    /// <summary>The missing fields in form order; empty when the form can be sent. The username
    /// and password only count for the password method.</summary>
    public static IReadOnlyList<RegistrationField> MissingFields(
        string? displayName, string? username, string? password, bool usePassword)
    {
        var missing = new List<RegistrationField>(3);
        if (string.IsNullOrWhiteSpace(displayName)) missing.Add(RegistrationField.DisplayName);
        if (usePassword)
        {
            if (string.IsNullOrWhiteSpace(username)) missing.Add(RegistrationField.Username);
            if (string.IsNullOrEmpty(password)) missing.Add(RegistrationField.Password);
        }
        return missing;
    }
}
