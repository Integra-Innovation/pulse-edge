using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Edge.Storage.Models;

namespace Pulse.Edge.Storage.Services;

public class QueueStorageService
{
    // ─────────────────────────────────────────────────────────────────────────
    // Buffer limits
    // ─────────────────────────────────────────────────────────────────────────
    private const int MaxBufferRows = 10_000;
    private static readonly TimeSpan MaxBufferAge = TimeSpan.FromDays(7);
    private static long _lastBufferLossCriticalTicks;

    // Serializes InitializeAsync's schema work (CREATE/ALTER/DROP TABLE, PRAGMA) across
    // concurrent callers within this process. On the real device this runs once at startup
    // with no contention, but the test suite constructs a fresh QueueStorageService and calls
    // InitializeAsync() from nearly every test class's setup — under default-parallel xunit
    // execution, dozens of instances can run this against the SAME shared SQLite file at the
    // same moment. Ordinary row-level lock contention is retried by SQLite's own busy_timeout,
    // but concurrent schema-modifying statements (ALTER TABLE / CREATE TABLE / DROP TABLE)
    // racing each other — or racing a query on another connection — can surface as an
    // unhandled SQLite exception instead of a retried wait. Serializing the whole method
    // in-process removes that surface; it's cheap since InitializeAsync is fast and idempotent.
    private static readonly SemaphoreSlim InitializeLock = new(1, 1);

    // Initializes the SQLite database, creating it and its tables if they do not exist
    public async Task InitializeAsync()
    {
        await InitializeLock.WaitAsync();
        try
        {
            await InitializeCoreAsync();
        }
        finally
        {
            InitializeLock.Release();
        }
    }

    // Password-recovery columns (spec 2026-10-06-edge-password-recovery-design §4). Idempotent:
    // ADD COLUMN throws when the column exists, which we ignore like the other upgrades here.
    // Users from before this upgrade get a random stamp, which signs out their old cookies once.
    public static async Task UpgradeLocalUsersSchemaAsync(QueueDbContext db)
    {
        string[] columns =
        [
            "ALTER TABLE LocalUsers ADD COLUMN SecurityStamp TEXT NOT NULL DEFAULT '';",
            "ALTER TABLE LocalUsers ADD COLUMN RecoveryCodeHash TEXT NOT NULL DEFAULT '';",
            "ALTER TABLE LocalUsers ADD COLUMN RecoveryCodeCreatedAtUtc TEXT;",
        ];
        foreach (var ddl in columns)
        {
            try { await db.Database.ExecuteSqlRawAsync(ddl); }
            catch { /* Column already exists */ }
        }
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE LocalUsers SET SecurityStamp = lower(hex(randomblob(16))) WHERE SecurityStamp = '';");
    }

    private async Task InitializeCoreAsync()
    {
        using var db = new QueueDbContext();
        await db.Database.EnsureCreatedAsync();

        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS LocalUsers (
                Id TEXT PRIMARY KEY,
                Username TEXT NOT NULL,
                NormalizedUsername TEXT NOT NULL UNIQUE,
                PasswordHash TEXT NOT NULL,
                Role TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                FailedLoginCount INTEGER NOT NULL DEFAULT 0,
                LockoutEndUtc TEXT,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                LastLoginAtUtc TEXT,
                SecurityStamp TEXT NOT NULL DEFAULT '',
                RecoveryCodeHash TEXT NOT NULL DEFAULT '',
                RecoveryCodeCreatedAtUtc TEXT
            );
            CREATE TABLE IF NOT EXISTS AuditEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT NOT NULL,
                EventType TEXT NOT NULL,
                ActorUsername TEXT NOT NULL DEFAULT '',
                Target TEXT NOT NULL DEFAULT '',
                RemoteIp TEXT NOT NULL DEFAULT '',
                Succeeded INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS DiagnosticEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT NOT NULL,
                Level TEXT NOT NULL,
                Category TEXT NOT NULL DEFAULT '',
                EventCode TEXT NOT NULL DEFAULT '',
                Message TEXT NOT NULL,
                Details TEXT NOT NULL DEFAULT '',
                AdapterId TEXT NOT NULL DEFAULT '',
                DataPointId TEXT NOT NULL DEFAULT '',
                CorrelationId TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS IX_DiagnosticEvents_TimestampUtc ON DiagnosticEvents(TimestampUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_DiagnosticEvents_Level ON DiagnosticEvents(Level);
            CREATE TABLE IF NOT EXISTS DiagnosticCaptureConfigs (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                IsEnabled INTEGER NOT NULL DEFAULT 0,
                AdapterId TEXT NOT NULL DEFAULT '',
                StartedAtUtc TEXT,
                ExpiresAtUtc TEXT,
                HasRotated INTEGER NOT NULL DEFAULT 0
            );
            INSERT OR IGNORE INTO DiagnosticCaptureConfigs (Id) VALUES (1);
        ");

        await UpgradeLocalUsersSchemaAsync(db);

        // Create DeviceConfigs table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS DeviceConfigs (
                    Id TEXT PRIMARY KEY,
                    CloudEdgeId TEXT NOT NULL DEFAULT '',
                    ClaimSecret TEXT NOT NULL DEFAULT '',
                    PairingToken TEXT NOT NULL DEFAULT '',
                    PairingShortCode TEXT NOT NULL DEFAULT '',
                    PairingExpiresAt TEXT,
                    PairingBaseUrl TEXT NOT NULL DEFAULT '',
                    SerialNumber TEXT NOT NULL DEFAULT '',
                    OrganizationId TEXT NOT NULL DEFAULT '',
                    OrganizationName TEXT NOT NULL DEFAULT '',
                    SiteId TEXT NOT NULL DEFAULT '',
                    SiteName TEXT NOT NULL DEFAULT '',
                    ApiKey TEXT NOT NULL DEFAULT '',
                    CloudEndpoint TEXT NOT NULL DEFAULT 'http://localhost:3000',
                    Version TEXT NOT NULL DEFAULT '1.0.0',
                    IsSyncEnabled INTEGER NOT NULL DEFAULT 1,
                    CloudStatus TEXT NOT NULL DEFAULT 'PendingApproval',
                    HistoryPausedQuota TEXT NOT NULL DEFAULT '',
                    HistoryPausedSince TEXT
                );
            ");
        }
        catch {}

