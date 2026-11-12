// The v2 side of the Plugins page (design/plugin-v2 01 §6, 07 §3): v2 plugins with their status, permissions, settings
// and activity; the update reminder for v1 plugins that have a v2 version; and, mounted in the shell, runtime
// permission requests and plugin notifications.
import { ArrowUpCircle, Info, Settings2, ShieldAlert, ShieldCheck } from 'lucide-react';
import { Link } from 'react-router';
import { useEffect, useRef, useState } from 'react';

import {
  useAnswerPermissionRequest,
  useDismissSuccessor,
  useGrantPluginV2,
  usePluginNotifications,
  usePluginSuccessors,
  usePluginsV2,
  usePluginV2,
  useRollbackPlugin,
  useSavePluginV2Settings,
  useUpgradePlugin,
} from '../api/hooks';
import { api, enc } from '../api/client';
import type { PluginSuccessor, PluginV2, PluginV2SettingField } from '../api/types';
import { Badge, Button, EmptyState, Field, Spinner, Switch } from '../components/ui/controls';
import { errorText, Menu, Select, Sheet, useFeedback } from '../components/ui/overlays';
import { useI18n, type MessageKey } from '../i18n';
import { cn } from '../lib/cn';
import { formatDateTime } from '../lib/format';
import { PluginView } from './PluginView';
import { Icon } from './render';
import { KeyValueEditorView, TagInputView } from './widgets';

/** A permission in words: the fixed ones have their own text, net: names its origin. */
export function usePermissionText() {
  const { t } = useI18n();
  return (p: string) => {
    if (p.startsWith('net:')) {
      const target = p.slice(4);
      return target.startsWith('{config.')
        ? t('pluginUi.perm.netConfig', { setting: target.slice(8, -1) })
        : t('pluginUi.perm.net', { origin: target });
    }
    const key = `pluginUi.perm.${p}` as MessageKey;
    const text = t(key);
    return text === key ? p : text;
  };
}

function PluginIcon({ icon }: { icon?: string | null }) {
  if (icon && (icon.startsWith('/api/') || icon.startsWith('data:image/')))
    return <img src={icon} alt="" className="size-9 rounded-[10px]" />;
  return (
    <span className="grid size-9 place-items-center rounded-[10px] bg-[var(--accent-subtle)] text-[var(--accent)]">
      <Icon name={icon ?? 'puzzle'} className="size-5" />
    </span>
  );
}

