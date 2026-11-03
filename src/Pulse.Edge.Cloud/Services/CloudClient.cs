using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Pulse.Edge.Cloud.Services;

public enum TelemetrySyncResult
{
    Success,
    TransientError,
    ClientError,
    Unauthorized
}

/// <summary>
/// The cloud's "history paused" notice on a 202 from POST /edge/telemetry (edge contract §5.4):
/// the organization used its monthly IoT allowance (<c>iot</c>) or its storage is full
/// (<c>storage</c>). The batch counts as delivered, but the cloud kept no history for it; only
/// each metric's latest value (live readings, alarms). <see cref="Quota"/> is <c>iot</c>,
/// <c>storage</c>, or <c>unknown</c> for a value this edge does not know yet. <see cref="SinceUtc"/>
/// is null when the server's timestamp is missing or unreadable.
/// </summary>
public sealed record HistoryPauseNotice(string Quota, DateTime? SinceUtc);

/// <summary>A telemetry upload's result plus the history-paused notice, if the 202 carried one.</summary>
public sealed record TelemetrySendOutcome(TelemetrySyncResult Result, HistoryPauseNotice? HistoryPause = null);

public enum OeeSyncResult
{
    Success,
    TransientError,   // 503 / network / timeout / unexpected status → keep batch, backoff, re-send
    EnvelopeError,    // 400 on the envelope → edge bug; never retry unchanged
    Unauthorized,     // 401 → key revoked; re-enter claim flow
    NotPaired         // 409 → device not paired to a site; re-enter pairing
}

public class CloudClient
{
    private readonly ILogger<CloudClient> _logger;
    private readonly HttpClient _httpClient;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public record OrganizationDto(string Id, string Name);
    public record SiteDto(string Id, string Name);

    public record RegisterRequest(string DeviceId, string Hostname, string AgentVersion, string ClaimSecretHash, string PairingTokenHash);
    public record RegisterResponse(string EdgeId, string Status, string? ShortCode, string? PairingExpiresAt, string? PairingBaseUrl);
    public record RegisterResult(string EdgeId, string Status, string? ShortCode, DateTime? PairingExpiresAt, string? PairingBaseUrl);
    
    public record ClaimRequest(string DeviceId, string ClaimSecret);
    public class ClaimResponse
    {
        public string Status { get; set; } = string.Empty;
        public string? ApiKey { get; set; }
        public OrganizationDto? Organization { get; set; }
        public SiteDto? Site { get; set; }
        public string? Error { get; set; }
    }
    public record ClaimResult(
        string Status, 
        string? ApiKey, 
        string? OrgId, 
        string? OrgName, 
        string? SiteId, 
        string? SiteName);

    public record HeartbeatRequest(string AgentVersion);
    public record HeartbeatResponse(bool Ok, string Status);

    public record CloudMetricMetadataDto(
        string AdapterId,
        string? MqttDeviceId,
        string Address,
        string DataType,
        int ScanIntervalMs,
        double ScaleFactor,
        double Offset,
        string ByteOrder,
        string MqttParseMode,
        string? MqttJsonPath);

    public record CloudMetricDto(string Name, string Protocol, CloudMetricMetadataDto Metadata);

    public record CloudDataSourceDto(string Id, string ExternalId, string Name, CloudMetricDto[] Metrics);
    public record ConfigResponse(string EdgeId, string Status, OrganizationDto? Organization, SiteDto? Site, List<CloudDataSourceDto>? DataSources);

    public record DeclareDataSourceRequest(string ExternalId, string Name, CloudMetricDto[] Metrics);

    public record ConfigResult(
        bool Success, 
        HttpStatusCode StatusCode, 
        string OrgId, 
        string OrgName, 
        string SiteId, 
        string SiteName, 
        List<CloudDataSourceDto> DataSources);

    public CloudClient(ILogger<CloudClient> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
    }

    /// <summary>Test seam: inject a scripted HttpMessageHandler. Production uses the default ctor.</summary>
    public CloudClient(ILogger<CloudClient> logger, HttpMessageHandler handler)
    {
        _logger = logger;
        _httpClient = new HttpClient(handler);
    }

