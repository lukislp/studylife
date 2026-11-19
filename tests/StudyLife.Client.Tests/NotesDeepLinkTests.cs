// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

/// <summary>Links into the notes page: ?new=1 starts a note, ?note=&lt;id&gt; opens one.</summary>
public class NotesDeepLinkTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("?other=1")]
    public void Without_a_known_parameter_nothing_is_asked_for(string? query) =>
        Assert.Equal(new NotesDeepLink(false, null), NotesDeepLink.Parse(query));

    [Fact]
    public void New_starts_a_fresh_note() =>
        Assert.Equal(new NotesDeepLink(true, null), NotesDeepLink.Parse("?new=1"));

    [Theory]
    [InlineData("?note=42", 42)]
    [InlineData("note=7", 7)]
    [InlineData("?x=1&note=3", 3)]
    public void Note_opens_that_note(string query, int id) =>
        Assert.Equal(new NotesDeepLink(false, id), NotesDeepLink.Parse(query));

    [Theory]
    [InlineData("?note=0")]
    [InlineData("?note=-1")]
    [InlineData("?note=abc")]
    [InlineData("?note=1.5")]
    [InlineData("?note=")]
    public void Anything_but_a_positive_id_is_ignored(string query) =>
        Assert.Null(NotesDeepLink.Parse(query).NoteId);

    [Fact]
    public void New_wins_over_note() =>
        Assert.Equal(new NotesDeepLink(true, null), NotesDeepLink.Parse("?note=4&new=1"));
}
