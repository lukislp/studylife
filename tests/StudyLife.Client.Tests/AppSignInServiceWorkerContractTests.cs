// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Tests;

/// <summary>
/// Device test round 8 (2026-11-17): the native app's sign-in in the system browser ran a client
/// bundle the service worker had precached before a server update, and its handoff (the pre-audit
/// request shape) was refused with a 401 for every passkey and password sign-in. The rules that
/// keep those pages on the server's own bundle live in service-worker-app-sign-in.js; this pins
/// that both workers apply them, since no other test runs the workers.
/// </summary>
public class AppSignInServiceWorkerContractTests
{
    private static string WwwrootFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StudyLife.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "StudyLife.Client", "wwwroot", name));
    }

    [Theory]
    [InlineData("service-worker.published.js")]
    [InlineData("service-worker.js")]
    public void Both_workers_serve_app_sign_in_pages_from_the_network_and_reload_stale_ones(string worker)
    {
        var js = WwwrootFile(worker);

        Assert.Contains("self.importScripts('service-worker-app-sign-in.js');", js);
        // Checked before the precache lookup, otherwise the precached bundle still wins.
        var bypass = js.IndexOf("if (await isAppSignInRequest(event)) return fetchForAppSignIn(event);", StringComparison.Ordinal);
        var precache = js.IndexOf("const precached = await cache.match(event.request);", StringComparison.Ordinal);
        Assert.True(bypass >= 0 && bypass < precache, "the sign-in bypass must run before the precache lookup");
        // Stale pages are found before claim() (afterwards matchAll no longer tells old from new)
        // and reloaded after it (navigate() needs control).
        var find = js.IndexOf("await findStaleAppSignInPages()", StringComparison.Ordinal);
        var claim = js.IndexOf("self.clients.claim()", StringComparison.Ordinal);
        var reload = js.IndexOf("await reloadAppSignInPages(", StringComparison.Ordinal);
        Assert.True(find >= 0 && find < claim && claim < reload, "find stale pages, claim, then reload them");
    }

    [Fact]
    public void An_app_sign_in_page_is_recognised_by_the_same_marker_AppReturnContext_reads()
    {
        var js = WwwrootFile("service-worker-app-sign-in.js");

        // AppReturnContext.FromUri: "app=1" turns a page into the native app's sign-in.
        Assert.Contains("searchParams.get('app') === '1'", js);
    }
}
