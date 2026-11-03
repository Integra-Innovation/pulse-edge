import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import HistoryPausedBanner from './HistoryPausedBanner';

// Edge contract §5.4: PULSE Cloud answers 202 + quota_exceeded {quota, history:'paused', since};
// the dashboard surfaces it as historyPause. These are the operator-visible states.
describe('HistoryPausedBanner', () => {
  it('shows nothing while history is recording', () => {
    const { container } = render(<HistoryPausedBanner pause={null} />);
    expect(container).toBeEmptyDOMElement();
    const { container: c2 } = render(<HistoryPausedBanner pause={undefined} />);
    expect(c2).toBeEmptyDOMElement();
  });

  it('IoT allowance used: names the reason and the since, and says it is not a network problem and not backfilled', () => {
    const since = '2026-10-31T04:12:00.000Z';
    render(<HistoryPausedBanner pause={{ quota: 'iot', since }} />);
    const banner = screen.getByTestId('history-paused-banner');
    expect(banner).toHaveTextContent(/history recording paused/i);
    expect(banner).toHaveTextContent(/monthly IoT allowance/i);
    expect(banner).toHaveTextContent(new Date(since).toLocaleString());
    expect(banner).toHaveTextContent(/not a network problem/i);
    expect(banner).toHaveTextContent(/live readings and alarms keep working/i);
    expect(banner).toHaveTextContent(/will not be backfilled/i);
  });

  it('storage full: names storage as the reason', () => {
    render(<HistoryPausedBanner pause={{ quota: 'storage', since: '2026-10-31T04:12:00.000Z' }} />);
    expect(screen.getByTestId('history-paused-banner')).toHaveTextContent(/cloud storage is full/i);
  });

  it('an unknown reason or a missing / unreadable since still shows the paused state', () => {
    render(<HistoryPausedBanner pause={{ quota: 'unknown', since: null }} />);
    const banner = screen.getByTestId('history-paused-banner');
    expect(banner).toHaveTextContent(/reached a cloud limit/i);
    expect(banner).not.toHaveTextContent(/since/i);
  });

  it('ignores an unreadable since date instead of printing "Invalid Date"', () => {
    render(<HistoryPausedBanner pause={{ quota: 'iot', since: 'not-a-date' }} />);
    expect(screen.getByTestId('history-paused-banner')).not.toHaveTextContent(/invalid date/i);
  });
});
