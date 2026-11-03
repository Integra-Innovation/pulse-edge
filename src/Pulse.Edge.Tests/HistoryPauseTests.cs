using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pulse.Edge.Cloud.Services;
using Pulse.Edge.Storage;
using Pulse.Edge.Storage.Models;
using Pulse.Edge.Storage.Services;
using Xunit;

namespace Pulse.Edge.Tests;

// pulse-project#215 — edge contract docs/edge-contract/onboarding.md §5.4: at the IoT allowance or
// storage limit PULSE Cloud still answers POST /edge/telemetry with 202, accepted = 0, and
// quota_exceeded { quota: iot | storage, history: "paused", since }. The batch is delivered (never
// retried, never kept for backfill); the operator sees a stable status; a plain 202 clears it.
public class HistoryPauseParsingTests
{
    private const string BaseUrl = "http://localhost:3000";

    private static List<QueueTelemetry> Batch() => new()
    {
        new QueueTelemetry { Id = 1, DataSourceId = "DS001", Timestamp = DateTime.UtcNow, MetricsJson = "{\"kw\":1}", QualitiesJson = "{\"kw\":\"Good\"}" },
    };

    private static Task<TelemetrySendOutcome> Send(HttpStatusCode status, string body) =>
        new CloudClient(NullLogger<CloudClient>.Instance, new ScriptedHandler(status, body))
            .SendTelemetryBatchWithOutcomeAsync(BaseUrl, "dev", "key", Batch());

    [Fact]
    public void Normal202_HasCounts_AndNoNotice()
    {
        var (res, pause) = CloudClient.ParseTelemetryAccepted("{\"accepted\":2,\"rejected\":0,\"errors\":[]}");
        Assert.Equal(2, res!.Accepted);
        Assert.Null(pause);
    }

    [Theory]
    [InlineData("iot", "iot")]
    [InlineData("storage", "storage")]
    [InlineData("bandwidth", "unknown")] // a reason this edge doesn't know yet is still a pause
    public void Paused202_EachQuotaReason(string wire, string expected)
    {
        var (res, pause) = CloudClient.ParseTelemetryAccepted(
            $"{{\"accepted\":0,\"rejected\":0,\"errors\":[],\"quota_exceeded\":{{\"quota\":\"{wire}\",\"history\":\"paused\",\"since\":\"2026-10-31T04:12:00.000Z\"}}}}");
        Assert.Equal(0, res!.Accepted);
        Assert.Equal(expected, pause!.Quota);
        Assert.Equal(new DateTime(2026, 10, 31, 4, 12, 0, DateTimeKind.Utc), pause.SinceUtc);
        Assert.Equal(DateTimeKind.Utc, pause.SinceUtc!.Value.Kind);
    }

    [Fact]
    public void Since_WithOffset_IsNormalizedToUtc()
    {
        var (_, pause) = CloudClient.ParseTelemetryAccepted(
            "{\"accepted\":0,\"quota_exceeded\":{\"quota\":\"iot\",\"history\":\"paused\",\"since\":\"2026-10-31T11:12:00+07:00\"}}");
        Assert.Equal(new DateTime(2026, 10, 31, 4, 12, 0, DateTimeKind.Utc), pause!.SinceUtc);
    }

    [Fact]
    public void UnknownFields_AreIgnored()
    {
        var (res, pause) = CloudClient.ParseTelemetryAccepted(
            "{\"accepted\":0,\"rejected\":0,\"errors\":[],\"newTopLevel\":{\"x\":1},\"quota_exceeded\":{\"quota\":\"storage\",\"history\":\"paused\",\"since\":\"2026-10-31T04:12:00Z\",\"limitBytes\":123,\"hint\":\"upgrade\"}}");
        Assert.NotNull(res);
        Assert.Equal("storage", pause!.Quota);
    }

    [Theory]
    [InlineData("\"quota_exceeded\":null")]
    [InlineData("\"quota_exceeded\":\"paused\"")]                                        // not an object
    [InlineData("\"quota_exceeded\":[1,2]")]
    [InlineData("\"quota_exceeded\":{\"quota\":\"iot\",\"since\":\"2026-10-31T04:12:00Z\"}\n")] // no history
    [InlineData("\"quota_exceeded\":{\"quota\":\"iot\",\"history\":\"recording\"}")]       // a future non-paused value
    [InlineData("\"quota_exceeded\":{\"quota\":\"iot\",\"history\":true}")]                // wrong type
    public void NotAPausedNotice_GivesNoNotice_AndStillReadsCounts(string fragment)
    {
        var (res, pause) = CloudClient.ParseTelemetryAccepted($"{{\"accepted\":3,\"rejected\":0,{fragment}}}");
        Assert.Equal(3, res!.Accepted);
        Assert.Null(pause);
    }

