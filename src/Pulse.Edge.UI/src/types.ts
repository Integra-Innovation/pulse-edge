/** Edge contract §5.4: the cloud keeps only latest values, not history. `since` is ISO UTC or null. */
export interface HistoryPause {
  quota: 'iot' | 'storage' | 'unknown' | string;
  since: string | null;
}

export interface DashboardData {
  connectionStatus: string;
  cloudStatus: string;
  /** PULSE Cloud stopped recording history (IoT allowance used or storage full); null while it records. */
  historyPause?: HistoryPause | null;
  bufferStatus: string;
  version: string;
  lastSync: string;
  device: {
    deviceId: string;
    cloudEdgeId: string;
    serialNumber: string;
    siteId: string;
    siteName: string;
    apiKey: string;
    cloudEndpoint: string;
    pairingToken?: string;
    pairingShortCode?: string;
    pairingExpiresAt?: string | null;
    pairingBaseUrl?: string;
    organizationId?: string;
    organizationName?: string;
  };
  queue: {
    pendingTelemetry: number;
    pendingOee: number;
  };
}

export interface DriverAdapter {
  id: string;
  name: string;
  protocol: string; // "OPC_UA", "MODBUS_TCP", "MQTT", "Ethernet/IP"
  host: string;
  port: number;
  configJson: string;
  isEnabled: boolean;
  status: string; // "Connected", "Offline", "Error"
}

export interface DataSource {
  id: string;
  name: string;
  type: string; // "Production" | "Energy" | "General"
  description: string;
  isEnabled: boolean;
}

export interface DataPoint {
  id: string;
  adapterId: string;
  mqttDeviceId: string | null;
  dataSourceId: string | null;
  metric: string | null;
  address: string; // tag / register / topic
  dataType: string;
  scanIntervalMs: number;
  scaleFactor: number;
  offset: number;
  isEnabled: boolean;
  byteOrder: string;
  mqttParseMode: string;
  mqttJsonPath: string | null;
  description?: string | null;
  lastValue?: string | null;
  lastError?: string | null;
  lastUpdated?: string | null;
  lastLatencyMs?: number | null;
  consecutiveFailures?: number;
}

export interface MqttDevice {
  id: string;
  adapterId: string;
  name: string;
  topicSubscription: string;
  mqttParseMode: string; // "Plaintext" | "JSON"
  isEnabled: boolean;
  lwtTopic: string | null;
  lwtOnlinePayload: string;
  lwtOfflinePayload: string;
  status: string; // "Connected", "Offline", "Error"
  lastError: string | null;
  lastUpdated: string | null;
  consecutiveFailures: number;
}

export interface DiagnosticData {
  cpuUsage: string;
  memoryUsage: string;
  diskSpace: string;
  uptime: string;
}

export interface BufferTelemetryItem {
  id: number;
  dataSourceId: string;
  /** MetricsJson: merged dict e.g. {"temperature":85.3,"good_count":142} */
  metricsJson: string;
  qualitiesJson?: string;
  timestamp: string;
  retryCount: number;
  isSending: boolean;
}

export interface OeeOutboxItem {
  id: number;
  channelId: number;
  seq: number;
  type: string;      // "state" | "sync"
  ts: string;
  state: string;     // "running" | "stopped" | "fault"
  code: string | null;
  goodCount: number | null;
  rejectCount: number | null;
  isSending: boolean;
  retryCount: number;
  createdAt: string;
}

export interface OeeChannel {
  id: number;
  externalId: string;
  name: string;
  enabled: boolean;
  runDataPointId: string;
  faultDataPointId: string | null;
  codeDataPointId: string | null;
  goodDataPointId: string | null;
  rejectDataPointId: string | null;
  debounceSeconds: number;
  nextSeq: number;
  lastState: string | null;
  lastStateChangedAt: string | null;
  lastCode: string | null;
  updatedAt: string;
}

export interface OeeChannelStatus {
  id: number;
  externalId: string;
  name: string;
  enabled: boolean;
  lastState: string | null;
  lastCode: string | null;
  lastStateChangedAt: string | null;
  nextSeq: number;
  pendingCount: number;
}

export interface OeeStatusResponse {
  channels: OeeChannelStatus[];
  outboxDepth: number;
}

export interface StreamTemplate {
  id: string;
  description: string;
  parametersJson: string;
  icon: string;
}
