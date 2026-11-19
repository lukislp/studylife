// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>
/// What a link to the notes page asks for: <c>/notes?new=1</c> starts a fresh note (the PWA's "New
/// note" shortcut, the native app's quick actions), <c>/notes?note=42</c> opens note 42 (the native
/// app's store screenshots, any link that points at one note). <c>new</c> wins when both are given.
/// Pure: the page applies the result once its notes are loaded.
/// </summary>
public readonly record struct NotesDeepLink(bool NewNote, int? NoteId)
{
    public static NotesDeepLink Parse(string? query)
    {
        var values = System.Web.HttpUtility.ParseQueryString(query ?? "");
        if (values["new"] == "1")
            return new(true, null);
        return int.TryParse(values["note"], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
            ? new(false, id)
            : new(false, null);
    }
}
