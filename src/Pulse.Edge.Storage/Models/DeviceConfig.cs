using System;

namespace Pulse.Edge.Storage.Models;

public class DeviceConfig
{
    public string Id { get; set; } = string.Empty; // Store UUID as string
    public string CloudEdgeId { get; set; } = string.Empty; // Cloud's UUID for this device
    public string ClaimSecret { get; set; } = string.Empty; // Device-generated raw secret
    public string PairingToken { get; set; } = string.Empty; // Device-generated pairing token
    public string PairingShortCode { get; set; } = string.Empty; // Received display code
    public DateTime? PairingExpiresAt { get; set; } // ShortCode and link expiry
    public string PairingBaseUrl { get; set; } = string.Empty; // Base URL for constructing pairing link
    public string SerialNumber { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string CloudEndpoint { get; set; } = "http://localhost:3000"; // Target cloud URL
    public string Version { get; set; } = "1.0.0";
    public bool IsSyncEnabled { get; set; } = true; // Flag to toggle cloud sync on/off
    public string CloudStatus { get; set; } = "PendingApproval"; // e.g. Connected, PendingApproval, Disconnected, Revoked

    // PULSE Cloud's "history paused" notice (edge contract §5.4): set from a 202 that carries
    // quota_exceeded, cleared by the next 202 without it. Empty = history is recording.
    // Written only by QueueStorageService.SetHistoryPauseAsync, never by SaveDeviceConfigAsync,
    // so a service saving an older copy of the config can't bring a cleared pause back.
    public string HistoryPausedQuota { get; set; } = string.Empty; // "iot" | "storage" | "unknown" | ""
    public DateTime? HistoryPausedSince { get; set; } // the server's "since" (UTC); null if unknown
}
