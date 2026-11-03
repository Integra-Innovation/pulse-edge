using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pulse.Edge.Storage;
using Pulse.Edge.Storage.Services;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace Pulse.Edge.Api.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/dashboard", async (QueueStorageService storageService, HttpContext context) =>
        {
            using var db = new QueueDbContext();

            // Ensure database is created (just in case API is run before Agent)
            await db.Database.EnsureCreatedAsync();

            var config = await db.DeviceConfigs.FirstOrDefaultAsync();

            int pendingTelemetryCount = await db.QueueTelemetry.CountAsync();
            int pendingOeeCount = await db.OeeOutboxMessages.CountAsync();

            // Pairing credentials are Admin-only once an admin exists (paired or not). During initial
            // setup (no users yet) they must be visible so the operator can pair the device.
            bool hasUsers = await db.LocalUsers.AnyAsync();
            bool showPairing = !hasUsers || context.User.IsInRole("Admin");

            return Results.Ok(new
            {
                ConnectionStatus = "Connected",
                CloudStatus = config?.CloudStatus ?? "PendingApproval",
                // PULSE Cloud's history-paused notice (edge contract §5.4); null while history records.
                HistoryPause = string.IsNullOrEmpty(config?.HistoryPausedQuota)
                    ? null
                    : new
                    {
                        Quota = config.HistoryPausedQuota,
                        Since = config.HistoryPausedSince.HasValue ? DateTime.SpecifyKind(config.HistoryPausedSince.Value, DateTimeKind.Utc) : (DateTime?)null,
                    },
                BufferStatus = pendingTelemetryCount + pendingOeeCount > 0 ? "Buffering" : "Healthy",
                Version = config?.Version ?? "1.0.0",
                LastSync = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture),
                Device = new
                {
                    DeviceId = config?.Id ?? "Not Registered",
                    CloudEdgeId = config?.CloudEdgeId ?? "Not Registered",
                    SerialNumber = config?.SerialNumber ?? "N/A",
                    PairingToken = showPairing ? (config?.PairingToken ?? "") : "",
                    PairingShortCode = showPairing ? (config?.PairingShortCode ?? "") : "",
                    PairingExpiresAt = showPairing && config != null && config.PairingExpiresAt.HasValue ? DateTime.SpecifyKind(config.PairingExpiresAt.Value, DateTimeKind.Utc) : (DateTime?)null,
                    PairingBaseUrl = showPairing ? (config?.PairingBaseUrl ?? "") : "",
                    OrganizationId = config?.OrganizationId ?? "N/A",
                    OrganizationName = config?.OrganizationName ?? "N/A",
                    SiteId = config?.SiteId ?? "N/A",
                    SiteName = config?.SiteName ?? "N/A",
                    ApiKey = !string.IsNullOrEmpty(config?.ApiKey) ? "••••••••" + config.ApiKey[Math.Max(0, config.ApiKey.Length - 6)..] : "None",
                    CloudEndpoint = config?.CloudEndpoint ?? "http://localhost:3000"
                },
                Queue = new
                {
                    PendingTelemetry = pendingTelemetryCount,
                    PendingOee = pendingOeeCount
                }
            });
        });

        DateTime lastCpuTime = DateTime.UtcNow;
        TimeSpan lastTotalProcessorTime = TimeSpan.Zero;
        object cpuLock = new();

        routes.MapGet("/api/diagnostics", () =>
        {
            // 1. Process CPU Usage
            double cpuPercent = 0.0;
            lock (cpuLock)
            {
                var now = DateTime.UtcNow;
                using var process = Process.GetCurrentProcess();
                var cpuTime = process.TotalProcessorTime;
                
                if (lastTotalProcessorTime != TimeSpan.Zero)
                {
                    var systemTimeDelta = now - lastCpuTime;
                    var cpuTimeDelta = cpuTime - lastTotalProcessorTime;
                    if (systemTimeDelta.TotalMilliseconds > 0)
                    {
                        cpuPercent = (cpuTimeDelta.TotalMilliseconds / (systemTimeDelta.TotalMilliseconds * Environment.ProcessorCount)) * 100.0;
                        cpuPercent = Math.Min(100.0, Math.Max(0.0, cpuPercent));
                    }
                }
                lastCpuTime = now;
                lastTotalProcessorTime = cpuTime;
            }

            // 2. Process Memory Footprint
            long workingSetBytes;
            DateTime processStartTime;
            using (var process = Process.GetCurrentProcess())
            {
                workingSetBytes = process.WorkingSet64;
                processStartTime = process.StartTime.ToUniversalTime();
            }
            double memoryUsageMb = workingSetBytes / (1024.0 * 1024.0);

            // 3. Disk Space (Root / App drive)
            double usedSpaceGb = 0.0;
            double totalSpaceGb = 0.0;
            try
            {
                string rootPath = OperatingSystem.IsWindows() ? "C:\\" : "/";
                var drive = new DriveInfo(rootPath);
                totalSpaceGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                double availableSpaceGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                usedSpaceGb = totalSpaceGb - availableSpaceGb;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading DriveInfo: {ex.Message}");
            }

            // 4. Uptime
            var uptime = DateTime.UtcNow - processStartTime;
            string uptimeStr = $"{uptime.Days:D2}d:{uptime.Hours:D2}h:{uptime.Minutes:D2}m";

            return Results.Ok(new
            {
                CpuUsage = $"{cpuPercent:F1}%",
                MemoryUsage = $"{memoryUsageMb:F1} MB",
                DiskSpace = $"{usedSpaceGb:F1} GB / {totalSpaceGb:F0} GB",
                Uptime = uptimeStr
            });
        });
    }
}