    [Theory]
    [InlineData("\"since\":\"yesterday\"")]
    [InlineData("\"since\":12345")]
    [InlineData("\"since\":null")]
    [InlineData("\"other\":1")] // since missing
    public void MalformedOrMissingSince_StillPaused_WithUnknownSince(string sinceFragment)
    {
        var (_, pause) = CloudClient.ParseTelemetryAccepted(
            $"{{\"accepted\":0,\"quota_exceeded\":{{\"quota\":\"iot\",\"history\":\"paused\",{sinceFragment}}}}}");
        Assert.Equal("iot", pause!.Quota);
        Assert.Null(pause.SinceUtc);
    }

    [Fact]
    public void MalformedQuota_StillPaused_AsUnknown()
    {
        var (_, pause) = CloudClient.ParseTelemetryAccepted("{\"quota_exceeded\":{\"quota\":7,\"history\":\"paused\"}}");
        Assert.Equal("unknown", pause!.Quota);
    }

    [Fact]
    public void MalformedCounts_StillParseTheNotice()
    {
        var (res, pause) = CloudClient.ParseTelemetryAccepted(
            "{\"accepted\":\"lots\",\"quota_exceeded\":{\"quota\":\"iot\",\"history\":\"paused\",\"since\":\"2026-10-31T04:12:00Z\"}}");
        Assert.Null(res);
        Assert.Equal("iot", pause!.Quota);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"accepted\":")]
    public void UnreadableBody_GivesNothing_WithoutThrowing(string body)
    {
        var (res, pause) = CloudClient.ParseTelemetryAccepted(body);
        Assert.Null(res);
        Assert.Null(pause);
    }

    [Fact]
    public async Task Send_Paused202_IsSuccess_WithTheNotice()
    {
        var outcome = await Send(HttpStatusCode.Accepted,
            "{\"accepted\":0,\"rejected\":0,\"errors\":[],\"quota_exceeded\":{\"quota\":\"iot\",\"history\":\"paused\",\"since\":\"2026-10-31T04:12:00.000Z\"}}");
        Assert.Equal(TelemetrySyncResult.Success, outcome.Result);
        Assert.Equal("iot", outcome.HistoryPause!.Quota);
    }

    [Fact]
    public async Task Send_Normal202_IsSuccess_WithoutNotice()
    {
        var outcome = await Send(HttpStatusCode.Accepted, "{\"accepted\":1,\"rejected\":0,\"errors\":[]}");
        Assert.Equal(TelemetrySyncResult.Success, outcome.Result);
        Assert.Null(outcome.HistoryPause);
    }

    [Fact]
    public async Task Send_Unreadable202_IsStillSuccess_NotARetryLoop()
    {
        // Before #215 a 202 body that failed to deserialize threw, was caught, and came back as
        // TransientError: the batch was re-sent forever although the server had taken it.
        var outcome = await Send(HttpStatusCode.Accepted, "<html>proxy page</html>");
        Assert.Equal(TelemetrySyncResult.Success, outcome.Result);
        Assert.Null(outcome.HistoryPause);
        Assert.Equal(TelemetrySyncResult.Success, await new CloudClient(NullLogger<CloudClient>.Instance,
            new ScriptedHandler(HttpStatusCode.Accepted, "{\"accepted\":\"x\"}")).SendTelemetryBatchAsync(BaseUrl, "dev", "key", Batch()));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, TelemetrySyncResult.TransientError)]
    [InlineData(HttpStatusCode.InternalServerError, TelemetrySyncResult.TransientError)]
    [InlineData(HttpStatusCode.BadRequest, TelemetrySyncResult.ClientError)]
    [InlineData(HttpStatusCode.Unauthorized, TelemetrySyncResult.Unauthorized)]
    public async Task Send_NonAccepted_KeepsItsMeaning_AndNeverCarriesANotice(HttpStatusCode status, TelemetrySyncResult expected)
    {
        // Even if a non-202 body happened to contain the notice, it is not a delivered batch.
        var outcome = await Send(status, "{\"quota_exceeded\":{\"quota\":\"iot\",\"history\":\"paused\"}}");
        Assert.Equal(expected, outcome.Result);
        Assert.Null(outcome.HistoryPause);
    }
}