        // The legacy event queue was removed (replaced by the OEE outbox — see
        // docs/superpowers/specs/2026-07-18-edge-oee-ingestion-design.md §6). The table
        // was provably always empty (no producer ever existed), so dropping it is safe.
        try
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS QueueEvents;");
        }
        catch {}

        // Enable Write-Ahead Logging (WAL) for better concurrent read/write throughput
        try
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        }
        catch {}
        
        // Auto-migrate schema: add IsSyncEnabled column to existing databases if it's missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceConfigs ADD COLUMN IsSyncEnabled INTEGER NOT NULL DEFAULT 1;");
        }
        catch
        {
            // Column already exists, ignore exception
        }

        // Auto-migrate schema: add CloudEdgeId column to existing databases if it's missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceConfigs ADD COLUMN CloudEdgeId TEXT NOT NULL DEFAULT '';");
        }
        catch
        {
            // Column already exists, ignore exception
        }

        // Auto-migrate schema: add CloudEndpoint column to existing databases if it's missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceConfigs ADD COLUMN CloudEndpoint TEXT NOT NULL DEFAULT 'http://localhost:3000';");
        }
        catch
        {
            // Column already exists, ignore exception
        }

        // Auto-migrate schema: add CloudStatus column to existing databases if it's missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceConfigs ADD COLUMN CloudStatus TEXT NOT NULL DEFAULT 'PendingApproval';");
        }
        catch
        {
            // Column already exists, ignore exception
        }

        // Auto-migrate schema: the cloud's history-paused notice (edge contract §5.4)
        foreach (var ddl in new[]
        {
            "ALTER TABLE DeviceConfigs ADD COLUMN HistoryPausedQuota TEXT NOT NULL DEFAULT '';",
            "ALTER TABLE DeviceConfigs ADD COLUMN HistoryPausedSince TEXT;",
        })
        {
            try { await db.Database.ExecuteSqlRawAsync(ddl); }
            catch { /* Column already exists */ }
        }

        // Auto-migrate schema: add SiteName column to existing databases if it's missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceConfigs ADD COLUMN SiteName TEXT NOT NULL DEFAULT '';");
        }
        catch
        {
            // Column already exists, ignore exception
        }

        // Auto-migrate schema: add ClaimSecret column to existing databases if it's missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceConfigs ADD COLUMN ClaimSecret TEXT NOT NULL DEFAULT '';");
        }
        catch
        {
            // Column already exists, ignore exception
        }

        // Auto-migrate schema: add new pairing and organization columns to existing databases if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceConfigs ADD COLUMN PairingToken TEXT NOT NULL DEFAULT '';");
        }
        catch {}

        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceConfigs ADD COLUMN PairingShortCode TEXT NOT NULL DEFAULT '';");
        }
        catch {}

        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceConfigs ADD COLUMN PairingExpiresAt TEXT;");
        }
        catch {}

        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceConfigs ADD COLUMN PairingBaseUrl TEXT NOT NULL DEFAULT '';");
        }
        catch {}

        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceConfigs ADD COLUMN OrganizationId TEXT NOT NULL DEFAULT '';");
        }
        catch {}

        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceConfigs ADD COLUMN OrganizationName TEXT NOT NULL DEFAULT '';");
        }
        catch {}

        // Create DriverAdapters table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS DriverAdapters (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Protocol TEXT NOT NULL,
                    Host TEXT NOT NULL,
                    Port INTEGER NOT NULL,
                    ConfigJson TEXT NOT NULL,
                    IsEnabled INTEGER NOT NULL,
                    Status TEXT NOT NULL
                );
            ");
        }
        catch {}

        // Create DataSources table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS DataSources (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Type TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    IsEnabled INTEGER NOT NULL DEFAULT 1
                );
            ");
        }
        catch {}

        // Add Type and IsEnabled columns to existing DataSources table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DataSources ADD COLUMN Type TEXT NOT NULL DEFAULT 'General';");
        }
        catch {}
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DataSources ADD COLUMN IsEnabled INTEGER NOT NULL DEFAULT 1;");
        }
        catch {}

        // Create StreamTemplates table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS StreamTemplates (
                    Id TEXT PRIMARY KEY,
                    Description TEXT NOT NULL,
                    ParametersJson TEXT NOT NULL,
                    Icon TEXT NOT NULL DEFAULT 'Database'
                );
            ");
            await SeedDefaultStreamTemplatesAsync(db);
        }
        catch {}

        // Create DataPoints table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS DataPoints (
                    Id TEXT PRIMARY KEY,
                    AdapterId TEXT NOT NULL,
                    DataSourceId TEXT NOT NULL,
                    Metric TEXT NOT NULL,
                    Address TEXT NOT NULL,
                    DataType TEXT NOT NULL,
                    ScanIntervalMs INTEGER NOT NULL,
                    ScaleFactor REAL NOT NULL,
                    Offset REAL NOT NULL,
                    IsEnabled INTEGER NOT NULL,
                    ByteOrder TEXT NOT NULL DEFAULT 'ABCD',
                    Description TEXT NULL,
                    MqttParseMode TEXT NOT NULL DEFAULT 'Plaintext',
                    MqttJsonPath TEXT NULL,
                    LastValue TEXT NULL,
                    LastError TEXT NULL,
                    LastUpdated TEXT NULL,
                    LastLatencyMs REAL NULL,
                    ConsecutiveFailures INTEGER NOT NULL DEFAULT 0
                );
            ");
        }
        catch {}

        // Add Description column to existing DataPoints table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DataPoints ADD COLUMN Description TEXT NULL;");
        }
        catch {}

        // Add MqttParseMode column to existing DataPoints table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DataPoints ADD COLUMN MqttParseMode TEXT NOT NULL DEFAULT 'Plaintext';");
        }
        catch {}

        // Add MqttJsonPath column to existing DataPoints table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DataPoints ADD COLUMN MqttJsonPath TEXT NULL;");
        }
        catch {}

        // Add ByteOrder column to existing DataPoints table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DataPoints ADD COLUMN ByteOrder TEXT NOT NULL DEFAULT 'ABCD';");
        }
        catch {}

        // Add diagnostic columns to existing DataPoints table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DataPoints ADD COLUMN LastValue TEXT NULL;");
        }
        catch {}
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DataPoints ADD COLUMN LastError TEXT NULL;");
        }
        catch {}
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DataPoints ADD COLUMN LastUpdated TEXT NULL;");
        }
        catch {}
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DataPoints ADD COLUMN LastLatencyMs REAL NULL;");
        }
        catch {}
        // Add diagnostic columns to existing DataPoints table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DataPoints ADD COLUMN ConsecutiveFailures INTEGER NOT NULL DEFAULT 0;");
        }
        catch {}

        // Add MqttDeviceId column to existing DataPoints table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DataPoints ADD COLUMN MqttDeviceId TEXT NULL;");
        }
        catch {}

        // Create MqttDevices table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS MqttDevices (
                    Id TEXT PRIMARY KEY,
                    AdapterId TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    TopicSubscription TEXT NOT NULL,
                    MqttParseMode TEXT NOT NULL DEFAULT 'JSON',
                    IsEnabled INTEGER NOT NULL DEFAULT 1,
                    LwtTopic TEXT NULL,
                    LwtOnlinePayload TEXT NOT NULL DEFAULT 'Online',
                    LwtOfflinePayload TEXT NOT NULL DEFAULT 'Offline',
                    Status TEXT NOT NULL DEFAULT 'Disconnected',
                    LastError TEXT NULL,
                    LastUpdated TEXT NULL,
                    ConsecutiveFailures INTEGER NOT NULL DEFAULT 0
                );
            ");
        }
        catch {}

        // Create MqttSeenTopics table if missing
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS MqttSeenTopics (
                    Topic TEXT PRIMARY KEY,
                    Payload TEXT NOT NULL,
                    LastSeen TEXT NOT NULL
                );
            ");
        }
        catch {}

        // ── OEE tables (durable — never dropped; see docs/superpowers/specs/2026-07-18-edge-oee-ingestion-design.md) ──
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS OeeChannels (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ExternalId TEXT NOT NULL UNIQUE,
                    Name TEXT NOT NULL,
                    Enabled INTEGER NOT NULL DEFAULT 1,
                    RunDataPointId TEXT NOT NULL,
                    FaultDataPointId TEXT,
                    CodeDataPointId TEXT,
                    GoodDataPointId TEXT,
                    RejectDataPointId TEXT,
                    DebounceSeconds INTEGER NOT NULL DEFAULT 2,
                    NextSeq INTEGER NOT NULL DEFAULT 0,
                    LastState TEXT,
                    LastStateChangedAt TEXT,
                    LastCode TEXT,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS OeeOutboxMessages (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ChannelId INTEGER NOT NULL,
                    Seq INTEGER NOT NULL,
                    Type TEXT NOT NULL,
                    Ts TEXT NOT NULL,
                    State TEXT NOT NULL,
                    Code TEXT,
                    GoodCount INTEGER,
                    RejectCount INTEGER,
                    IsSending INTEGER NOT NULL DEFAULT 0,
                    RetryCount INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    UNIQUE (ChannelId, Seq)
                );
                CREATE INDEX IF NOT EXISTS idx_oee_outbox_pending
                    ON OeeOutboxMessages (IsSending, Id);
            ");
        }
        catch {}

        await EnsureTelemetrySchemaAsync(db);

        // Safety check: reset sending status for any items stuck in-flight due to an abrupt
        // shutdown/crash. This must run at most ONCE per process lifetime, not once per
        // InitializeAsync() call: it unconditionally clears every IsSending flag in both
        // QueueTelemetry and OeeOutboxMessages, which is correct exactly once at real startup
        // (nothing else in the process has touched either queue yet) but is WRONG if it runs
        // again later, since by then it would clobber rows that are legitimately in-flight
        // *right now* as part of an active, successful drain-lock cycle elsewhere in the same
        // process. In production InitializeAsync() is only ever called once anyway (Worker
        // startup, per docs/PULSE_Edge_Production_Readiness_Roadmap.md), so this guard changes
        // nothing there. In the test suite, though, ~150 test classes each construct their own
        // QueueStorageService and call InitializeAsync() from their own setup — without this
        // guard, every one of those re-runs the "abrupt shutdown" recovery logic throughout the
        // run, racing with and silently un-locking sibling tests' active drain-lock rows
        // (GetPendingTelemetryBatchAsync / OeeStorageService.GetPendingBatchAsync mark rows
        // IsSending=true to claim them; this reset would immediately hand them back out again).
        if (Interlocked.Exchange(ref _resetSendingStatusDone, 1) == 0)
        {
            await ResetSendingStatusAsync();
        }
    }

    private static int _resetSendingStatusDone;

    private static async Task SeedDefaultStreamTemplatesAsync(QueueDbContext db)
    {
        try
        {
            var defaultTemplates = new List<StreamTemplate>
            {
                new StreamTemplate
                {
                    Id = "Electrical Meter",
                    Description = "Electrical energy & power measurement stream template",
                    Icon = "Zap",
                    ParametersJson = JsonSerializer.Serialize(new List<string>
                    {
                        "voltage", "current", "power", "energy", "power_factor", "frequency"
                    })
                },
                new StreamTemplate
                {
                    Id = "Flow Meter",
                    Description = "Liquid or gas volumetric flow measurement stream template",
                    Icon = "Wind",
                    ParametersJson = JsonSerializer.Serialize(new List<string>
                    {
                        "flow_rate", "total_flow", "pressure", "temperature"
                    })
                },
                new StreamTemplate
                {
                    Id = "Steam Meter",
                    Description = "Thermal steam flow & energy measurement stream template",
                    Icon = "Thermometer",
                    ParametersJson = JsonSerializer.Serialize(new List<string>
                    {
                        "flow_rate", "total_flow", "pressure", "temperature", "enthalpy"
                    })
                }
            };

            bool addedAny = false;
            foreach (var tmpl in defaultTemplates)
            {
                var existing = await db.StreamTemplates.FirstOrDefaultAsync(x => x.Id == tmpl.Id);
                if (existing == null)
                {
                    db.StreamTemplates.Add(tmpl);
                    addedAny = true;
                }
            }

            if (addedAny)
            {
                await db.SaveChangesAsync();
            }
        }
        catch {}
    }

    // ── Telemetry Queue Migration ─────────────────────────────────────────────
    // Drop old single-metric schema and create the new merged-metrics schema.
    // Old schema: (Id, DataSourceId, PayloadJson, Timestamp, RetryCount, IsSending)
    // New schema: (Id, DataSourceId, Timestamp, MetricsJson, RetryCount, IsSending)
    //             with UNIQUE (DataSourceId, Timestamp) for upsert merging.
    //
    // This must run this DROP at most ONCE per device, ever — only when the table is
    // still on the OLD schema. InitializeAsync() runs on every process startup (real
    // devices reboot; the test suite calls it from nearly every test class's setup), so an
    // unconditional "DROP TABLE IF EXISTS QueueTelemetry" here would silently destroy the
    // entire durable store-and-forward telemetry buffer on every single restart — the exact
    // data this table exists to protect against a restart losing. Detect the new schema by
    // checking for the MetricsJson column before considering the drop; once a device has
    // migrated, this becomes a permanent no-op, same as the ALTER-TABLE-in-a-try/catch
    // migrations elsewhere in this method.
    //
    // Public and static (rather than a private instance method) so tests can exercise this
    // schema-migration logic directly against an isolated temp-path QueueDbContext, without
    // going through the full InitializeAsync() (which always targets the shared default-path
    // database and process-wide locks/guards) — see QueueStorageServiceTests for the
    // partial-schema (MetricsJson present, QualitiesJson missing) regression coverage.
    public static async Task EnsureTelemetrySchemaAsync(QueueDbContext db)
    {
        var hasMergedMetricsSchema = (await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('QueueTelemetry') WHERE name = 'MetricsJson'")
            .ToListAsync())
            .FirstOrDefault() > 0;

        if (!hasMergedMetricsSchema)
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS QueueTelemetry;");
        }

        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS QueueTelemetry (
                Id            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                DataSourceId  TEXT    NOT NULL,
                Timestamp     TEXT    NOT NULL,
                MetricsJson   TEXT    NOT NULL DEFAULT '{{}}',
                QualitiesJson TEXT    NOT NULL DEFAULT '{{}}',
                RetryCount    INTEGER NOT NULL DEFAULT 0,
                IsSending     INTEGER NOT NULL DEFAULT 0,
                UNIQUE (DataSourceId, Timestamp)
            );
        ");

        // Fast index for the sync read path: WHERE IsSending=0 ORDER BY Timestamp
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS idx_queue_pending
                ON QueueTelemetry (IsSending, Timestamp);
        ");

        // Guard against a partial merged schema (MetricsJson present, QualitiesJson not yet
        // added): patch the missing column instead of dropping data. No-ops when present.
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE QueueTelemetry ADD COLUMN QualitiesJson TEXT NOT NULL DEFAULT '{{}}';");
        }
        catch {}
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CACHING UTILITIES
    // ─────────────────────────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, bool> _enabledDataSourceCache = new();
    private DateTime _lastCacheRefresh = DateTime.MinValue;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>
    /// Checks if a data source is enabled, utilizing a thread-safe in-memory cache refreshed every 10 seconds.
    /// This avoids repetitive SQLite disk read queries during high-frequency telemetry loops.
    /// </summary>
    public async Task<bool> IsDataSourceEnabledAsync(string dataSourceId)
    {
        if (string.IsNullOrEmpty(dataSourceId)) return false;

        if (DateTime.UtcNow - _lastCacheRefresh > TimeSpan.FromSeconds(10))
        {
            await _cacheLock.WaitAsync();
            try
            {
                if (DateTime.UtcNow - _lastCacheRefresh > TimeSpan.FromSeconds(10))
                {
                    using var db = new QueueDbContext();
                    var list = await db.DataSources.Select(x => new { x.Id, x.IsEnabled }).ToListAsync();
                    _enabledDataSourceCache.Clear();
                    foreach (var item in list)
                    {
                        _enabledDataSourceCache[item.Id] = item.IsEnabled;
                    }
                    _lastCacheRefresh = DateTime.UtcNow;
                }
            }
            catch
            {
                // Ignore errors to allow fallback to cache/default
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        return _enabledDataSourceCache.TryGetValue(dataSourceId, out bool enabled) ? enabled : true;
    }

    // Fetches the saved configuration, if any
    public async Task<DeviceConfig?> GetDeviceConfigAsync()
    {
        using var db = new QueueDbContext();
        return await db.DeviceConfigs.FirstOrDefaultAsync();
    }

    /// <summary>
    /// Records the cloud's history-paused state (edge contract §5.4): a quota (<c>iot</c>,
    /// <c>storage</c>, <c>unknown</c>) and its since, or an empty quota to clear it. Touches only
    /// these two columns (EF updates just the modified properties), so it never races the other
    /// config writers. A no-op when no device config exists yet.
    /// </summary>
    public async Task SetHistoryPauseAsync(string quota, DateTime? sinceUtc)
    {
        using var db = new QueueDbContext();
        var existing = await db.DeviceConfigs.FirstOrDefaultAsync();
        if (existing == null) return;
        existing.HistoryPausedQuota = quota;
        existing.HistoryPausedSince = string.IsNullOrEmpty(quota) ? null : sinceUtc;
        await db.SaveChangesAsync();
    }

    // Saves the configuration, replacing any older one
    public async Task SaveDeviceConfigAsync(DeviceConfig config)
    {
        using var db = new QueueDbContext();
        var existing = await db.DeviceConfigs.FirstOrDefaultAsync();
        if (existing != null)
        {
            if (existing.Id != config.Id)
            {
                db.DeviceConfigs.Remove(existing);
                db.DeviceConfigs.Add(config);
            }
            else
            {
                existing.SerialNumber = config.SerialNumber;
                existing.SiteId = config.SiteId;
                existing.SiteName = config.SiteName;
                existing.ApiKey = config.ApiKey;
                existing.Version = config.Version;
                existing.IsSyncEnabled = config.IsSyncEnabled;
                existing.CloudEndpoint = config.CloudEndpoint;
                existing.CloudEdgeId = config.CloudEdgeId;
                existing.ClaimSecret = config.ClaimSecret;
                existing.CloudStatus = config.CloudStatus;
                existing.PairingToken = config.PairingToken;
                existing.PairingShortCode = config.PairingShortCode;
                existing.PairingExpiresAt = config.PairingExpiresAt;
                existing.PairingBaseUrl = config.PairingBaseUrl;
                existing.OrganizationId = config.OrganizationId;
                existing.OrganizationName = config.OrganizationName;
                db.DeviceConfigs.Update(existing);
            }
        }
        else
        {
            db.DeviceConfigs.Add(config);
        }
        await db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STORE AND FORWARD TELEMETRY QUEUE (merged-metrics design)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues a single metric reading into the merged-metrics telemetry buffer with its quality status.
    ///
    /// Grouping key: (DataSourceId, Timestamp truncated to ms)
    ///   - Uses SQLite-native INSERT OR IGNORE + UPDATE json_set/json_remove so that the
    ///     UNIQUE (DataSourceId, Timestamp) constraint itself drives the upsert.
    ///   - All tags polled in the same tick for the same stream produce ONE row.
    /// </summary>
    public async Task EnqueueTelemetryAsync(string dataSourceId, DateTime timestamp, string metricName, double? value, string quality)
    {
        using var db = new QueueDbContext();

        // Truncate to millisecond precision — stable, consistent UNIQUE key
        var ts = new DateTime(
            timestamp.Year, timestamp.Month, timestamp.Day,
            timestamp.Hour, timestamp.Minute, timestamp.Second,
            timestamp.Millisecond, DateTimeKind.Utc);

        // Initial JSON objects for a brand-new row
        var initialMetrics = value.HasValue
            ? JsonSerializer.Serialize(new Dictionary<string, double> { [metricName] = value.Value })
            : "{}";
        var initialQualities = JsonSerializer.Serialize(new Dictionary<string, string> { [metricName] = quality });

        // The JSON path for json_set, e.g. "$.temperature"
        var jsonPath = "$." + metricName;

        // Step 1: Insert a new row only if no row exists for this (stream, tick).
        //         INSERT OR IGNORE silently skips if the UNIQUE constraint fires.
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT OR IGNORE INTO QueueTelemetry (DataSourceId, Timestamp, MetricsJson, QualitiesJson, RetryCount, IsSending)
            VALUES ({dataSourceId}, {ts}, {initialMetrics}, {initialQualities}, 0, 0)
        ");

        // Step 2: Merge the metric value into the row's MetricsJson using SQLite json_set or json_remove.
        if (value.HasValue)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE QueueTelemetry
                SET MetricsJson = json_set(MetricsJson, {jsonPath}, {value.Value})
                WHERE DataSourceId = {dataSourceId} AND Timestamp = {ts} AND IsSending = 0
            ");
        }
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE QueueTelemetry
                SET MetricsJson = json_remove(MetricsJson, {jsonPath})
                WHERE DataSourceId = {dataSourceId} AND Timestamp = {ts} AND IsSending = 0
            ");
        }

        // Step 3: Merge the quality status into QualitiesJson using SQLite json_set.
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE QueueTelemetry
            SET QualitiesJson = json_set(QualitiesJson, {jsonPath}, {quality})
            WHERE DataSourceId = {dataSourceId} AND Timestamp = {ts} AND IsSending = 0
        ");

        // Enforce buffer limits after every write
        await EnforceBufferCapAsync(db);
    }

    /// <summary>
    /// Enqueues a batch of metric readings for a single DataSourceId under the same timestamp
    /// into the merged-metrics telemetry buffer with their quality statuses.
    /// This prevents interleaving sync tasks from locking out partially enqueued metrics.
    /// </summary>
    public async Task EnqueueTelemetryBatchAsync(string dataSourceId, DateTime timestamp, Dictionary<string, (double? Value, string Quality)> metrics)
    {
        if (metrics == null || metrics.Count == 0) return;

        using var db = new QueueDbContext();

        // Truncate to millisecond precision — stable, consistent UNIQUE key
        var ts = new DateTime(
            timestamp.Year, timestamp.Month, timestamp.Day,
            timestamp.Hour, timestamp.Minute, timestamp.Second,
            timestamp.Millisecond, DateTimeKind.Utc);

        // Build initial JSONs for a brand-new row
        var initialMetricsDict = new Dictionary<string, double>();
        var initialQualitiesDict = new Dictionary<string, string>();
        foreach (var kvp in metrics)
        {
            if (kvp.Value.Value.HasValue)
            {
                initialMetricsDict[kvp.Key] = kvp.Value.Value.Value;
            }
            initialQualitiesDict[kvp.Key] = kvp.Value.Quality;
        }

        var initialMetrics = JsonSerializer.Serialize(initialMetricsDict);
        var initialQualities = JsonSerializer.Serialize(initialQualitiesDict);

        // Step 1: Insert a new row only if no row exists for this (stream, tick).
        //         INSERT OR IGNORE silently skips if the UNIQUE constraint fires.
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT OR IGNORE INTO QueueTelemetry (DataSourceId, Timestamp, MetricsJson, QualitiesJson, RetryCount, IsSending)
            VALUES ({dataSourceId}, {ts}, {initialMetrics}, {initialQualities}, 0, 0)
        ");

        // Step 2 & 3: Merge the metrics and qualities into the existing row (in case it already existed and wasn't inserted).
        var setParams = new List<object>();
        var removeParams = new List<object>();
        var qualityParams = new List<object>();

        foreach (var kvp in metrics)
        {
            var jsonPath = "$." + kvp.Key;
            if (kvp.Value.Value.HasValue)
            {
                setParams.Add(jsonPath);
                setParams.Add(kvp.Value.Value.Value);
            }
            else
            {
                removeParams.Add(jsonPath);
            }

            qualityParams.Add(jsonPath);
            qualityParams.Add(kvp.Value.Quality);
        }

        // Apply json_set for values
        if (setParams.Count > 0)
        {
            var args = new List<object> { dataSourceId, ts };
            var placeholders = new List<string>();
            for (int i = 0; i < setParams.Count; i++)
            {
                placeholders.Add($"{{{args.Count}}}");
                args.Add(setParams[i]);
            }
            string query = $@"
                UPDATE QueueTelemetry
                SET MetricsJson = json_set(MetricsJson, {string.Join(", ", placeholders)})
                WHERE DataSourceId = {{0}} AND Timestamp = {{1}} AND IsSending = 0";
            await db.Database.ExecuteSqlRawAsync(query, args.ToArray());
        }

        // Apply json_remove for null values
        if (removeParams.Count > 0)
        {
            var args = new List<object> { dataSourceId, ts };
            var placeholders = new List<string>();
            for (int i = 0; i < removeParams.Count; i++)
            {
                placeholders.Add($"{{{args.Count}}}");
                args.Add(removeParams[i]);
            }
            string query = $@"
                UPDATE QueueTelemetry
                SET MetricsJson = json_remove(MetricsJson, {string.Join(", ", placeholders)})
                WHERE DataSourceId = {{0}} AND Timestamp = {{1}} AND IsSending = 0";
            await db.Database.ExecuteSqlRawAsync(query, args.ToArray());
        }

        // Apply json_set for qualities
        if (qualityParams.Count > 0)
        {
            var args = new List<object> { dataSourceId, ts };
            var placeholders = new List<string>();
            for (int i = 0; i < qualityParams.Count; i++)
            {
                placeholders.Add($"{{{args.Count}}}");
                args.Add(qualityParams[i]);
            }
            string query = $@"
                UPDATE QueueTelemetry
                SET QualitiesJson = json_set(QualitiesJson, {string.Join(", ", placeholders)})
                WHERE DataSourceId = {{0}} AND Timestamp = {{1}} AND IsSending = 0";
            await db.Database.ExecuteSqlRawAsync(query, args.ToArray());
        }

        // Enforce buffer limits
        await EnforceBufferCapAsync(db);
    }

    /// <summary>
    /// Enforces the row count cap (MaxBufferRows) and the age TTL (MaxBufferAge).
    /// Oldest rows are pruned first to preserve recent data.
    /// </summary>
    private static async Task EnforceBufferCapAsync(QueueDbContext db)
    {
        // 1. Row count cap
        var count = await db.QueueTelemetry.CountAsync();
        if (count > MaxBufferRows)
        {
            int excess = count - MaxBufferRows;
            var toDelete = await db.QueueTelemetry
                .Where(x => !x.IsSending)
                .OrderBy(x => x.Timestamp)
                .Take(excess)
                .ToListAsync();
            if (toDelete.Any())
            {
                db.QueueTelemetry.RemoveRange(toDelete);
                var now = DateTime.UtcNow;
                var lastTicks = Interlocked.Read(ref _lastBufferLossCriticalTicks);
                if (now.Ticks - lastTicks >= TimeSpan.FromMinutes(15).Ticks)
                {
                    Interlocked.Exchange(ref _lastBufferLossCriticalTicks, now.Ticks);
                    db.DiagnosticEvents.Add(new DiagnosticEvent
                    {
                        TimestampUtc = now,
                        Level = "Critical",
                        Category = typeof(QueueStorageService).FullName ?? nameof(QueueStorageService),
                        EventCode = "BUFFER_DATA_LOSS",
                        Message = $"Telemetry buffer exceeded {MaxBufferRows:N0} rows. {toDelete.Count:N0} oldest record(s) were discarded to preserve recent data.",
                        Details = "The edge is producing telemetry faster than it can synchronize. Data loss has occurred."
                    });
                }
                await db.SaveChangesAsync();
            }
        }

        // 2. Age TTL
        var cutoff = DateTime.UtcNow - MaxBufferAge;
        var stale = await db.QueueTelemetry
            .Where(x => x.Timestamp < cutoff && !x.IsSending)
            .ToListAsync();
        if (stale.Any())
        {
            db.QueueTelemetry.RemoveRange(stale);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Reads up to batchSize pending rows and marks them IsSending=true (in-flight lock).
    /// Rows are returned oldest-first to preserve delivery order.
    /// Batch size is larger (50) because each row now carries multiple metrics.
    /// </summary>
    public async Task<List<QueueTelemetry>> GetPendingTelemetryBatchAsync(int batchSize = 50)
    {
        using var db = new QueueDbContext();
        var items = await db.QueueTelemetry
            .AsNoTracking()
            .Where(x => !x.IsSending)
            .OrderBy(x => x.Timestamp)
            .Take(batchSize)
            .ToListAsync();

        if (items.Any())
        {
            var ids = items.Select(x => x.Id).ToList();
            // Flag items in database as in-flight in a single query
            await db.QueueTelemetry
                .Where(x => ids.Contains(x.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsSending, true));

            foreach (var item in items)
            {
                item.IsSending = true;
            }
        }

        return items;
    }

    // Deletes items after successful transmission to PULSE Cloud
    public async Task CompleteTelemetryBatchAsync(IEnumerable<int> ids)
    {
        using var db = new QueueDbContext();
        await db.QueueTelemetry.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync();
    }

    // Handles transmission failure: increments retry and unlocks the items
    public async Task FailTelemetryBatchAsync(IEnumerable<int> ids)
    {
        using var db = new QueueDbContext();
        await db.QueueTelemetry
            .Where(x => ids.Contains(x.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RetryCount, x => x.RetryCount + 1)
                .SetProperty(x => x.IsSending, false));
    }

    // Reset status on startup. Unconditionally clears every IsSending flag in both
    // QueueTelemetry and OeeOutboxMessages — correct as a crash-recovery sweep run once at real
    // process startup (nothing else has touched either queue yet), but not safe to call while
    // other in-flight work may be genuinely mid-drain. InitializeAsync() calls this automatically
    // exactly once per process (see the guard there). Public so a test that wants to explicitly
    // simulate "the device restarted and is sweeping up crash artifacts" can invoke the sweep
    // directly, without relying on a second InitializeAsync() call re-triggering it as a side
    // effect (that side effect is intentionally guarded away for every OTHER caller — see
    // InitializeCoreAsync's comment).
    public async Task ResetSendingStatusAsync()
    {
        using var db = new QueueDbContext();

        // Bulk ExecuteUpdateAsync, not fetch-then-foreach-then-SaveChangesAsync: the old
        // fetch/track/SaveChanges pattern snapshots rows client-side, then expects that exact
        // row count to still be affected at commit time — if ANY of those rows gets deleted or
        // completed by other in-flight work between the read and the write (a real possibility
        // any time this runs while something else is genuinely mid-drain), EF raises
        // DbUpdateConcurrencyException and the WHOLE batch (including every other, unrelated
        // row) fails to reset. A bulk UPDATE has no such stale-snapshot expectation — it simply
        // updates whatever currently matches, atomically, so a row disappearing concurrently is
        // a no-op for that row rather than an exception for everything.
        await db.QueueTelemetry
            .Where(x => x.IsSending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsSending, false));

        await db.OeeOutboxMessages
            .Where(x => x.IsSending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsSending, false));
    }
}
