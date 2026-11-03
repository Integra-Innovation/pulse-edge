import { useState, useEffect } from 'react';
import {
  Activity,
  Shuffle,
  Network,
  RefreshCw,
  Layers,
  Gauge,
  AlertTriangle,
  Tag,
  Settings as SettingsIcon,
  PanelLeft,
  ShieldCheck,
  ScrollText
} from 'lucide-react';
import { useToast } from './hooks/useToast';
import ToastContainer from './components/ToastContainer';

import type {
  DataSource,
  OeeStatusResponse,
} from './types';

import DashboardTab from './components/DashboardTab';
import DataSourcesTab from './components/DataSourcesTab';
import TagsTab from './components/TagsTab';
import ProtocolsTab from './components/ProtocolsTab';
import BufferTab from './components/BufferTab';
import OeeTab from './components/OeeTab';
import SettingsTab from './components/SettingsTab';
import DiagnosticLogsTab from './components/DiagnosticLogsTab';
import CriticalAlertBanner from './components/CriticalAlertBanner';
import HistoryPausedBanner from './components/HistoryPausedBanner';
import OnboardingWizard from './components/OnboardingWizard';
import OnboardingTourBanner from './components/OnboardingTourBanner';

import { useTranslation } from 'react-i18next';
import LanguageSwitcher from './components/LanguageSwitcher';

import { EdgeProvider } from './context/EdgeContext';
import { useEdge } from './context/edge';
import { ConfirmProvider } from './context/ConfirmProvider';
import { AuthProvider } from './context/AuthContext';
import { useAuth } from './context/auth';
import LoginScreen from './components/LoginScreen';
import { useDashboardData } from './hooks/useDashboardData';
import { useBufferStatus } from './hooks/useBufferStatus';
import { useAdaptersList } from './hooks/useAdaptersList';
import { useDatapointsList } from './hooks/useDatapointsList';
import FirstAdminCodeGate from './components/FirstAdminCodeGate';

const getCloudStatusInfo = (status: string | undefined, t: (key: string) => string) => {
  switch (status) {
    case 'Connected':
      return { className: '', label: t('cloudStatus.connected') };
    case 'PendingApproval':
      return { className: 'pending', label: t('cloudStatus.pending') };
    case 'Revoked':
      return { className: 'revoked', label: t('cloudStatus.revoked') };
    case 'Disconnected':
      return { className: 'error', label: t('cloudStatus.disconnected') };
    default:
      return { className: 'pending', label: t('cloudStatus.pending') };
  }
};

type Route = 'dashboard' | 'datasources' | 'tags' | 'protocols' | 'buffer' | 'oee' | 'logs' | 'settings';
const validRoutes: Route[] = ['dashboard', 'datasources', 'tags', 'protocols', 'buffer', 'oee', 'logs', 'settings'];

function getRouteFromPath(defaultRoute: Route): Route {
  const segment = window.location.pathname.split('/').filter(Boolean)[0] as Route;
  return validRoutes.includes(segment) ? segment : defaultRoute;
}

function usePathRouting(defaultRoute: Route): [Route, (route: Route) => void] {
  const [currentRoute, setCurrentRoute] = useState<Route>(() => getRouteFromPath(defaultRoute));

  const navigate = (newRoute: Route) => {
    window.history.pushState(null, '', `/${newRoute}`);
    setCurrentRoute(newRoute);
  };

  useEffect(() => {
    const handlePopState = () => {
      setCurrentRoute(getRouteFromPath(defaultRoute));
    };
    window.addEventListener('popstate', handlePopState);
    return () => window.removeEventListener('popstate', handlePopState);
  }, [defaultRoute]);

  useEffect(() => {
    const path = window.location.pathname.split('/').filter(Boolean)[0] as Route;
    if (!validRoutes.includes(path)) {
      window.history.replaceState(null, '', `/${currentRoute}`);
    }
  }, [currentRoute]);

  return [currentRoute, navigate];
}

