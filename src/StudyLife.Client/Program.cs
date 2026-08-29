using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using StudyLife.Client;
using StudyLife.Client.Services;
using Toolbelt.Blazor.Extensions.DependencyInjection;
using Toolbelt.Blazor.I18nText;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// i18n: DE/EN UI text via Toolbelt.Blazor.I18nText - JSON tables live in i18ntext/,
// typed Text Table classes are source-generated from them at build time.
builder.Services.AddI18nText(options => options.PersistenceLevel = PersistanceLevel.SessionAndLocal);

// Browser client auth (exclusively since phase 3): the SessionHandler attaches the
// passkey session token (phase 2) as X-Session-Token to every request to the app's own server.
// The former ApiKeyHandler (a global, rotating X-Api-Key with its own bootstrap endpoint) has
// been completely removed - API keys now only exist per user for Home Assistant & co. and
// are never used by the browser.
builder.Services.AddScoped<SessionTokenStore>();
builder.Services.AddScoped(sp =>
{
    var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
    var handler = new SessionHandler(sp.GetRequiredService<SessionTokenStore>(), baseAddress)
    {
        InnerHandler = new HttpClientHandler()
    };
    return new HttpClient(handler) { BaseAddress = baseAddress };
});
builder.Services.AddScoped<AppStateService>();
builder.Services.AddScoped<TimerService>();
builder.Services.AddScoped<NotificationService>();

// Marketplace catalog: reads studylife-marketplace's public listings/ directory directly from
// GitHub's REST API - a separate typed HttpClient (NOT the session-token one above), since this
// talks to api.github.com, not this app's own server, and needs no auth at all (public repo,
// public read-only data). GitHub requires a User-Agent on every request.
builder.Services.AddHttpClient<MarketplaceClient>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("StudyLife-Marketplace-Client");
});

// Native app bridge (MAUI Blazor Hybrid): always the no-op variants in the browser - the
// auth pages resp. NotificationService check IsAvailable and behave here exactly as
// before. The native app (separate studylife-app repo) registers its
// real implementations instead.
builder.Services.AddScoped<INativeAppAuth, NoNativeAppAuth>();
builder.Services.AddScoped<INativePush, NoNativePush>();
builder.Services.AddScoped<INativeIcsIntake, NoNativeIcsIntake>();
builder.Services.AddScoped<INativeFileExport, NoNativeFileExport>();
builder.Services.AddScoped<INativeHealthData, NoNativeHealthData>();

var host = builder.Build();

// Load the session token once from localStorage BEFORE the first render ("is a token present?" -
// app start deliberately checks nothing more than that, no visible login screen for existing users).
// In Blazor WASM, JS interop is already available at this point (everything runs in-process).
await host.Services.GetRequiredService<SessionTokenStore>().InitializeAsync();

// Language default: if the user has NEVER actively chosen a language yet (no
// "Toolbelt.Blazor.I18nText.CurrentLanguage" entry in localStorage, the library's own
// persistence key), German is forced and thereby persisted permanently - without this,
// the library's browser-side language auto-detection often falls back to English/GB even though
// the app is primarily intended for German-speaking users. Runs BEFORE RunAsync so no
// English UI text flashes briefly before switching. A value the user already explicitly
// chose (even "en") is left untouched - this is only a first-launch default.
var js = host.Services.GetRequiredService<IJSRuntime>();
var i18nText = host.Services.GetRequiredService<I18nText>();
try
{
    var storedLanguage = await js.InvokeAsync<string?>("localStorage.getItem", "Toolbelt.Blazor.I18nText.CurrentLanguage");
    if (string.IsNullOrEmpty(storedLanguage))
    {
        await i18nText.SetCurrentLanguageAsync("de");
    }

    // Keep <html lang> in sync with the actually active language - it used to be hardwired
    // to "en" (index.html), which caused Chromium browsers to always render native form
    // controls (especially the calendar time picker, <input type="datetime-local">) in
    // English format (AM/PM) instead of 24h, regardless of the selected UI language.
    var currentLanguage = await i18nText.GetCurrentLanguageAsync();
    await js.InvokeVoidAsync("setDocumentLanguage", currentLanguage);
}
catch { /* localStorage not available (e.g. private mode) - app keeps running with browser auto-detection */ }

await host.RunAsync();
