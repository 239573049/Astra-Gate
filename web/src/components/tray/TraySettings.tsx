import { useQueryClient } from '@tanstack/react-query';
import { RotateCcw } from 'lucide-react';
import type { ReactNode } from 'react';

import { useI18n, type MessageKey } from '../../i18n';
import { TRAY_TITLE_MODES, type TraySection, type TrayTitleMode } from '../../shell/bridge';
import { RadioCards } from '../arc/radio-cards/radio-cards';
import { Button, Row, Switch } from '../ui/controls';
import { TrayPanelView } from './TrayPanelView';
import { DEFAULT_TRAY_PREFS, useTrayPrefs, useTrayState, withSection } from './useTrayBridge';
import { useTrayData } from './useTrayData';

const USAGE_SECTIONS: TraySection[] = ['overview', 'cost', 'chart', 'topModel', 'compareYesterday', 'cacheHitRate', 'quota'];

/**
 * Settings › Tray panel: what the menu bar shows next to the icon, which blocks the panel shows, and a live
 * preview rendered by the panel component itself with real data. Changes are saved by the desktop main
 * process and reach the open panel immediately.
 */
export function TraySettings() {
  const { t } = useI18n();
  const qc = useQueryClient();
  const [prefs, setPrefs] = useTrayPrefs();
  const state = useTrayState();
  const data = useTrayData(prefs, { enabled: state.running, background: false, live: true });

  const section = (key: TraySection) => (
    <Row key={key} label={t(`settings.tray.section.${key}` as MessageKey)} detail={t(`settings.tray.section.${key}.hint` as MessageKey)}>
      <Switch checked={prefs.sections[key]} label={t(`settings.tray.section.${key}` as MessageKey)} onChange={(on) => setPrefs(withSection(prefs, key, on))} />
    </Row>
  );

  return (
    <div className="mx-auto grid max-w-[1120px] items-start gap-4 lg:grid-cols-[minmax(0,1fr)_392px]">
      <div className="flex min-w-0 flex-col gap-4">
        <Card title={t('settings.tray.titleHeading')} hint={t('settings.tray.titleHint')}>
          <div className="p-4">
            <RadioCards
              aria-label={t('settings.tray.titleHeading')}
              minColumnWidth={150}
              value={prefs.title}
              onValueChange={(v) => setPrefs({ ...prefs, title: v as TrayTitleMode })}
              options={TRAY_TITLE_MODES.map((mode) => ({
                value: mode,
                label: t(`settings.tray.title.${mode}` as MessageKey),
                description: t(`settings.tray.title.${mode}.hint` as MessageKey),
              }))}
            />
          </div>
        </Card>

        <Card title={t('settings.tray.usageHeading')}>
          <div className="divide-y divide-[var(--border)]">{USAGE_SECTIONS.map(section)}</div>
        </Card>

        <Card title={t('settings.tray.clientsHeading')}>
          <div className="divide-y divide-[var(--border)]">{section('clients')}</div>
        </Card>

        <div>
          <Button size="sm" variant="plain" icon={<RotateCcw className="size-3.5" />} onClick={() => setPrefs(DEFAULT_TRAY_PREFS)}>
            {t('settings.tray.reset')}
          </Button>
        </div>
      </div>

      <div className="lg:sticky lg:top-0">
        <Card title={t('settings.tray.previewHeading')} hint={t('settings.tray.previewHint')}>
          <div className="flex justify-center bg-[var(--surface-muted)] px-4 py-5">
            <TrayPanelView
              preview
              className="w-[360px] max-w-full shadow-[0_12px_40px_rgb(0_0_0/0.25)]"
              prefs={prefs}
              state={state}
              data={data}
              onRefresh={() => void qc.invalidateQueries({ queryKey: ['stats'] })}
            />
          </div>
          <p className="border-t border-[var(--border)] px-4 py-2.5 text-[11px] text-[var(--text-muted)]">{t('settings.tray.previewFooter')}</p>
        </Card>
      </div>
    </div>
  );
}

function Card({ title, hint, children }: { title: string; hint?: string; children: ReactNode }) {
  const { t } = useI18n();
  return (
    <section className="card">
      <header className="border-b border-[var(--border)] px-4 py-3">
        <div className="text-[11px] text-[var(--text-muted)]">{t('settings.tab.tray')}</div>
        <h3 className="text-[14px] font-semibold">{title}</h3>
        {hint && <p className="mt-0.5 text-[12px] text-[var(--text-secondary)]">{hint}</p>}
      </header>
      {children}
    </section>
  );
}