export function PluginsV2Section({ onJob }: { onJob: (id: string) => void }) {
  const { t, locale } = useI18n();
  const list = usePluginsV2(locale);
  const [granting, setGranting] = useState<PluginV2 | null>(null);
  const [settings, setSettings] = useState<PluginV2 | null>(null);
  const [details, setDetails] = useState<string | null>(null);
  const { toast } = useFeedback();
  const rollback = useRollbackPlugin();
  if (list.isLoading) return <Spinner lines={2} />;
  const plugins = list.data?.plugins ?? [];
  return (
    <div className="flex flex-col gap-3">
      <SuccessorBanner onJob={onJob} />
      {plugins.length > 0 && (
        <section className="flex flex-col gap-2">
          <div className="flex items-center justify-between px-1">
            <h3 className="text-[12px] font-medium text-[var(--text-secondary)]">{t('pluginUi.v2Title')}</h3>
            {/* the component gallery (06 §2), for plugin authors: shown while a plugin is loaded from a folder */}
            {plugins.some((p) => p.dev) && (
              <Link to="/plugins/dev/gallery" className="text-[12px] text-[var(--accent)]">
                {t('pluginUi.gallery.title')}
              </Link>
            )}
          </div>
          <div className="card divide-y divide-[var(--border)]">
            {plugins.map((p) => (
              <div key={p.id} className="flex items-start gap-3 px-4 py-3">
                <PluginIcon icon={p.icon} />
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-1.5">
                    <span className="text-[14px] font-medium">{p.name}</span>
                    <span className="font-mono text-[11px] text-[var(--text-muted)]">
                      {p.id} {p.version}
                    </span>
                    <Badge tone="accent">v2</Badge>
                    {p.dev && <Badge tone="orange">{t('pluginUi.dev')}</Badge>}
                    {p.sensitive.length > 0 && (
                      <span title={p.sensitive.join(', ')}>
                        <Badge tone="orange">
                          <ShieldAlert className="mr-1 inline size-3" />
                          {t('pluginUi.sensitive')}
                        </Badge>
                      </span>
                    )}
                  </div>
                  {p.description && (
                    <p className="mt-0.5 text-[12px] text-[var(--text-secondary)]">
                      {p.description}
                    </p>
                  )}
                  <div className="mt-1 flex flex-wrap items-center gap-2 text-[12px]">
                    {p.running ? (
                      <span className="text-[var(--green)]">● {t('pluginUi.running')}</span>
                    ) : p.problem ? (
                      <span className="text-[var(--orange)]">● {p.problem}</span>
                    ) : p.enabled ? (
                      <span className="text-[var(--text-muted)]">● {t('pluginUi.idle')}</span>
                    ) : (
                      <span className="text-[var(--text-muted)]">● {t('pluginUi.off')}</span>
                    )}
                    {p.lastError && (
                      <span className="truncate text-[var(--red)]" title={p.lastError}>
                        {p.lastError}
                      </span>
                    )}
                    {p.contributes.pages.length > 0 && (
                      <span className="text-[var(--text-muted)]">
                        {t('pluginUi.pages', { count: p.contributes.pages.length })}
                      </span>
                    )}
                    {p.contributes.hooks.length > 0 && (
                      <span className="text-[var(--text-muted)]">
                        {t('pluginUi.hooks', { count: p.contributes.hooks.length })}
                      </span>
                    )}
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-1.5">
                  {p.missing.length > 0 && (
                    <Button
                      variant="primary"
                      size="sm"
                      icon={<ShieldCheck className="size-3.5" />}
                      onClick={() => setGranting(p)}
                    >
                      {t('pluginUi.grant')}
                    </Button>
                  )}
                  {p.configuration?.properties.length || p.contributes.settings.length ? (
                    <Button
                      size="sm"
                      icon={<Settings2 className="size-3.5" />}
                      onClick={() => setSettings(p)}
                    >
                      {t('pluginUi.settings')}
                    </Button>
                  ) : null}
                  <Switch
                    checked={p.enabled}
                    label={p.name}
                    onChange={(on) =>
                      void api('PATCH', `/api/plugins?spec=${enc(p.spec)}`, { enabled: on })
                        .then(() => list.refetch())
                        .catch((e) => toast(errorText(e), 'error'))
                    }
                  />
                  <Menu
                    label={p.name}
                    iconOnly
                    items={[
                      {
                        id: 'details',
                        label: t('pluginUi.details'),
                        onSelect: () => setDetails(p.id),
                      },
                      {
                        id: 'permissions',
                        label: t('pluginUi.permissions'),
                        onSelect: () => setGranting(p),
                      },
                      ...(p.replaces.length
                        ? [
                            {
                              id: 'rollback',
                              label: t('pluginUi.rollback'),
                              onSelect: () =>
                                rollback.mutate(p.id, {
                                  onSuccess: (j) => onJob(j.id),
                                  onError: (e) => toast(errorText(e), 'error'),
                                }),
                            },
                          ]
                        : []),
                    ]}
                  />
                </div>
              </div>
            ))}
          </div>
        </section>
      )}
      {granting && <GrantSheet plugin={granting} onClose={() => setGranting(null)} />}
      {settings && <SettingsSheet plugin={settings} onClose={() => setSettings(null)} />}
      {details && <DetailsSheet id={details} onClose={() => setDetails(null)} />}
    </div>
  );
}

function PermissionList({
  permissions,
  sensitive,
}: {
  permissions: string[];
  sensitive: string[];
}) {
  const text = usePermissionText();
  return (
    <ul className="flex flex-col gap-1.5">
      {permissions.map((p) => (
        <li key={p} className="flex items-start gap-2 text-[13px]">
          {sensitive.includes(p) ? (
            <ShieldAlert className="mt-0.5 size-4 shrink-0 text-[var(--orange)]" />
          ) : (
            <ShieldCheck className="mt-0.5 size-4 shrink-0 text-[var(--green)]" />
          )}
          <span>
            {text(p)}
            <span className="ml-1.5 font-mono text-[11px] text-[var(--text-muted)]">{p}</span>
          </span>
        </li>
      ))}
    </ul>
  );
}