function EdgeInner({ forceOnboarding = false }: { forceOnboarding?: boolean }) {
  const { t } = useTranslation();
  const { user, logout } = useAuth();
  const [activeTab, setActiveTab] = usePathRouting('dashboard');
  
  const {
    isConnected,
    isLoading, setIsLoading,
    dashboard,
    datasources, setDatasources,
    adapters, setAdapters,
    datapoints, setDatapoints,
    mqttDevices,
    diagnostics,
    isSyncEnabled, setIsSyncEnabled,
    bufferTelemetry,
    oeeOutbox,
    pollingInterval, setPollingInterval,
    maxLiveLogs, setMaxLiveLogs,
    telemetryWarningThreshold, setTelemetryWarningThreshold,
    eventWarningThreshold, setEventWarningThreshold,
    showDiagnosticsPanel, setShowDiagnosticsPanel,
    showLiveFeedPanel, setShowLiveFeedPanel,
    liveFeed,
    cloudEndpoint, setCloudEndpoint,
    edgeSerial, setEdgeSerial,
    isOnboarded, setIsOnboarded,
    isSidebarCollapsed, setIsSidebarCollapsed,
    fetchStaticData,
    setHasInitializedSettings
  } = useEdge();

  // Split Polling Hooks
  useDashboardData(pollingInterval); // Polls dashboard and diagnostics
  useBufferStatus(2000);             // Polls buffer status and updates live logs
  useAdaptersList(activeTab === 'protocols', 3000); // Polls adapters only when tab is active
  useDatapointsList(activeTab === 'tags' || activeTab === 'datasources' || activeTab === 'oee', pollingInterval); // Polls tags when active

  const [operationalRefreshedAt, setOperationalRefreshedAt] = useState<Date | null>(null);
  const [operationalDataStale, setOperationalDataStale] = useState(false);
  const [oeeStatus, setOeeStatus] = useState<OeeStatusResponse | null>(null);

  useEffect(() => {
    if (activeTab !== 'dashboard') return;
    let active = true;
    const refreshOperationalData = async () => {
      try {
        const [adapterRes, datapointRes, datasourceRes] = await Promise.all([
          fetch('/api/adapters'), fetch('/api/datapoints'), fetch('/api/datasources')
        ]);
        if (!adapterRes.ok || !datapointRes.ok || !datasourceRes.ok) throw new Error('Operational refresh failed');
        const [adapterData, datapointData, datasourceData] = await Promise.all([
          adapterRes.json(), datapointRes.json(), datasourceRes.json()
        ]);
        if (!active) return;
        setAdapters(adapterData);
        setDatapoints(datapointData);
        setDatasources(datasourceData);
        setOperationalRefreshedAt(new Date());
        setOperationalDataStale(false);
      } catch (error) {
        if (active) setOperationalDataStale(true);
        console.error('Failed to refresh dashboard operational data:', error);
      }

      // OEE status rides the same cadence but fails independently (null = unavailable).
      try {
        const oeeRes = await fetch('/api/oee/status');
        if (active) setOeeStatus(oeeRes.ok ? await oeeRes.json() : null);
      } catch {
        if (active) setOeeStatus(null);
      }
    };
    void refreshOperationalData();
    const interval = window.setInterval(refreshOperationalData, pollingInterval);
    return () => { active = false; window.clearInterval(interval); };
  }, [activeTab, pollingInterval, setAdapters, setDatapoints, setDatasources]);

  const toggleSidebar = () => {
    setIsSidebarCollapsed(!isSidebarCollapsed);
    localStorage.setItem('sidebarCollapsed', String(!isSidebarCollapsed));
  };

  const { toasts, removeToast, toast } = useToast();

  const handleToggleSync = async () => {
    try {
      const res = await fetch('/api/settings/toggle-sync', { method: 'POST' });
      if (res.ok) {
        const data = await res.json();
        setIsSyncEnabled(data.isSyncEnabled);
        void fetchStaticData();
      }
    } catch (e) {
      console.error('Failed to toggle sync status:', e);
    }
  };

  const handleToggleStreamEnabled = async (ds: DataSource) => {
    try {
      const updated = { ...ds, isEnabled: !ds.isEnabled };
      const res = await fetch('/api/datasources', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(updated)
      });
      if (res.ok) {
        void fetchStaticData();
      } else {
        toast.error('Failed to toggle stream state.');
      }
    } catch (err) {
      console.error('Error toggling stream state:', err);
      toast.error('Error toggling stream state.');
    }
  };

  const handleDeleteDataPoint = async (id: string) => {
    try {
      const res = await fetch(`/api/datapoints/${id}`, { method: 'DELETE' });
      if (res.ok) {
        void fetchStaticData();
      } else {
        toast.error('Failed to unbind data point metric.');
      }
    } catch (err) {
      console.error('Failed to delete data point:', err);
      toast.error('Failed to delete data point.');
    }
  };

  const handleDeleteStream = async (id: string) => {
    try {
      const res = await fetch(`/api/datasources/${id}`, { method: 'DELETE' });
      if (res.ok) {
        void fetchStaticData();
        toast.success('Stream object deleted successfully.');
      } else {
        const errData = await res.json().catch(() => ({}));
        toast.error(errData.error || 'Failed to delete stream object.');
      }
    } catch (err) {
      console.error('Failed to delete stream:', err);
      toast.error('Failed to delete stream.');
    }
  };

  const handleRenameStream = async (ds: DataSource, newName: string) => {
    try {
      const updated = { ...ds, name: newName };
      const res = await fetch('/api/datasources', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(updated)
      });
      if (res.ok) {
        void fetchStaticData();
        toast.success(`Stream renamed to '${newName}' successfully.`);
      } else {
        const errData = await res.json().catch(() => ({}));
        toast.error(errData.error || 'Failed to rename stream.');
      }
    } catch (err) {
      console.error('Error renaming stream:', err);
      toast.error('Error renaming stream.');
    }
  };

  const handleSaveSettings = async () => {
    try {
      const res = await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          serialNumber: edgeSerial,
          cloudEndpoint: cloudEndpoint
        })
      });

      if (res.ok) {
        toast.success('System settings saved successfully to SQLite.');
        void fetchStaticData();
      } else {
        const data = await res.json().catch(() => ({}));
        toast.error(data.error || 'Failed to save system settings.');
      }
    } catch (err) {
      console.error('Failed to save settings:', err);
      toast.error('An error occurred while saving settings.');
    }
  };

  const handleFactoryReset = async () => {
    try {
      const res = await fetch('/api/settings/factory-reset', {
        method: 'POST'
      });

      if (res.ok) {
        localStorage.removeItem('pulse_onboarding_tour_dismissed');
        localStorage.removeItem('pulse_onboarding_completed');
        window.location.assign('/');
      } else {
        const data = await res.json().catch(() => ({}));
        toast.error(data.error || 'Failed to perform factory reset.');
      }
    } catch (err) {
      console.error('Failed to factory reset:', err);
      toast.error('An error occurred during factory reset.');
    }
  };

  const handleSoftReset = async () => {
    try {
      const res = await fetch('/api/settings/soft-reset', {
        method: 'POST'
      });

      if (res.ok) {
        toast.success('Agent cloud pairing has been successfully reset. Configurations preserved.');
        
        localStorage.removeItem('pulse_onboarding_tour_dismissed');
        localStorage.removeItem('pulse_onboarding_completed');
        setIsOnboarded(false);
        setHasInitializedSettings(false);
        setActiveTab('dashboard');
        
        void fetchStaticData();
      } else {
        const data = await res.json().catch(() => ({}));
        toast.error(data.error || 'Failed to perform soft reset.');
      }
    } catch (err) {
      console.error('Failed to soft reset:', err);
      toast.error('An error occurred during soft reset.');
    }
  };

  // UI config persistence effects
  useEffect(() => {
    localStorage.setItem('pulse_ui_polling_interval', pollingInterval.toString());
  }, [pollingInterval]);

  useEffect(() => {
    localStorage.setItem('pulse_ui_max_live_logs', maxLiveLogs.toString());
  }, [maxLiveLogs]);

  useEffect(() => {
    localStorage.setItem('pulse_ui_telemetry_threshold', telemetryWarningThreshold.toString());
  }, [telemetryWarningThreshold]);

  useEffect(() => {
    localStorage.setItem('pulse_ui_event_threshold', eventWarningThreshold.toString());
  }, [eventWarningThreshold]);

  useEffect(() => {
    localStorage.setItem('pulse_ui_show_diagnostics', showDiagnosticsPanel.toString());
  }, [showDiagnosticsPanel]);

  useEffect(() => {
    localStorage.setItem('pulse_ui_show_live_feed', showLiveFeedPanel.toString());
  }, [showLiveFeedPanel]);

  // Dynamic filter states for live feed
  const [telemetryFilterQuery, setTelemetryFilterQuery] = useState('');
  const [telemetryFilterType, setTelemetryFilterType] = useState('All');

  if (forceOnboarding || !isOnboarded) {
    return (
      <>
        <OnboardingWizard 
          toast={toast} 
          requireFirstAdmin={!user}
          onComplete={() => { 
            localStorage.setItem('pulse_onboarding_completed', 'true');
            setIsOnboarded(true); 
            setHasInitializedSettings(false);
            void fetchStaticData(); 
          }} 
        />
        <ToastContainer toasts={toasts} onRemove={removeToast} />
      </>
    );
  }

  return (
    <div className={`app-container${user?.role === 'ReadOnly' ? ' role-read-only' : ''}`}>
      <aside className={`sidebar ${isSidebarCollapsed ? 'collapsed' : ''}`}>
        <div className="sidebar-brand">
          {!isSidebarCollapsed && (
            <div className="sidebar-logo">
              PULSE <span>EDGE</span>
            </div>
          )}
          <button
            onClick={toggleSidebar} 
            className="sidebar-toggle-btn"
            title={isSidebarCollapsed ? t('sidebar.expandSidebar') : t('sidebar.collapseSidebar')}
          >
            <PanelLeft size={18} />
          </button>
        </div>
        
        <nav className="sidebar-menu">
          <button 
            className={`menu-item ${activeTab === 'dashboard' ? 'active' : ''}`}
            onClick={() => setActiveTab('dashboard')}
            title={isSidebarCollapsed ? t('nav.dashboard') : undefined}
          >
            <Activity size={18} />
            {!isSidebarCollapsed && <span>{t('nav.dashboard')}</span>}
          </button>
          
          <button 
            className={`menu-item ${activeTab === 'protocols' ? 'active' : ''}`}
            onClick={() => setActiveTab('protocols')}
            title={isSidebarCollapsed ? t('nav.protocols') : undefined}
          >
            <Network size={18} />
            {!isSidebarCollapsed && <span>{t('nav.protocols')}</span>}
          </button>
 
          <button 
            className={`menu-item ${activeTab === 'tags' ? 'active' : ''}`}
            onClick={() => setActiveTab('tags')}
            title={isSidebarCollapsed ? t('nav.tags') : undefined}
          >
            <Tag size={18} />
            {!isSidebarCollapsed && <span>{t('nav.tags')}</span>}
          </button>
 
          <button 
            className={`menu-item ${activeTab === 'datasources' ? 'active' : ''}`}
            onClick={() => setActiveTab('datasources')}
            title={isSidebarCollapsed ? t('nav.streams') : undefined}
          >
            <Shuffle size={18} />
            {!isSidebarCollapsed && <span>{t('nav.streams')}</span>}
          </button>
 
          <button
            className={`menu-item ${activeTab === 'oee' ? 'active' : ''}`}
            onClick={() => setActiveTab('oee')}
            title={isSidebarCollapsed ? t('nav.oee') : undefined}
          >
            <Gauge size={18} />
            {!isSidebarCollapsed && <span>{t('nav.oee')}</span>}
          </button>

          <button 
            className={`menu-item ${activeTab === 'buffer' ? 'active' : ''}`}
            onClick={() => setActiveTab('buffer')}
            title={isSidebarCollapsed ? `${t('nav.buffer')} (${bufferTelemetry.length + oeeOutbox.length})` : undefined}
          >
            <Layers size={18} />
            {!isSidebarCollapsed && <span>{t('nav.buffer')} ({bufferTelemetry.length + oeeOutbox.length})</span>}
          </button>

          <button
            className={`menu-item ${activeTab === 'logs' ? 'active' : ''}`}
            onClick={() => setActiveTab('logs')}
            title={isSidebarCollapsed ? t('nav.logs') : undefined}
          >
            <ScrollText size={18} />
            {!isSidebarCollapsed && <span>{t('nav.logs')}</span>}
          </button>

          <button
            className={`menu-item ${activeTab === 'settings' ? 'active' : ''}`}
            onClick={() => setActiveTab('settings')}
            title={isSidebarCollapsed ? t('nav.settings') : undefined}
          >
            <SettingsIcon size={18} />
            {!isSidebarCollapsed && <span>{t('nav.settings')}</span>}
          </button>
        </nav>
        
        {!isSidebarCollapsed && (
          <div className="sidebar-footer">
            <div>{t('sidebar.agentVersion', { version: dashboard?.version || '1.0.0' })}</div>
            <div className="sidebar-footer-note">{t('sidebar.dbStatus')}</div>
          </div>
        )}
      </aside>
 
      <main className="main-content">
        <div className="content-area">
          <CriticalAlertBanner onOpenLogs={() => setActiveTab('logs')} />
          <HistoryPausedBanner pause={dashboard?.historyPause} />
          {!isSyncEnabled && (
            <div className="banner-warning">
              <AlertTriangle size={20} />
              <span>
                <strong>{t('alerts.syncPausedTitle')}</strong> {t('alerts.syncPausedMsg')}
              </span>
            </div>
          )}

          {isLoading && !dashboard ? (
            <div className="loading-center">
              Loading edge statistics...
            </div>
          ) : (
            <>
              <OnboardingTourBanner 
                adapters={adapters}
                datapoints={datapoints}
                setActiveTab={setActiveTab}
              />
              {activeTab === 'dashboard' && (
                <DashboardTab
                  isSyncEnabled={isSyncEnabled}
                  handleToggleSync={handleToggleSync}
                  bufferTelemetry={bufferTelemetry}
                  telemetryWarningThreshold={telemetryWarningThreshold}
                  oeeOutbox={oeeOutbox}
                  eventWarningThreshold={eventWarningThreshold}
                  diagnostics={diagnostics}
                  adapters={adapters}
                  datapoints={datapoints}
                  datasources={datasources}
                  isConnected={isConnected}
                  operationalRefreshedAt={operationalRefreshedAt}
                  operationalDataStale={operationalDataStale}
                  showDiagnosticsPanel={showDiagnosticsPanel}
                  showLiveFeedPanel={showLiveFeedPanel}
                  liveFeed={liveFeed}
                  telemetryFilterQuery={telemetryFilterQuery}
                  setTelemetryFilterQuery={setTelemetryFilterQuery}
                  telemetryFilterType={telemetryFilterType}
                  setTelemetryFilterType={setTelemetryFilterType}
                  maxLiveLogs={maxLiveLogs}
                  setMaxLiveLogs={setMaxLiveLogs}
                  oeeStatus={oeeStatus}
                  onOpenOee={() => setActiveTab('oee')}
                />
              )}
 
              {activeTab === 'datasources' && (
                <DataSourcesTab
                  datasources={datasources}
                  datapoints={datapoints}
                  adapters={adapters}
                  handleToggleStreamEnabled={handleToggleStreamEnabled}
                  handleDeleteDataPoint={handleDeleteDataPoint}
                  handleDeleteStream={handleDeleteStream}
                  handleRenameStream={handleRenameStream}
                  fetchData={fetchStaticData}
                  toast={toast}
                />
              )}
 
              {activeTab === 'tags' && (
                <TagsTab
                  datapoints={datapoints}
                  adapters={adapters}
                  mqttDevices={mqttDevices}
                  handleDeleteDataPoint={handleDeleteDataPoint}
                  fetchData={fetchStaticData}
                  toast={toast}
                />
              )}
 
              {activeTab === 'protocols' && (
                <ProtocolsTab
                  adapters={adapters}
                  datapoints={datapoints}
                  mqttDevices={mqttDevices}
                  fetchData={fetchStaticData}
                  toast={toast}
                />
              )}
 
              {activeTab === 'buffer' && (
                <BufferTab
                  bufferTelemetry={bufferTelemetry}
                  oeeOutbox={oeeOutbox}
                />
              )}

              {activeTab === 'oee' && (
                <OeeTab datapoints={datapoints} adapters={adapters} />
              )}
              {activeTab === 'logs' && <DiagnosticLogsTab />}
 
              {activeTab === 'settings' && (
                <SettingsTab
                  dashboard={dashboard}
                  cloudEndpoint={cloudEndpoint}
                  setCloudEndpoint={setCloudEndpoint}
                  edgeSerial={edgeSerial}
                  setEdgeSerial={setEdgeSerial}
                  handleSaveSettings={handleSaveSettings}
                  pollingInterval={pollingInterval}
                  setPollingInterval={setPollingInterval}
                  maxLiveLogs={maxLiveLogs}
                  setMaxLiveLogs={setMaxLiveLogs}
                  telemetryWarningThreshold={telemetryWarningThreshold}
                  setTelemetryWarningThreshold={setTelemetryWarningThreshold}
                  eventWarningThreshold={eventWarningThreshold}
                  setEventWarningThreshold={setEventWarningThreshold}
                  showLiveFeedPanel={showLiveFeedPanel}
                  setShowLiveFeedPanel={setShowLiveFeedPanel}
                  showDiagnosticsPanel={showDiagnosticsPanel}
                  setShowDiagnosticsPanel={setShowDiagnosticsPanel}
                  handleFactoryReset={handleFactoryReset}
                  handleSoftReset={handleSoftReset}
                  onRestoreComplete={fetchStaticData}
                  onOpenWizard={() => setIsOnboarded(false)}
                />
              )}
            </>
          )}
        </div>

        <footer className="bottombar">
          <div className="bottombar-badge-group">
            {dashboard?.device.organizationName && dashboard.device.organizationName !== 'N/A' && (
              <div className="bottombar-node-badge">
                <span className="badge-label">{t('bottombar.org')}</span>
                <span className="badge-value">{dashboard.device.organizationName}</span>
              </div>
            )}

            {dashboard?.device.siteName && dashboard.device.siteName !== 'N/A' && (
              <div className="bottombar-node-badge">
                <span className="badge-label">{t('bottombar.site')}</span>
                <span className="badge-value">{dashboard.device.siteName}</span>
              </div>
            )}

            <div className="bottombar-node-badge">
              <span className="badge-label">{t('bottombar.node')}</span>
              <span className="badge-value">{edgeSerial}</span>
            </div>
          </div>
          
          <div className="bottombar-status">
            <LanguageSwitcher />
            {user && (
              <div className="status-indicator auth-user-chip">
                <ShieldCheck size={14} />
                <span>{user.username} · {user.role === 'ReadOnly' ? t('common.readOnly') : t('common.admin')}</span>
                <button onClick={() => void logout()}>{t('common.signOut')}</button>
              </div>
            )}
            <div className="status-indicator">
              <div className={`pulse-dot ${isConnected ? '' : 'warning'}`} />
              <span>{isConnected ? t('bottombar.localAgentOnline') : t('bottombar.localAgentOffline')}</span>
            </div>

            {isConnected && dashboard && (
              (() => {
                const cloudInfo = getCloudStatusInfo(dashboard.cloudStatus, t);
                const isPending = dashboard.cloudStatus === 'PendingApproval';
                return (
                  <div
                    className="status-indicator status-indicator-divider"
                    style={{ cursor: isPending ? 'pointer' : 'default' }}
                    onClick={() => {
                      if (isPending) {
                        setIsOnboarded(false);
                      }
                    }}
                    title={isPending ? 'Click to open Cloud Pairing Wizard' : undefined}
                  >
                    <div className={`pulse-dot ${cloudInfo.className}`} />
                    <span>{cloudInfo.label}</span>
                  </div>
                );
              })()
            )}
            
            <button
              type="button"
              onClick={() => { setIsLoading(true); void fetchStaticData(); }}
              className="refresh-btn"
              title={t('common.forceRefresh')}
              aria-label="Force refresh data"
            >
              <RefreshCw size={16} className={isLoading ? 'spin refresh-icon' : 'refresh-icon'} />
            </button>
          </div>
        </footer>
      </main>
      <ToastContainer toasts={toasts} onRemove={removeToast} />
    </div>
  );
}

export default function App() {
  return <AuthProvider><AppGate /></AuthProvider>;
}

function AppGate() {
  return <FirstAdminCodeGate><AppRoutes /></FirstAdminCodeGate>;
}

function AppRoutes() {
  const { t } = useTranslation();
  const { loading, setupState, user } = useAuth();
  if (loading) return <div className="auth-loading"><Activity className="spin" size={28} /> {t('bottombar.loadingAccess')}</div>;
  if (setupState === 'Operational' && !user) return <LoginScreen />;
  return (
    <EdgeProvider>
      <ConfirmProvider>
        <EdgeInner forceOnboarding={setupState !== 'Operational'} />
      </ConfirmProvider>
    </EdgeProvider>
  );
}