public class HistoryPauseDecisionTests
{
    private static readonly DateTime Since = new(2026, 10, 31, 4, 12, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstPausedResponse_Sets() =>
        Assert.Equal(SyncService.HistoryPauseChange.Set, SyncService.DecideHistoryPause("", null, new HistoryPauseNotice("iot", Since)));

    [Fact]
    public void RepeatedIdenticalPausedResponse_ChangesNothing() =>
        Assert.Equal(SyncService.HistoryPauseChange.None, SyncService.DecideHistoryPause("iot", Since, new HistoryPauseNotice("iot", Since)));

    [Fact]
    public void RepeatedPausedResponse_WithUnknownSince_ChangesNothing() =>
        Assert.Equal(SyncService.HistoryPauseChange.None, SyncService.DecideHistoryPause("unknown", null, new HistoryPauseNotice("unknown", null)));

    [Fact]
    public void ReasonOrSinceChange_UpdatesTheState()
    {
        Assert.Equal(SyncService.HistoryPauseChange.Set, SyncService.DecideHistoryPause("iot", Since, new HistoryPauseNotice("storage", Since)));
        Assert.Equal(SyncService.HistoryPauseChange.Set, SyncService.DecideHistoryPause("iot", Since, new HistoryPauseNotice("iot", Since.AddDays(1))));
    }

    [Fact]
    public void PlainResponseWhilePaused_Clears() =>
        Assert.Equal(SyncService.HistoryPauseChange.Clear, SyncService.DecideHistoryPause("storage", Since, null));

    [Fact]
    public void PlainResponseWhileRecording_ChangesNothing()
    {
        Assert.Equal(SyncService.HistoryPauseChange.None, SyncService.DecideHistoryPause("", null, null));
        Assert.Equal(SyncService.HistoryPauseChange.None, SyncService.DecideHistoryPause(null, null, null));
    }
}

// Drives the real sync step against the suite's SQLite DB and a scripted cloud. [Collection("EdgeApi")]
// (DisableParallelization) because it claims the global telemetry batch and writes the single
// DeviceConfig row, both shared with other test classes.
[Collection("EdgeApi")]
public class SyncServiceHistoryPauseTests
{
    private const string Paused =
        "{\"accepted\":0,\"rejected\":0,\"errors\":[],\"quota_exceeded\":{\"quota\":\"iot\",\"history\":\"paused\",\"since\":\"2026-10-31T04:12:00.000Z\"}}";
    private const string Normal = "{\"accepted\":2,\"rejected\":0,\"errors\":[]}";
    private static readonly DateTime Since = new(2026, 10, 31, 4, 12, 0, DateTimeKind.Utc);
    private static readonly DateTime T0 = DateTime.UtcNow.AddHours(-1);

    private static async Task<(QueueStorageService Storage, DeviceConfig Config, string Ds)> Arrange()
    {
        var storage = new QueueStorageService();
        await storage.InitializeAsync();
        if (await storage.GetDeviceConfigAsync() == null)
        {
            await storage.SaveDeviceConfigAsync(new DeviceConfig { Id = Guid.NewGuid().ToString(), ApiKey = "test-key", CloudStatus = "Connected" });
        }
        await storage.SetHistoryPauseAsync(string.Empty, null);
        var config = (await storage.GetDeviceConfigAsync())!;
        config.ApiKey = "test-key"; // used only for this scripted request; not saved
        // Recent rows (the queue's age TTL purges old ones on every write). The EdgeApi collection
        // runs alone, so these are the batch the step claims.
        var ds = "hp-ds-" + Guid.NewGuid().ToString("N");
        var t0 = T0;
        await storage.EnqueueTelemetryAsync(ds, t0, "kw", 1.5, "Good");
        await storage.EnqueueTelemetryAsync(ds, t0.AddSeconds(1), "kw", 1.6, "Good");
        return (storage, config, ds);
    }

    private static SyncService Service(QueueStorageService storage, HttpStatusCode status, string body) =>
        new(NullLogger<SyncService>.Instance, storage, new CloudClient(NullLogger<CloudClient>.Instance, new ScriptedHandler(status, body)));

    private static async Task<List<QueueTelemetry>> Rows(string ds)
    {
        using var db = new QueueDbContext();
        return await db.QueueTelemetry.AsNoTracking().Where(x => x.DataSourceId == ds).ToListAsync();
    }