function GrantSheet({ plugin, onClose }: { plugin: PluginV2; onClose: () => void }) {
  const { t } = useI18n();
  const grant = useGrantPluginV2();
  const { toast } = useFeedback();
  const asked = plugin.missing.length ? plugin.missing : plugin.permissions;
  return (
    <Sheet
      open
      onOpenChange={(o) => !o && onClose()}
      title={t('pluginUi.grantTitle', { name: plugin.name })}
      description={t('pluginUi.grantDetail')}
      footer={
        plugin.missing.length > 0 ? (
          <>
            <Button onClick={onClose}>{t('common.cancel')}</Button>
            <Button
              variant="primary"
              disabled={grant.isPending}
              onClick={() =>
                grant.mutate(
                  { id: plugin.id, permissions: plugin.missing },
                  { onSuccess: onClose, onError: (e) => toast(errorText(e), 'error') },
                )
              }
            >
              {t('pluginUi.allow')}
            </Button>
          </>
        ) : (
          <Button onClick={onClose}>{t('common.close')}</Button>
        )
      }
    >
      <PermissionList permissions={asked} sensitive={plugin.sensitive} />
      {plugin.missing.length === 0 && plugin.granted.length > 0 && (
        <div className="mt-4 flex flex-col gap-2 border-t border-[var(--border)] pt-3">
          <div className="text-[12px] text-[var(--text-secondary)]">{t('pluginUi.granted')}</div>
          {plugin.granted.map((p) => (
            <div key={p} className="flex items-center justify-between text-[12px]">
              <span className="font-mono">{p}</span>
              <button
                type="button"
                className="text-[var(--red)]"
                onClick={() =>
                  grant.mutate(
                    { id: plugin.id, permissions: [p], revoke: true },
                    { onError: (e) => toast(errorText(e), 'error') },
                  )
                }
              >
                {t('pluginUi.revoke')}
              </button>
            </div>
          ))}
        </div>
      )}
    </Sheet>
  );
}

/** The settings form generated from the configuration schema (02 §9), or the plugin's own settings view. */
function SettingsSheet({ plugin, onClose }: { plugin: PluginV2; onClose: () => void }) {
  const { t } = useI18n();
  const save = useSavePluginV2Settings();
  const { toast } = useFeedback();
  const [values, setValues] = useState<Record<string, unknown>>(plugin.settings);
  const [secrets, setSecrets] = useState<Record<string, string | null>>({});
  const view = plugin.contributes.settings[0];
  const fields = plugin.configuration?.properties ?? [];
  const groups = plugin.configuration?.groups.length
    ? plugin.configuration.groups
    : [{ title: null, properties: fields.map((f) => f.name) }];
  return (
    <Sheet
      open
      width={view ? 640 : 480}
      onOpenChange={(o) => !o && onClose()}
      title={t('pluginUi.settingsTitle', { name: plugin.name })}
      footer={
        fields.length > 0 ? (
          <>
            <Button onClick={onClose}>{t('common.cancel')}</Button>
            <Button
              variant="primary"
              disabled={save.isPending}
              onClick={() =>
                save.mutate(
                  {
                    id: plugin.id,
                    values: Object.fromEntries(
                      Object.entries(values).filter(([k]) =>
                        fields.some((f) => f.name === k && !f.secret),
                      ),
                    ),
                    secrets,
                  },
                  {
                    onSuccess: () => (toast(t('pluginUi.saved'), 'success'), onClose()),
                    onError: (e) => toast(errorText(e), 'error'),
                  },
                )
              }
            >
              {t('pluginUi.save')}
            </Button>
          </>
        ) : undefined
      }
    >
      <div className="flex flex-col gap-5">
        {groups.map((g, gi) => (
          <div key={gi} className="flex flex-col gap-3">
            {g.title && (
              <h3 className="text-[12px] font-medium text-[var(--text-secondary)]">{g.title}</h3>
            )}
            {g.properties
              .map((name) => fields.find((f) => f.name === name))
              .filter((f): f is PluginV2SettingField => Boolean(f))
              .map((f) => (
                <SettingInput
                  key={f.name}
                  field={f}
                  value={values[f.name]}
                  isSet={plugin.secretsSet.includes(f.name)}
                  secret={secrets[f.name]}
                  onChange={(v) => setValues((x) => ({ ...x, [f.name]: v }))}
                  onSecret={(v) => setSecrets((x) => ({ ...x, [f.name]: v }))}
                />
              ))}
          </div>
        ))}
        {view && (
          <PluginView
            target={{ plugin: plugin.id, view: view.view }}
            surface="sheet"
            onClose={onClose}
          />
        )}
      </div>
    </Sheet>
  );
}

