using Microsoft.EntityFrameworkCore;
using Pulse.Edge.Storage.Models;
using Pulse.Edge.Storage.Security;

namespace Pulse.Edge.Storage;

public class QueueDbContext : DbContext
{
    private readonly string? _databasePath;

    public QueueDbContext()
    {
    }

    public QueueDbContext(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        _databasePath = Path.GetFullPath(databasePath);
    }

    public DbSet<DeviceConfig> DeviceConfigs => Set<DeviceConfig>();
    public DbSet<QueueTelemetry> QueueTelemetry => Set<QueueTelemetry>();
    public DbSet<DriverAdapter> DriverAdapters => Set<DriverAdapter>();
    public DbSet<DataSource> DataSources => Set<DataSource>();
    public DbSet<DataPoint> DataPoints => Set<DataPoint>();
    public DbSet<MqttDevice> MqttDevices => Set<MqttDevice>();
    public DbSet<StreamTemplate> StreamTemplates => Set<StreamTemplate>();
    public DbSet<LocalUser> LocalUsers => Set<LocalUser>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<DiagnosticEvent> DiagnosticEvents => Set<DiagnosticEvent>();
    public DbSet<DiagnosticCaptureConfig> DiagnosticCaptureConfigs => Set<DiagnosticCaptureConfig>();
    public DbSet<OeeChannel> OeeChannels => Set<OeeChannel>();
    public DbSet<OeeOutboxMessage> OeeOutboxMessages => Set<OeeOutboxMessage>();

    /// <summary>
    /// The PULSE Edge data directory (not created here): <c>PULSE_EDGE_DATA_DIR</c> when set, else the
    /// OS default (Windows: CommonApplicationData\PULSE Edge; elsewhere: ~/.pulse).
    /// </summary>
    public static string ResolveDataDirectory()
    {
        // An explicit PULSE_EDGE_DATA_DIR override wins (used by integration tests and by
        // operators who relocate the data directory).
        var overrideDir = Environment.GetEnvironmentVariable("PULSE_EDGE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return overrideDir;

        if (OperatingSystem.IsWindows())
        {
            var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(appDataFolder, "PULSE Edge");
        }

        var userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userFolder, ".pulse");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured) return;

        if (_databasePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            optionsBuilder.UseSqlite($"Data Source={_databasePath};Default Timeout={BusyTimeoutSeconds}");
            return;
        }

        var pulseFolder = ResolveDataDirectory();
        Directory.CreateDirectory(pulseFolder); // Ensure the folder exists
        var dbPath = Path.Combine(pulseFolder, "edge.db");

        optionsBuilder.UseSqlite($"Data Source={dbPath};Default Timeout={BusyTimeoutSeconds}");
    }

    // Sets the "Default Timeout" connection-string option, which Microsoft.Data.Sqlite (the
    // ADO.NET driver EF Core uses here) applies as its own client-side command-timeout: on
    // SQLITE_BUSY it retries in a loop for up to this many seconds before giving up and
    // throwing, rather than failing immediately. This is NOT sqlite3_busy_timeout (SQLite's own
    // native busy-handler) — it's a driver-level retry loop layered on top. SQLite only ever
    // allows one writer at a time even in WAL mode, and this single file is shared by every
    // QueueDbContext instance across the whole process (the acquisition pipeline, the API,
    // sync, and — in the test suite — dozens of concurrently-running test classes all hitting
    // it at once), so contention here is expected. 30s is already Microsoft.Data.Sqlite's own
    // default; it's set explicitly here to document that choice rather than to change it.
    private const int BusyTimeoutSeconds = 30;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Encrypt the cloud credentials at rest (Slice 2C / B-07). The DB stores ciphertext;
        // every reader sees plaintext. The converter reads SecretProtection.Protector at call
        // time, so it works regardless of when the protector is configured. Device/protocol
        // credentials (DriverAdapter.ConfigJson) are intentionally NOT encrypted (R-008).
        var secretConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<string, string>(
            plaintext => SecretProtection.Protector.Protect(plaintext),
            stored => SecretProtection.Protector.Unprotect(stored));

        modelBuilder.Entity<DeviceConfig>().Property(x => x.ApiKey).HasConversion(secretConverter);
        modelBuilder.Entity<DeviceConfig>().Property(x => x.ClaimSecret).HasConversion(secretConverter);
        modelBuilder.Entity<DeviceConfig>().Property(x => x.PairingToken).HasConversion(secretConverter);
        // History-paused notice (edge contract §5.4). Same DEFAULT '' as the raw CREATE / ALTER in
        // QueueStorageService, so a table built by EnsureCreated also accepts rows written
        // without this column (older code paths, raw inserts).
        modelBuilder.Entity<DeviceConfig>().Property(x => x.HistoryPausedQuota).HasDefaultValue(string.Empty);

        modelBuilder.Entity<OeeChannel>().HasIndex(x => x.ExternalId).IsUnique();
        modelBuilder.Entity<OeeOutboxMessage>().HasIndex(x => new { x.ChannelId, x.Seq }).IsUnique();
    }
}
