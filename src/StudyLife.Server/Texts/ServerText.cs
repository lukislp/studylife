// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.Server.Texts;

/// <summary>
/// The server text catalogue: every user-visible text the server sends without a client in the
/// loop (push notifications, Live Activity alerts) lives in <c>Texts/ServerText.&lt;lang&gt;.json</c>,
/// one flat key-to-template file per supported language, embedded in this assembly. Senders only
/// ask <see cref="Text"/> (or the bound <see cref="For"/> form) for a key; they never build a
/// sentence themselves. The client has its own i18n tables for what the UI shows - these two
/// sets are separate on purpose: the client tables are per-page, loaded in the browser and keyed
/// to Razor components, while these are per-notification templates the server formats with the
/// user's culture. tools/check-i18n.py enforces the same key set and the same placeholders in all
/// languages, ServerTextTests the same against <see cref="SupportedLanguages"/>.
/// </summary>
public static class ServerText
{
    /// <summary>The catalogue loaded from the embedded language files, once.</summary>
    public static ServerTextCatalogue Default { get; } = ServerTextCatalogue.FromEmbeddedResources();

    /// <summary>
    /// The text for <paramref name="key"/> in <paramref name="language"/>, with
    /// <paramref name="args"/> formatted in that language's culture (decimal separator, short
    /// date, clock time). Fallback chain: the language, then English, then the key itself - a
    /// missing translation degrades to English, never to an empty notification. A null or
    /// unsupported language is <see cref="SupportedLanguages.LegacyDefault"/> (what the server
    /// sent before it knew languages).
    /// </summary>
    public static string Text(string key, string? language, params object?[] args) =>
        Default.Text(key, language, args);

    /// <summary>The lookup bound to one language, for a sender that formats several texts for the
    /// same user.</summary>
    public static ServerTextLocalizer For(string? language) => new(Default, language);
}

/// <summary>A catalogue lookup bound to one user's language.</summary>
public readonly record struct ServerTextLocalizer(ServerTextCatalogue Catalogue, string? Language)
{
    /// <summary>The language the texts come out in (resolved, never null).</summary>
    public string ResolvedLanguage => ServerTextCatalogue.Resolve(Language);

    /// <summary>The culture dates, times and numbers are formatted with.</summary>
    public CultureInfo Culture => ServerTextCatalogue.CultureFor(Language);

    public string T(string key, params object?[] args) => Catalogue.Text(key, Language, args);
}

/// <summary>The data and the pure lookup behind <see cref="ServerText"/>; instantiable so tests
/// can build a small catalogue by hand.</summary>
public sealed class ServerTextCatalogue
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _byLanguage;

    public ServerTextCatalogue(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> byLanguage)
        => _byLanguage = byLanguage;

    /// <summary>The language codes this catalogue has a table for.</summary>
    public IReadOnlyCollection<string> Languages => _byLanguage.Keys.ToList();

    /// <summary>The keys of one language's table (empty for an unknown language).</summary>
    public IReadOnlyCollection<string> Keys(string language) =>
        _byLanguage.TryGetValue(language, out var table) ? table.Keys.ToList() : Array.Empty<string>();

    /// <summary>The template of one language's table, null when that table lacks the key.</summary>
    public string? Template(string key, string language) =>
        _byLanguage.TryGetValue(language, out var table) && table.TryGetValue(key, out var template) ? template : null;

    /// <summary>The language the lookup really uses: the supported match of the input, else
    /// <see cref="SupportedLanguages.LegacyDefault"/>.</summary>
    public static string Resolve(string? language) =>
        SupportedLanguages.Match(language) ?? SupportedLanguages.LegacyDefault;

    /// <summary>The culture of a language; the invariant culture when the runtime has no data for
    /// it (invariant-globalization images).</summary>
    public static CultureInfo CultureFor(string? language)
    {
        try { return CultureInfo.GetCultureInfo(Resolve(language)); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    public string Text(string key, string? language, params object?[] args)
    {
        var resolved = Resolve(language);
        var template = Template(key, resolved) ?? Template(key, SupportedLanguages.Fallback);
        if (template is null) return key;
        if (args.Length == 0) return template;
        try
        {
            return string.Format(CultureFor(resolved), template, args);
        }
        catch (FormatException)
        {
            // A template/argument mismatch must cost the user a formatted number at worst, never
            // the whole notification (the catalogue checks make this unreachable in practice).
            return template;
        }
    }

    /// <summary>Reads every <c>ServerText.&lt;lang&gt;.json</c> embedded in this assembly.</summary>
    public static ServerTextCatalogue FromEmbeddedResources()
    {
        var assembly = typeof(ServerTextCatalogue).Assembly;
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var language in SupportedLanguages.Codes)
        {
            using var stream = assembly.GetManifestResourceStream($"StudyLife.Server.Texts.ServerText.{language}.json")
                ?? throw new InvalidOperationException($"Embedded server text table for '{language}' is missing.");
            tables[language] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidOperationException($"Server text table for '{language}' is empty.");
        }
        return new ServerTextCatalogue(tables);
    }
}