function SettingInput({
  field: f,
  value,
  isSet,
  secret,
  onChange,
  onSecret,
}: {
  field: PluginV2SettingField;
  value: unknown;
  isSet: boolean;
  secret: string | null | undefined;
  onChange: (v: unknown) => void;
  onSecret: (v: string | null) => void;
}) {
  const { t } = useI18n();
  const input =
    'h-8 w-full rounded-[var(--radius-control)] border border-[var(--border)] bg-[var(--surface)] px-2 text-[13px]';
  const label = f.title + (f.unit ? ` (${f.unit})` : '') + (f.required ? ' *' : '');
  let control;
  if (f.secret)
    control = (
      <input
        type="password"
        className={input}
        placeholder={isSet ? t('pluginUi.secretSet') : undefined}
        value={secret ?? ''}
        onChange={(e) => onSecret(e.target.value || null)}
        aria-label={f.title}
      />
    );
  else if (f.type === 'boolean')
    control = <Switch checked={value === true} label={f.title} onChange={onChange} />;
  else if (f.enum)
    control =
      f.widget === 'segmented' || f.widget === 'radio' ? (
        <div className="inline-flex rounded-[var(--radius-control)] bg-[var(--surface-muted)] p-0.5">
          {f.enum.map((v, i) => (
            <button
              key={v}
              type="button"
              className={cn(
                'rounded px-3 py-1 text-[12px]',
                value === v
                  ? 'bg-[var(--surface-solid)] shadow-sm'
                  : 'text-[var(--text-secondary)]',
              )}
              onClick={() => onChange(v)}
            >
              {f.enumTitles?.[i] ?? v}
            </button>
          ))}
        </div>
      ) : (
        <Select
          value={typeof value === 'string' ? value : null}
          ariaLabel={f.title}
          options={f.enum.map((v, i) => ({ value: v, label: f.enumTitles?.[i] ?? v }))}
          onChange={onChange}
        />
      );
  else if (f.type === 'number' || f.type === 'integer')
    control = (
      <input
        type="number"
        className={input}
        min={f.minimum ?? undefined}
        max={f.maximum ?? undefined}
        step={f.type === 'integer' ? 1 : 'any'}
        value={typeof value === 'number' ? value : ''}
        onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))}
        aria-label={f.title}
      />
    );
  else if (f.type === 'array' && (f.widget === 'tags' || f.itemsType === 'string'))
    control = (
      <TagInputView value={Array.isArray(value) ? (value as string[]) : []} onChange={onChange} />
    );
  else if (f.type === 'object' && f.widget === 'keyvalue')
    control = (
      <KeyValueEditorView
        value={Object.entries((value as Record<string, string>) ?? {}).map(([key, v]) => ({
          key,
          value: String(v),
        }))}
        onChange={(rows) =>
          onChange(Object.fromEntries(rows.filter((r) => r.key).map((r) => [r.key, r.value])))
        }
        addLabel={t('pluginUi.add')}
      />
    );
  else if (f.type === 'array' || f.type === 'object' || f.widget === 'code')
    control = (
      <textarea
        className={cn(input, 'h-auto py-1.5 font-mono text-[12px]')}
        rows={5}
        defaultValue={
          typeof value === 'string'
            ? value
            : JSON.stringify(value ?? (f.type === 'array' ? [] : {}), null, 2)
        }
        onChange={(e) => {
          try {
            onChange(f.type === 'string' ? e.target.value : JSON.parse(e.target.value));
          } catch {
            // keep the last valid value until the JSON parses
          }
        }}
        aria-label={f.title}
      />
    );
  else if (f.widget === 'textarea')
    control = (
      <textarea
        className={cn(input, 'h-auto py-1.5')}
        rows={4}
        value={String(value ?? '')}
        onChange={(e) => onChange(e.target.value)}
        aria-label={f.title}
      />
    );
  else
    control = (
      <input
        className={input}
        value={String(value ?? '')}
        pattern={f.pattern ?? undefined}
        onChange={(e) => onChange(e.target.value)}
        aria-label={f.title}
      />
    );
  return (
    <Field label={label} hint={f.description ?? undefined}>
      {control}
    </Field>
  );
}