    private Uri GetUri(string baseUrl, string path)
    {
        // Ensure baseUrl is parsed correctly, fallback if empty
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "http://localhost:3000";
        }
        if (!baseUrl.StartsWith("http://") && !baseUrl.StartsWith("https://"))
        {
            baseUrl = "http://" + baseUrl;
        }
        baseUrl = baseUrl.TrimEnd('/');
        path = path.TrimStart('/');
        if (!IsAcceptableCloudEndpoint(baseUrl))
            throw new InvalidOperationException(
                $"Refusing to contact PULSE Cloud over an insecure endpoint '{baseUrl}'. Use https:// (loopback may use http).");
        return new Uri($"{baseUrl}/{path}");
    }

    /// <summary>
    /// True iff the endpoint is safe to use for a cloud connection: scheme is <c>https</c>,
    /// or scheme is <c>http</c> and the host is loopback (localhost/127.0.0.1/::1), which keeps
    /// local dev against a local cloud instance working without weakening the production check.
    /// </summary>
    public static bool IsAcceptableCloudEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        if (uri.Scheme == Uri.UriSchemeHttp) return uri.IsLoopback;
        return false;
    }

    private static string ComputeSha256Hash(string? input)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // Real call: POST /edge/register
    public async Task<RegisterResult?> RegisterDeviceAsync(
        string baseUrl, 
        string deviceId, 
        string hostname, 
        string agentVersion, 
        string claimSecret, 
        string pairingToken)
    {
        _logger.LogInformation("Sending device registration request to PULSE Cloud ({BaseUrl})...", baseUrl);
        
        try
        {
            string claimSecretHash = ComputeSha256Hash(claimSecret);
            string pairingTokenHash = ComputeSha256Hash(pairingToken);

            var req = new RegisterRequest(deviceId, hostname, agentVersion, claimSecretHash, pairingTokenHash);
            var response = await _httpClient.PostAsJsonAsync(GetUri(baseUrl, "/edge/register"), req, JsonOptions);
            
            if (response.IsSuccessStatusCode)
            {
                var res = await response.Content.ReadFromJsonAsync<RegisterResponse>(JsonOptions);
                if (res != null)
                {
                    _logger.LogInformation("Registration request acknowledged. Cloud EdgeId: {EdgeId}, Status: {Status}, ShortCode: {ShortCode}", res.EdgeId, res.Status, res.ShortCode);
                    
                    DateTime? parsedExpiry = null;
                    if (!string.IsNullOrEmpty(res.PairingExpiresAt))
                    {
                        if (DateTime.TryParse(res.PairingExpiresAt, out var dt))
                        {
                            parsedExpiry = dt.ToUniversalTime();
                        }
                    }

                    return new RegisterResult(res.EdgeId, res.Status, res.ShortCode, parsedExpiry, res.PairingBaseUrl);
                }
            }
            
            var err = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to register device: {StatusCode} - {Error}", response.StatusCode, err);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during device registration to {BaseUrl}", baseUrl);
        }
        
        return null;
    }

    // Real call: POST /edge/claim
    public async Task<ClaimResult> ClaimKeyAsync(string baseUrl, string deviceId, string claimSecret)
    {
        _logger.LogInformation("Claiming API Key from PULSE Cloud ({BaseUrl}) for device {DeviceId}...", baseUrl, deviceId);
        
        try
        {
            var req = new ClaimRequest(deviceId, claimSecret);
            var response = await _httpClient.PostAsJsonAsync(GetUri(baseUrl, "/edge/claim"), req, JsonOptions);
            
            if (response.IsSuccessStatusCode)
            {
                var res = await response.Content.ReadFromJsonAsync<ClaimResponse>(JsonOptions);
                if (res != null)
                {
                    _logger.LogInformation("Claim request successful. Status: {Status}", res.Status);
                    return new ClaimResult(
                        res.Status, 
                        res.ApiKey, 
                        res.Organization?.Id, 
                        res.Organization?.Name, 
                        res.Site?.Id, 
                        res.Site?.Name);
                }
            }
            else
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("Claim request failed: 401 Unauthorized (invalid device or secret)");
                    return new ClaimResult("unauthorized", null, null, null, null, null);
                }
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning("Claim request failed: 403 Forbidden (device revoked)");
                    return new ClaimResult("revoked", null, null, null, null, null);
                }
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    _logger.LogWarning("Claim request failed: 409 Conflict (already claimed)");
                    return new ClaimResult("conflict", null, null, null, null, null);
                }

                var err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Claim request failed with status {StatusCode}: {Error}", response.StatusCode, err);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during claim request to {BaseUrl}", baseUrl);
        }
        
        return new ClaimResult("pending", null, null, null, null, null);
    }

    // Real call: GET /edge/config
    public async Task<ConfigResult> GetConfigAsync(string baseUrl, string apiKey)
    {
        _logger.LogInformation("Pulling device configuration from PULSE Cloud ({BaseUrl})...", baseUrl);
        
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, GetUri(baseUrl, "/edge/config"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            
            var response = await _httpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                var res = await response.Content.ReadFromJsonAsync<ConfigResponse>(JsonOptions);
                if (res != null)
                {
                    _logger.LogInformation("Successfully retrieved configuration. Site: {SiteName}", res.Site?.Name ?? "None");
                    return new ConfigResult(
                        Success: true,
                        StatusCode: response.StatusCode,
                        OrgId: res.Organization?.Id ?? "",
                        OrgName: res.Organization?.Name ?? "",
                        SiteId: res.Site?.Id ?? "",
                        SiteName: res.Site?.Name ?? "",
                        DataSources: res.DataSources ?? new List<CloudDataSourceDto>()
                    );
                }
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to fetch configuration: {StatusCode} - {Error}", response.StatusCode, err);
            }
            
            return new ConfigResult(false, response.StatusCode, "", "", "", "", new List<CloudDataSourceDto>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during config pull from {BaseUrl}", baseUrl);
            return new ConfigResult(false, HttpStatusCode.InternalServerError, "", "", "", "", new List<CloudDataSourceDto>());
        }
    }

    // Real call: POST /edge/data-sources
    public async Task<bool> UpsertDataSourcesAsync(string baseUrl, string apiKey, List<DeclareDataSourceRequest> dataSources)
    {
        _logger.LogInformation("Upserting {Count} data sources with PULSE Cloud ({BaseUrl})...", dataSources.Count, baseUrl);
        
        try
        {
            var options = JsonOptions;
            var request = new HttpRequestMessage(HttpMethod.Post, GetUri(baseUrl, "/edge/data-sources"))
            {
                Content = JsonContent.Create(dataSources, options: options)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            
            var response = await _httpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully declared data sources with cloud.");
                return true;
            }
            
            var err = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to declare data sources: {StatusCode} - {Error}", response.StatusCode, err);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during data source declaration to {BaseUrl}", baseUrl);
        }
        
        return false;
    }

    // Real call: POST /edge/heartbeat
    public async Task<(bool Success, string Status)> SendHeartbeatAsync(string baseUrl, string apiKey, string version)
    {
        _logger.LogInformation("Reporting heartbeat to PULSE Cloud ({BaseUrl})...", baseUrl);
        
        try
        {
            var req = new HeartbeatRequest(version);
            var request = new HttpRequestMessage(HttpMethod.Post, GetUri(baseUrl, "/edge/heartbeat"))
            {
                Content = JsonContent.Create(req, options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            
            var response = await _httpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                var res = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(JsonOptions);
                if (res != null)
                {
                    _logger.LogDebug("Heartbeat successful. Status: {Status}", res.Status);
                    return (true, res.Status);
                }
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Heartbeat rejected: {StatusCode} - {Error}", response.StatusCode, err);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return (false, "revoked");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during heartbeat send to {BaseUrl}", baseUrl);
        }
        
        return (false, "unknown");
    }

    public class TelemetryFrame
    {
        public string DataSource { get; set; } = string.Empty;
        public string? Ts { get; set; }
        public JsonObject? Metrics { get; set; }
        public JsonObject? Qualities { get; set; }
    }

    public class TelemetryResponseError
    {
        public int Index { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public class TelemetryResponse
    {
        public int Accepted { get; set; }
        public int Rejected { get; set; }
        public List<TelemetryResponseError>? Errors { get; set; }
    }

    /// <summary>
    /// Sends a batch of telemetry frames to the cloud (POST /edge/telemetry).
    /// </summary>
    public async Task<TelemetrySyncResult> SendTelemetryBatchAsync(string baseUrl, string deviceId, string apiKey, System.Collections.Generic.List<Pulse.Edge.Storage.Models.QueueTelemetry> batch)
        => (await SendTelemetryBatchWithOutcomeAsync(baseUrl, deviceId, apiKey, batch)).Result;

    /// <summary>
    /// Reads a 202 body from POST /edge/telemetry. Never throws: a 202 means the cloud took the
    /// batch, so an unreadable body or a malformed <c>quota_exceeded</c> must not turn that
    /// success into a retry (a retry is dropped the same way, so it would loop forever).
    /// Unknown fields are ignored. Returns a null response when the counts can't be read.
    /// </summary>
    public static (TelemetryResponse? Response, HistoryPauseNotice? HistoryPause) ParseTelemetryAccepted(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        JsonObject? root;
        try { root = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return (null, null); }
        if (root == null) return (null, null);

        TelemetryResponse? response = null;
        try { response = root.Deserialize<TelemetryResponse>(JsonOptions); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException) { response = null; }

        return (response, ParseHistoryPause(root["quota_exceeded"]));
    }

    /// <summary>The notice only when <c>history</c> is exactly <c>paused</c>; anything else is not one.</summary>
    private static HistoryPauseNotice? ParseHistoryPause(JsonNode? node)
    {
        if (node is not JsonObject q) return null;
        if (!TryGetString(q["history"], out var history) || history != "paused") return null;
        var quota = TryGetString(q["quota"], out var qv) && (qv == "iot" || qv == "storage") ? qv! : "unknown";
        DateTime? since = null;
        if (TryGetString(q["since"], out var sv)
            && DateTimeOffset.TryParse(sv, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var dto))
        {
            since = dto.UtcDateTime;
        }
        return new HistoryPauseNotice(quota, since);
    }

    private static bool TryGetString(JsonNode? node, out string? value)
    {
        value = null;
        return node is JsonValue v && v.TryGetValue(out value);
    }

    /// <summary>
    /// Sends a batch of telemetry frames to the cloud (POST /edge/telemetry) and also returns the
    /// history-paused notice a 202 may carry (contract §5.4).
    /// </summary>
    public async Task<TelemetrySendOutcome> SendTelemetryBatchWithOutcomeAsync(string baseUrl, string deviceId, string apiKey, System.Collections.Generic.List<Pulse.Edge.Storage.Models.QueueTelemetry> batch)
    {
        _logger.LogInformation("[Cloud Sync] Uploading {Count} telemetry frame(s) to PULSE Cloud (POST /edge/telemetry)...", batch.Count);

        var payload = new List<TelemetryFrame>();
        foreach (var t in batch)
        {
            JsonObject? metricsObj = null;
            if (!string.IsNullOrEmpty(t.MetricsJson))
            {
                try
                 {
                     metricsObj = JsonNode.Parse(t.MetricsJson)?.AsObject();
                 }
                 catch (Exception ex)
                 {
                     _logger.LogWarning(ex, "Failed to parse MetricsJson for frame. ID: {Id}, Data Source: {DataSourceId}", t.Id, t.DataSourceId);
                 }
            }

            JsonObject? qualitiesObj = null;
            if (!string.IsNullOrEmpty(t.QualitiesJson))
            {
                try
                {
                    qualitiesObj = JsonNode.Parse(t.QualitiesJson)?.AsObject();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse QualitiesJson for frame. ID: {Id}, Data Source: {DataSourceId}", t.Id, t.DataSourceId);
                }
            }

            var tsUtc = DateTime.SpecifyKind(t.Timestamp, DateTimeKind.Utc);
            var tsStr = tsUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

            payload.Add(new TelemetryFrame
            {
                DataSource = t.DataSourceId,
                Ts = tsStr,
                Metrics = metricsObj,
                Qualities = qualitiesObj
            });
        }

        try
        {
            var options = JsonOptions;

            var request = new HttpRequestMessage(HttpMethod.Post, GetUri(baseUrl, "/edge/telemetry"))
            {
                Content = JsonContent.Create(payload, options: options)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Accepted) // 202 Accepted
            {
                var body = await response.Content.ReadAsStringAsync();
                var (res, historyPause) = ParseTelemetryAccepted(body);
                if (res == null)
                {
                    // Still delivered: the server answered 202. Only the counts are lost.
                    _logger.LogWarning("[Cloud Sync] Telemetry batch accepted (202) but the response body could not be read; treating it as delivered.");
                }
                if (historyPause != null)
                {
                    // Expected while the org is over its IoT allowance or storage; SyncService
                    // announces the state change once, so this stays at Information.
                    _logger.LogInformation("[Cloud Sync] PULSE Cloud reports history paused (quota: {Quota}, since: {Since}); the batch is delivered and latest values stay live.",
                        historyPause.Quota, historyPause.SinceUtc?.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
                }
                if (res != null)
                {
                    _logger.LogInformation("[Cloud Sync] Telemetry batch processed. Accepted: {Accepted}, Rejected: {Rejected}", res.Accepted, res.Rejected);
                    if (res.Rejected > 0 && res.Errors != null)
                    {
                        foreach (var err in res.Errors)
                        {
                            string dsId = "Unknown";
                            if (err.Index >= 0 && err.Index < batch.Count)
                            {
                                dsId = batch[err.Index].DataSourceId;
                            }
                            _logger.LogWarning("[Cloud Sync] Frame rejected at index {Index} (DataSource: {DataSource}): {Reason}", 
                                err.Index, dsId, err.Reason);
                        }
                    }
                }
                return new TelemetrySendOutcome(TelemetrySyncResult.Success, historyPause);
            }
            else if (response.StatusCode == HttpStatusCode.BadRequest) // 400 Bad Request
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogError("[Cloud Sync] Telemetry batch rejected with 400 Bad Request. Error: {Error}", err);
                return new TelemetrySendOutcome(TelemetrySyncResult.ClientError);
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized) // 401 Unauthorized
            {
                _logger.LogError("[Cloud Sync] Telemetry batch rejected with 401 Unauthorized. API Key may be invalid/revoked.");
                return new TelemetrySendOutcome(TelemetrySyncResult.Unauthorized);
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("[Cloud Sync] Telemetry batch failed with status {StatusCode}. Error: {Error}", response.StatusCode, err);
                return new TelemetrySendOutcome(TelemetrySyncResult.TransientError);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during telemetry transmission to {BaseUrl}", baseUrl);
            return new TelemetrySendOutcome(TelemetrySyncResult.TransientError);
        }
    }

    // ── OEE ingestion (know-how/cloud_oee_ingestion.md) ──────────────────────

    public record OeeChannelDeclarationDto(string ExternalId, string Name, string[] Capabilities);

    public class OeeCountersDto
    {
        public long Good { get; set; }
        public long? Reject { get; set; }
    }

    public class OeeEventMessageDto
    {
        public string Type { get; set; } = string.Empty;     // "state" | "sync"
        public string Channel { get; set; } = string.Empty;  // declared externalId
        public long Seq { get; set; }
        public string? Ts { get; set; }                      // ISO-8601 UTC with ms
        public string State { get; set; } = string.Empty;    // "running" | "stopped" | "fault"
        public string? Code { get; set; }
        public OeeCountersDto? Counters { get; set; }
    }

    public class OeeEventsResponseError
    {
        public int Index { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public class OeeEventsResponse
    {
        public int Accepted { get; set; }
        public int Rejected { get; set; }
        public int Duplicates { get; set; }
        public List<OeeEventsResponseError>? Errors { get; set; }
    }

    /// <summary>
    /// Declares OEE channels (POST /edge/oee/channels). Idempotent on the cloud;
    /// 201 = success. Whole-batch validation: any invalid item fails the entire call with 400.
    /// </summary>
    public async Task<OeeSyncResult> DeclareOeeChannelsAsync(
        string baseUrl, string apiKey, List<OeeChannelDeclarationDto> channels)
    {
        _logger.LogInformation("[OEE Sync] Declaring {Count} OEE channel(s) (POST /edge/oee/channels)...", channels.Count);
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, GetUri(baseUrl, "/edge/oee/channels"))
            {
                Content = JsonContent.Create(channels, options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.SendAsync(request);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Created:
                    return OeeSyncResult.Success;
                case HttpStatusCode.BadRequest:
                    _logger.LogError("[OEE Sync] Channel declaration rejected with 400: {Error}", await response.Content.ReadAsStringAsync());
                    return OeeSyncResult.EnvelopeError;
                case HttpStatusCode.Unauthorized:
                    _logger.LogError("[OEE Sync] Channel declaration rejected with 401. API key may be revoked.");
                    return OeeSyncResult.Unauthorized;
                case HttpStatusCode.Conflict:
                    _logger.LogError("[OEE Sync] Channel declaration rejected with 409. Device is not paired to a site.");
                    return OeeSyncResult.NotPaired;
                default:
                    _logger.LogWarning("[OEE Sync] Channel declaration failed with status {Status}.", response.StatusCode);
                    return OeeSyncResult.TransientError;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during OEE channel declaration to {BaseUrl}", baseUrl);
            return OeeSyncResult.TransientError;
        }
    }

    /// <summary>
    /// Uploads an OEE event batch (POST /edge/oee/events). 202 = per-message result in the
    /// response body; duplicates are acknowledged like any accepted message (dedup on (channel, seq)).
    /// </summary>
    public async Task<(OeeSyncResult Result, OeeEventsResponse? Response)> SendOeeEventsBatchAsync(
        string baseUrl, string apiKey, List<OeeEventMessageDto> messages)
    {
        _logger.LogInformation("[OEE Sync] Uploading {Count} OEE event(s) (POST /edge/oee/events)...", messages.Count);
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, GetUri(baseUrl, "/edge/oee/events"))
            {
                Content = JsonContent.Create(messages, options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.SendAsync(request);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Accepted:
                    var body = await response.Content.ReadFromJsonAsync<OeeEventsResponse>(JsonOptions);
                    _logger.LogInformation("[OEE Sync] Batch processed. Accepted: {A}, Rejected: {R}, Duplicates: {D}",
                        body?.Accepted, body?.Rejected, body?.Duplicates);
                    return (OeeSyncResult.Success, body);
                case HttpStatusCode.BadRequest:
                    _logger.LogError("[OEE Sync] Event envelope rejected with 400: {Error}", await response.Content.ReadAsStringAsync());
                    return (OeeSyncResult.EnvelopeError, null);
                case HttpStatusCode.Unauthorized:
                    _logger.LogError("[OEE Sync] Event batch rejected with 401. API key may be revoked.");
                    return (OeeSyncResult.Unauthorized, null);
                case HttpStatusCode.Conflict:
                    _logger.LogError("[OEE Sync] Event batch rejected with 409. Device is not paired to a site.");
                    return (OeeSyncResult.NotPaired, null);
                default:
                    _logger.LogWarning("[OEE Sync] Event batch failed with status {Status}.", response.StatusCode);
                    return (OeeSyncResult.TransientError, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during OEE event transmission to {BaseUrl}", baseUrl);
            return (OeeSyncResult.TransientError, null);
        }
    }
}
