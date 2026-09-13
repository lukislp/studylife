using System.Net;
using System.Net.Http.Json;

namespace StudyLife.Server.Tests;

/// <summary>
/// One parameterised pass over every single-key API slot in SettingsController (ha, ai, mcp,
/// capture, focusguard, focustunes, tray, developer). The eight status/generate/revoke trios
/// used to be eight copies of the same body; they now share ApiKeySlot plus three private
/// helpers, so this pins the behaviour that the sharing must not change - per-slot isolation
/// above all: generating into one slot must leave the other seven empty, and revoking one must
/// not disturb them either. A descriptor wired to the wrong column pair would fail here rather
/// than silently cross-wire two integrations' credentials.
///
/// Own dedicated class/factory: rotates and revokes AuthUserId 1's keys, which several other
/// suites read.
/// </summary>
public class ApiKeySlotEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ApiKeySlotEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // Route prefixes exactly as clients use them - these are wire contract, not an
    // implementation detail (docs/api/openapi.json, three consumer repos).
    private static readonly string[] AllSlots =
    [
        "ha-api-key", "ai-api-key", "mcp-api-key", "capture-api-key",
        "focusguard-api-key", "focustunes-api-key", "tray-api-key", "developer-api-key",
    ];

    public static TheoryData<string> Slots()
    {
        var data = new TheoryData<string>();
        foreach (var slot in AllSlots) data.Add(slot);
        return data;
    }

    private sealed record KeyStatus(bool HasKey, DateTime? CreatedAt);

    private sealed record KeyGenerated(string ApiKey, DateTime CreatedAt);

    private async Task<KeyStatus> GetStatusAsync(string slot)
    {
        var response = await _client.GetAsync($"/api/settings/{slot}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<KeyStatus>())!;
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public async Task ApiKeySlot_GenerateStatusRevoke_IsSelfContainedPerSlot(string slot)
    {
        // Each case starts from "no keys at all" - the theory cases share one factory/DB.
        foreach (var other in AllSlots)
            Assert.Equal(HttpStatusCode.NoContent,
                (await _client.PostAsync($"/api/settings/{other}/revoke", null)).StatusCode);

        var generateResponse = await _client.PostAsync($"/api/settings/{slot}/generate", null);
        Assert.Equal(HttpStatusCode.OK, generateResponse.StatusCode);
        var generated = await generateResponse.Content.ReadFromJsonAsync<KeyGenerated>();
        Assert.NotNull(generated);
        Assert.NotEmpty(generated!.ApiKey);
        Assert.NotEqual(default, generated.CreatedAt);

        var status = await GetStatusAsync(slot);
        Assert.True(status.HasKey);
        Assert.Equal(generated.CreatedAt, status.CreatedAt);

        // Only THIS slot was written - a descriptor pointing at the wrong column pair shows up
        // as a sibling slot suddenly reporting a key.
        foreach (var other in AllSlots.Where(s => s != slot))
            Assert.False((await GetStatusAsync(other)).HasKey, $"{other} must be untouched by {slot}");

        // The plaintext really authenticates, i.e. what was stored is the hash of exactly this
        // key (whoami is in every slot's scope set, see ApiKeyScopes.Whoami).
        using (var keyClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
            Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/auth/whoami")).StatusCode);

        var revokeResponse = await _client.PostAsync($"/api/settings/{slot}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var afterRevoke = await GetStatusAsync(slot);
        Assert.False(afterRevoke.HasKey);
        // Both columns are cleared, never just the hash - a leftover timestamp would make the
        // setup card claim a key exists that no longer does.
        Assert.Null(afterRevoke.CreatedAt);

        using (var revokedClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, generated.ApiKey))
            Assert.Equal(HttpStatusCode.Unauthorized, (await revokedClient.GetAsync("/api/auth/whoami")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public async Task ApiKeySlot_Regenerate_InvalidatesThePreviousKeyImmediately(string slot)
    {
        var first = await (await _client.PostAsync($"/api/settings/{slot}/generate", null))
            .Content.ReadFromJsonAsync<KeyGenerated>();
        var second = await (await _client.PostAsync($"/api/settings/{slot}/generate", null))
            .Content.ReadFromJsonAsync<KeyGenerated>();
        Assert.NotEqual(first!.ApiKey, second!.ApiKey);

        using (var oldClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, first.ApiKey))
            Assert.Equal(HttpStatusCode.Unauthorized, (await oldClient.GetAsync("/api/auth/whoami")).StatusCode);
        using (var newClient = ApiKeyTestHelpers.CreateClientWithKey(_factory, second.ApiKey))
            Assert.Equal(HttpStatusCode.OK, (await newClient.GetAsync("/api/auth/whoami")).StatusCode);

        await _client.PostAsync($"/api/settings/{slot}/revoke", null);
    }
}