function DetailsSheet({ id, onClose }: { id: string; onClose: () => void }) {
  const { t, locale } = useI18n();
  const d = usePluginV2(id, locale);
  const p = d.data?.plugin;
  return (
    <Sheet open width={640} onOpenChange={(o) => !o && onClose()} title={p?.name ?? id}>
      {!d.data ? (
        <Spinner lines={4} />
      ) : (
        <div className="flex flex-col gap-4 text-[13px]">
          <div className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-[12px]">
            <span className="text-[var(--text-muted)]">ID</span>
            <span className="font-mono">{p!.id}</span>
            <span className="text-[var(--text-muted)]">{t('pluginUi.version')}</span>
            <span>{p!.version}</span>
            {p!.publisher && (
              <>
                <span className="text-[var(--text-muted)]">{t('pluginUi.publisher')}</span>
                <span>{p!.publisher}</span>
              </>
            )}
            <span className="text-[var(--text-muted)]">{t('pluginUi.source')}</span>
            <span className="truncate font-mono">{p!.spec}</span>
          </div>
          {p!.contributes.hooks.length + p!.contributes.jobs.length > 0 && (
            <div>
              <h4 className="mb-1 text-[12px] font-medium text-[var(--text-secondary)]">
                {t('pluginUi.contributes')}
              </h4>
              <ul className="font-mono text-[12px]">
                {[...p!.contributes.hooks, ...p!.contributes.jobs].map((h) => (
                  <li key={h}>{h}</li>
                ))}
              </ul>
            </div>
          )}
          {Object.keys(p!.counters).length > 0 && (
            <div className="flex flex-wrap gap-3">
              {Object.entries(p!.counters).map(([k, v]) => (
                <span key={k} className="rounded bg-[var(--surface-muted)] px-2 py-1 text-[12px]">
                  {k}: <strong className="tabular-nums">{v}</strong>
                </span>
              ))}
            </div>
          )}
          {p!.warnings.length > 0 && (
            <ul className="text-[12px] text-[var(--orange)]">
              {p!.warnings.map((w) => (
                <li key={w}>{w}</li>
              ))}
            </ul>
          )}
          <div>
            <h4 className="mb-1 text-[12px] font-medium text-[var(--text-secondary)]">
              {t('pluginUi.activity')}
            </h4>
            {d.data.activity.length === 0 ? (
              <p className="text-[12px] text-[var(--text-muted)]">{t('pluginUi.noActivity')}</p>
            ) : (
              <table className="w-full text-[12px]">
                <tbody>
                  {d.data.activity.slice(0, 100).map((a, i) => (
                    <tr key={i} className="border-b border-[var(--border)] last:border-0">
                      <td className="py-1 pr-3 text-[var(--text-muted)]">
                        {formatDateTime(a.at, locale)}
                      </td>
                      <td className="py-1 pr-3 font-mono">{a.call}</td>
                      <td className="max-w-[16rem] truncate py-1 pr-3 font-mono">{a.target}</td>
                      <td className="py-1">{a.outcome}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
          <div>
            <h4 className="mb-1 text-[12px] font-medium text-[var(--text-secondary)]">
              {t('pluginUi.log')}
            </h4>
            <div className="max-h-64 overflow-auto rounded bg-[var(--surface-muted)] p-2 font-mono text-[11px]">
              {d.data.lines.length === 0 && <span className="text-[var(--text-muted)]">—</span>}
              {d.data.lines.map((l, i) => (
                <div
                  key={i}
                  style={{
                    color:
                      l.level === 'error'
                        ? 'var(--red)'
                        : l.level === 'warn'
                          ? 'var(--orange)'
                          : undefined,
                  }}
                >
                  {l.at.slice(11, 19)} {l.text}
                </div>
              ))}
            </div>
          </div>
        </div>
      )}
    </Sheet>
  );
}

/** "N plugins have a v2 version": the update reminder (07 §3.2), one card per successor. */
export function SuccessorBanner({ onJob }: { onJob: (id: string) => void }) {
  const { t, locale } = useI18n();
  const successors = usePluginSuccessors(locale);
  const dismiss = useDismissSuccessor();
  const [upgrading, setUpgrading] = useState<PluginSuccessor | null>(null);
  const list = successors.data ?? [];
  if (!list.length) return null;
  return (
    <section className="rounded-[var(--radius-panel)] border border-[var(--accent)] bg-[var(--accent-subtle)] p-4">
      <div className="mb-2 flex items-center gap-2 text-[13px] font-medium">
        <ArrowUpCircle className="size-4 text-[var(--accent)]" />
        {t('pluginUi.successors', { count: list.length })}
      </div>
      <div className="flex flex-col gap-2">
        {list.map((s) => (
          <div key={s.spec} className="flex flex-wrap items-center gap-2 text-[13px]">
            <span className="font-medium">{s.v1Name}</span>
            <span className="text-[var(--text-muted)]">→</span>
            <span>
              {s.name}{' '}
              <span className="font-mono text-[11px] text-[var(--text-muted)]">v{s.version}</span>
            </span>
            <span className="ml-auto flex gap-1.5">
              <Button size="sm" variant="primary" onClick={() => setUpgrading(s)}>
                {t('pluginUi.upgrade')}
              </Button>
              <Button
                size="sm"
                variant="plain"
                onClick={() => dismiss.mutate({ spec: s.spec, version: s.version })}
              >
                {t('pluginUi.dismiss')}
              </Button>
            </span>
          </div>
        ))}
      </div>
      {upgrading && (
        <UpgradeSheet successor={upgrading} onClose={() => setUpgrading(null)} onJob={onJob} />
      )}
    </section>
  );
}

function UpgradeSheet({
  successor: s,
  onClose,
  onJob,
}: {
  successor: PluginSuccessor;
  onClose: () => void;
  onJob: (id: string) => void;
}) {
  const { t } = useI18n();
  const upgrade = useUpgradePlugin();
  const { toast } = useFeedback();
  return (
    <Sheet
      open
      onOpenChange={(o) => !o && onClose()}
      title={t('pluginUi.upgradeTitle', { name: s.v1Name })}
      description={t('pluginUi.upgradeDetail')}
      footer={
        <>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button
            variant="primary"
            disabled={upgrade.isPending}
            onClick={() =>
              upgrade.mutate(
                { spec: s.spec, grant: s.permissions },
                {
                  onSuccess: (j) => (onJob(j.id), onClose()),
                  onError: (e) => toast(errorText(e), 'error'),
                },
              )
            }
          >
            {t('pluginUi.upgradeConfirm')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {s.notes && (
          <p className="whitespace-pre-wrap text-[13px] text-[var(--text-secondary)]">{s.notes}</p>
        )}
        <div>
          <h4 className="mb-2 text-[12px] font-medium text-[var(--text-secondary)]">
            {t('pluginUi.upgradePermissions')}
          </h4>
          <PermissionList permissions={s.permissions} sensitive={s.sensitive} />
        </div>
        <p className="flex items-start gap-2 text-[12px] text-[var(--text-muted)]">
          <Info className="mt-0.5 size-3.5 shrink-0" />
          {t('pluginUi.upgradeSafety')}
        </p>
      </div>
    </Sheet>
  );
}

/** Runtime permission requests (permissions.request): the host's own dialog, never the plugin's. Mounted in the shell. */
export function PluginPermissionRequests() {
  const { t, locale } = useI18n();
  const list = usePluginsV2(locale);
  const answer = useAnswerPermissionRequest();
  const request = list.data?.requests[0];
  if (!request) return null;
  return (
    <Sheet
      open
      onOpenChange={(o) => !o && answer.mutate({ id: request.id, allow: false })}
      title={t('pluginUi.requestTitle', { name: request.pluginName })}
      description={t('pluginUi.requestDetail')}
      footer={
        <>
          <Button onClick={() => answer.mutate({ id: request.id, allow: false })}>
            {t('pluginUi.deny')}
          </Button>
          <Button variant="primary" onClick={() => answer.mutate({ id: request.id, allow: true })}>
            {t('pluginUi.allow')}
          </Button>
        </>
      }
    >
      <PermissionList
        permissions={request.permissions}
        sensitive={request.permissions.filter(
          (p) => p.startsWith('net:') || p.startsWith('gateway.'),
        )}
      />
    </Sheet>
  );
}

/** Shows notifications plugins send (notifications.send) as toasts, each once. Mounted in the shell. */
export function PluginNotifier({ enabled }: { enabled: boolean }) {
  const { toast } = useFeedback();
  const [after, setAfter] = useState<number | null>(null);
  const first = useRef(true);
  const n = usePluginNotifications(after ?? 0, enabled);
  useEffect(() => {
    const items = n.data ?? [];
    if (!items.length) {
      if (after === null) setAfter(0);
      return;
    }
    const last = items[items.length - 1]!.id;
    // what was sent before this window opened is history, not news
    if (!first.current)
      for (const x of items)
        toast(
          `${x.title}${x.body ? ' — ' + x.body : ''}`,
          x.tone === 'danger' ? 'error' : x.tone === 'success' ? 'success' : 'info',
        );
    first.current = false;
    if (last !== after) setAfter(last);
  }, [n.data, after, toast]);
  return null;
}

export function V2Empty() {
  const { t } = useI18n();
  return <EmptyState title={t('pluginUi.noV2')} />;
}
