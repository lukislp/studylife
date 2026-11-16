// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StudyLife.Server.Controllers;
using StudyLife.Server.Data;
using StudyLife.Server.Services;
using StudyLife.Tts;

namespace StudyLife.Server.Tests;

/// <summary>
/// A missing or blank <c>lang</c> is not a 400: a client that does not know its UI language gets
/// the account's language (then the legacy default). The voices directory is empty on purpose, so
/// the answer is the 404 "no voice for language X" and the message names the language that was
/// resolved.
/// </summary>
public class TtsControllerLanguageTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"studylife-tts-{Guid.NewGuid():N}.db");
    private readonly string _voices = Directory.CreateTempSubdirectory("studylife-voices-").FullName;

    private StudyLifeDb NewDb()
    {
        var options = new DbContextOptionsBuilder<StudyLifeDb>().UseSqlite($"Data Source={_dbPath}").Options;
        var db = new StudyLifeDb(options, new TestCurrentUserAccessor());
        db.Database.EnsureCreated();
        return db;
    }

    private async Task<IActionResult> SynthesizeAsync(string? accountLanguage, string? lang)
    {
        using var db = NewDb();
        if (accountLanguage is not null)
            db.Settings.Add(new UserSettingsEntity { Language = accountLanguage });
        db.Notes.Add(new NoteEntity { Title = "t", Content = "Hello world.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var id = db.Notes.Select(n => n.Id).First();

        using var registry = new PiperVoiceRegistry(_voices);
        var controller = new TtsController(db, registry, new EspeakPhonemizer(), new TtsAudioCache(1_000_000));
        return await controller.Synthesize(id, lang);
    }

    private static string ErrorOf(IActionResult result)
    {
        var not = Assert.IsType<NotFoundObjectResult>(result);
        return not.Value!.ToString()!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Empty_lang_uses_the_account_language(string? lang)
    {
        var result = await SynthesizeAsync("fr", lang);
        Assert.Contains("'fr'", ErrorOf(result));
    }

    [Fact]
    public async Task Empty_lang_without_a_reported_account_language_uses_the_legacy_default()
    {
        var result = await SynthesizeAsync(null, null);
        Assert.Contains("'de'", ErrorOf(result));
    }

    [Fact]
    public async Task A_given_lang_still_wins_over_the_account_language()
    {
        var result = await SynthesizeAsync("fr", "es");
        Assert.Contains("'es'", ErrorOf(result));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }
        try { Directory.Delete(_voices, true); } catch (IOException) { }
    }
}

/// <summary>Audit L7: the language code becomes part of a voice file name, so only the product's
/// own codes may reach the file system - even when a file with the crafted name exists.</summary>
public class PiperVoiceRegistryLanguageTests : IDisposable
{
    private readonly string _voices = Directory.CreateTempSubdirectory("studylife-voices-").FullName;

    [Theory]
    [InlineData("..")]
    [InlineData("../secret")]
    [InlineData("a/b")]
    [InlineData("xx")]
    [InlineData("DE")]
    [InlineData("")]
    public void TryGet_NonSupportedCode_IsRefusedWithoutTouchingFiles(string lang)
    {
        File.WriteAllText(Path.Combine(_voices, lang.Replace("/", "_") + ".onnx"), "x");
        using var registry = new PiperVoiceRegistry(_voices);
        Assert.Null(registry.TryGet(lang));
    }

    public void Dispose()
    {
        try { Directory.Delete(_voices, true); } catch (IOException) { }
    }
}
