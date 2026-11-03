import { DatabaseZap } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { HistoryPause } from '../types';

// PULSE Cloud stopped recording history for this organization (edge contract §5.4): the monthly
// IoT allowance is used, or storage is full. Telemetry is still delivered, so live readings and
// alarms keep working. This is NOT a network fault, and history sent meanwhile is never backfilled.
export default function HistoryPausedBanner({ pause }: { pause: HistoryPause | null | undefined }) {
  const { t } = useTranslation();
  if (!pause) return null;

  const reason = pause.quota === 'iot'
    ? t('alerts.historyPausedReasonIot')
    : pause.quota === 'storage'
      ? t('alerts.historyPausedReasonStorage')
      : t('alerts.historyPausedReasonUnknown');
  const since = pause.since ? new Date(pause.since) : null;
  const sinceText = since && !Number.isNaN(since.getTime())
    ? t('alerts.historyPausedSince', { since: since.toLocaleString() })
    : '';

  return (
    <div className="banner-warning" role="status" data-testid="history-paused-banner">
      <DatabaseZap size={20} />
      <span>
        <strong>{t('alerts.historyPausedTitle')}</strong> {reason}{sinceText ? ` ${sinceText}` : ''} {t('alerts.historyPausedMsg')}
      </span>
    </div>
  );
}
