using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Edge.Storage.Services;

namespace Pulse.Edge.Tests.Integration;

// pulse-project#215: the stored history-paused state (set by SyncService from the cloud's 202 +
// quota_exceeded, contract §5.4) reaches the operator through GET /api/dashboard, which the UI's
// HistoryPausedBanner renders. Every signed-in role sees it: it explains missing cloud history.
[Collection("EdgeApi")]
public sealed class DashboardHistoryPauseTests(PulseEdgeAppFactory factory) : IClassFixture<PulseEdgeAppFactory>
{
    private static async Task<JsonElement> HistoryPauseAsync(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/dashboard");
        return json.GetProperty("historyPause");
    }

    [Fact]
    public async Task Paused_shows_quota_and_since_to_every_role_and_clears_back_to_null()
    {
        await factory.ResetDatabaseAsync();
        await factory.SeedDeviceConfigAsync(_ => { });
        var admin = await factory.CreateClientLoggedInAsync("Admin", TestCredentials.AdminUsername, TestCredentials.Password);
        var readOnly = await factory.CreateClientLoggedInAsync("ReadOnly", TestCredentials.ReadOnlyUsername, TestCredentials.Password);
        var storage = new QueueStorageService();

        Assert.Equal(JsonValueKind.Null, (await HistoryPauseAsync(admin)).ValueKind);

        await storage.SetHistoryPauseAsync("storage", new DateTime(2026, 10, 31, 4, 12, 0, DateTimeKind.Utc));
        foreach (var client in new[] { admin, readOnly })
        {
            var pause = await HistoryPauseAsync(client);
            Assert.Equal("storage", pause.GetProperty("quota").GetString());
            var since = pause.GetProperty("since").GetDateTimeOffset();
            Assert.Equal(new DateTimeOffset(2026, 10, 31, 4, 12, 0, TimeSpan.Zero), since);
        }

        await storage.SetHistoryPauseAsync("unknown", null);
        var unknown = await HistoryPauseAsync(admin);
        Assert.Equal("unknown", unknown.GetProperty("quota").GetString());
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("since").ValueKind);

        await storage.SetHistoryPauseAsync(string.Empty, null);
        Assert.Equal(JsonValueKind.Null, (await HistoryPauseAsync(admin)).ValueKind);
    }
}