    private static async Task Cleanup(QueueStorageService storage, string ds)
    {
        using (var db = new QueueDbContext())
        {
            await db.QueueTelemetry.Where(x => x.DataSourceId == ds).ExecuteDeleteAsync();
        }
        await storage.SetHistoryPauseAsync(string.Empty, null);
    }

    [Fact]
    public async Task PausedResponse_DeliversTheBatch_SetsAStableStatus_AndAPlainResponseClearsIt()
    {
        var (storage, config, ds) = await Arrange();
        try
        {
            // 1. Paused 202: the batch is delivered (deleted locally, no retry, no backlog kept).
            var step = await Service(storage, HttpStatusCode.Accepted, Paused).SyncTelemetryOnceAsync(config, "http://localhost:3000");
            Assert.True(step.ProcessedAny);
            Assert.False(step.BackOff);
            Assert.Empty(await Rows(ds));
            var stored = (await storage.GetDeviceConfigAsync())!;
            Assert.Equal("iot", stored.HistoryPausedQuota);
            Assert.Equal(Since, DateTime.SpecifyKind(stored.HistoryPausedSince!.Value, DateTimeKind.Utc));

            // 2. The next batch, still paused: delivered again, state unchanged (no churn).
            await storage.EnqueueTelemetryAsync(ds, T0.AddSeconds(5), "kw", 1.7, "Good");
            step = await Service(storage, HttpStatusCode.Accepted, Paused).SyncTelemetryOnceAsync(config, "http://localhost:3000");
            Assert.False(step.BackOff);
            Assert.Empty(await Rows(ds));
            Assert.Equal("iot", (await storage.GetDeviceConfigAsync())!.HistoryPausedQuota);

            // 3. A failed request (503) is not recovery: the batch stays queued for retry and the
            //    paused status stays.
            await storage.EnqueueTelemetryAsync(ds, T0.AddSeconds(6), "kw", 1.8, "Good");
            step = await Service(storage, HttpStatusCode.ServiceUnavailable, "{}").SyncTelemetryOnceAsync(config, "http://localhost:3000");
            Assert.True(step.BackOff);
            var kept = Assert.Single(await Rows(ds));
            Assert.Equal(1, kept.RetryCount);
            Assert.False(kept.IsSending);
            Assert.Equal("iot", (await storage.GetDeviceConfigAsync())!.HistoryPausedQuota);

            // 4. A network failure is not recovery either.
            step = await new SyncService(NullLogger<SyncService>.Instance, storage,
                    new CloudClient(NullLogger<CloudClient>.Instance, new ThrowingHandler()))
                .SyncTelemetryOnceAsync(config, "http://localhost:3000");
            Assert.True(step.BackOff);
            Assert.Equal(2, Assert.Single(await Rows(ds)).RetryCount);
            Assert.Equal("iot", (await storage.GetDeviceConfigAsync())!.HistoryPausedQuota);

            // 5. A plain 202 (history recording again) delivers the retried batch and clears it.
            step = await Service(storage, HttpStatusCode.Accepted, Normal).SyncTelemetryOnceAsync(config, "http://localhost:3000");
            Assert.False(step.BackOff);
            Assert.Empty(await Rows(ds));
            var cleared = (await storage.GetDeviceConfigAsync())!;
            Assert.Equal(string.Empty, cleared.HistoryPausedQuota);
            Assert.Null(cleared.HistoryPausedSince);
        }
        finally
        {
            await Cleanup(storage, ds);
        }
    }

    [Fact]
    public async Task SavingAnOlderConfigCopy_DoesNotBringAClearedPauseBack()
    {
        var (storage, _, ds) = await Arrange();
        try
        {
            await storage.SetHistoryPauseAsync("storage", Since);
            var staleCopy = (await storage.GetDeviceConfigAsync())!; // carries the paused state
            await storage.SetHistoryPauseAsync(string.Empty, null);   // recovery
            await storage.SaveDeviceConfigAsync(staleCopy);           // e.g. the provisioning service saving its copy
            Assert.Equal(string.Empty, (await storage.GetDeviceConfigAsync())!.HistoryPausedQuota);
        }
        finally
        {
            await Cleanup(storage, ds);
        }
    }
}

/// <summary>Simulates a network failure (no HTTP response at all).</summary>
internal sealed class ThrowingHandler : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken ct) =>
        throw new System.Net.Http.HttpRequestException("network unreachable");
}

