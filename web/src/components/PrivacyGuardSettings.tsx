import { Plus, ShieldCheck, Trash2 } from 'lucide-react';
import { useState } from 'react';

import { usePrivacy, usePrivacyDryRun, useUpdatePrivacy } from '../api/hooks';
import { useI18n } from '../i18n';
import type { PrivacyAction, PrivacyCustomRule, PrivacySettingsDto } from '../api/types';
import { Badge, Button, Field, Group, Input, Row, Spinner, Switch, TextArea } from './ui/controls';
import { errorText, Select, Sheet, useFeedback } from './ui/overlays';
import { PRIVACY_ACTION_KEY, usePrivacyCategoryLabel } from './PrivacyMeta';
import { PRIVACY_RULE_TEMPLATES } from '../lib/privacyTemplates';

const ACTIONS: PrivacyAction[] = ['off', 'warn', 'block', 'redact'];

const ACTION_KEY = PRIVACY_ACTION_KEY;

/** 隐私护栏策略组（plan §6.7）：开关、只检测、响应还原与默认动作。每次修改都 PUT 完整策略。 */
export function PrivacyPolicyGroup() {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const privacy = usePrivacy();
  const update = useUpdatePrivacy();
  const s = privacy.data?.settings;

  const patch = (p: Partial<PrivacySettingsDto>) => {
    if (!s) return;
    update.mutate({ ...s, ...p }, { onError: (e) => toast(errorText(e), 'error') });
  };

  const actionOptions = ACTIONS.map((a) => ({
    value: a,
    label: a === 'off' ? t('settings.privacy.action.off') : t(ACTION_KEY[a]),
  }));

  return (
    <Group title={t('settings.privacy')} footer={t('settings.privacy.footer')}>
      {privacy.isLoading && <Spinner className="my-4" />}
      {privacy.isError && <div className="px-4 py-3 text-[12px] text-[var(--danger)]">{errorText(privacy.error)}</div>}
      {s && (
        <>
          <Row label={t('settings.privacy.enabled')} detail={t('settings.privacy.enabledHint')}>
            <Switch checked={s.enabled} label={t('settings.privacy.enabled')} onChange={(enabled) => patch({ enabled })} />
          </Row>
          <Row label={t('settings.privacy.dryRun')} detail={t('settings.privacy.dryRunHint')}>
            <Switch checked={s.dryRun} disabled={!s.enabled} label={t('settings.privacy.dryRun')} onChange={(dryRun) => patch({ dryRun })} />
          </Row>
          <Row label={t('settings.privacy.restore')} detail={t('settings.privacy.restoreHint')}>
            <Switch checked={s.restoreResponses} disabled={!s.enabled} label={t('settings.privacy.restore')} onChange={(restoreResponses) => patch({ restoreResponses })} />
          </Row>
          <Row label={t('settings.privacy.recordSamples')} detail={t('settings.privacy.recordSamplesHint')}>
            <Switch checked={s.recordSamples} disabled={!s.enabled} label={t('settings.privacy.recordSamples')} onChange={(recordSamples) => patch({ recordSamples })} />
          </Row>
          <Row label={t('settings.privacy.defaultAction')}>
            <Select
              className="w-36"
              ariaLabel={t('settings.privacy.defaultAction')}
              value={s.defaultAction}
              disabled={!s.enabled}
              onChange={(defaultAction) => patch({ defaultAction })}
              options={actionOptions}
            />
          </Row>
        </>
      )}
    </Group>
  );
}

/** 检测规则页：内置类别动作 + 自定义规则管理。 */
export function PrivacyRulesPanel() {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const privacy = usePrivacy();
  const update = useUpdatePrivacy();
  const s = privacy.data?.settings;
  const categoryLabel = usePrivacyCategoryLabel();

  const patch = (p: Partial<PrivacySettingsDto>) => {
    if (!s) return;
    update.mutate({ ...s, ...p }, { onError: (e) => toast(errorText(e), 'error') });
  };
  const setAction = (key: string, action: PrivacyAction) =>
    patch({ categoryActions: { ...s?.categoryActions, [key]: action } });

  const actionOptions = ACTIONS.map((a) => ({
    value: a,
    label: a === 'off' ? t('settings.privacy.action.off') : t(ACTION_KEY[a]),
  }));

  // Built-in rules grouped by category (server order), custom rules listed separately.
  const categories = [...new Set((privacy.data?.rules ?? []).map((r) => r.category))];
  const ruleDescription = (category: string) =>
    privacy.data?.rules.find((r) => r.category === category)?.description;

  if (privacy.isLoading) {
    return (
      <div className="mx-auto max-w-[720px]">
        <Spinner className="my-4" />
      </div>
    );
  }
  if (!s) return null;

  return (
    <div className="mx-auto flex max-w-[720px] flex-col gap-4">
      <Group title={t('settings.privacy.categories')}>
        {categories.map((category) => {
          const current = s.categoryActions[category] ?? s.defaultAction;
          const off = !s.enabled || current === 'off';
          return (
            <Row
              key={category}
              label={categoryLabel(category)}
              detail={ruleDescription(category)}
            >
              {current === 'off' ? (
                <Badge>{t('settings.privacy.action.off')}</Badge>
              ) : (
                <Select
                  className="w-36"
                  ariaLabel={categoryLabel(category)}
                  value={current}
                  disabled={!s.enabled}
                  onChange={(action) => setAction(category, action)}
                  options={actionOptions.filter((o) => o.value !== 'off')}
                />
              )}
              {off && (
                <Button
                  size="sm"
                  variant="plain"
                  disabled={!s.enabled}
                  onClick={() => setAction(category, s.categoryActions[category] ?? s.defaultAction)}
                >
                  {t('settings.privacy.enableCategory')}
                </Button>
              )}
            </Row>
          );
        })}
      </Group>
      <CustomRulesGroup s={s} onPatch={patch} />
    </div>
  );
}

function CustomRulesGroup({ s, onPatch }: { s: PrivacySettingsDto; onPatch: (p: Partial<PrivacySettingsDto>) => void }) {
  const { t } = useI18n();
  const { confirm } = useFeedback();
  const [addOpen, setAddOpen] = useState(false);

  const toggle = (rule: PrivacyCustomRule, enabled: boolean) =>
    onPatch({ customRules: s.customRules.map((r) => (r.id === rule.id ? { ...r, enabled } : r)) });

  const remove = async (rule: PrivacyCustomRule) => {
    if (!(await confirm({ title: t('settings.privacy.deleteRuleConfirm', { name: rule.name }), destructive: true, confirmLabel: t('common.delete') }))) return;
    onPatch({ customRules: s.customRules.filter((r) => r.id !== rule.id) });
  };

  return (
    <Group title={t('settings.privacy.customRules')}>
      {s.customRules.length === 0 && (
        <div className="px-4 py-4 text-[12px] text-[var(--text-secondary)]">{t('settings.privacy.noCustomRules')}</div>
      )}
      {s.customRules.map((rule) => (
        <Row key={rule.id} label={rule.name} detail={rule.pattern}>
          <Switch checked={rule.enabled} disabled={!s.enabled} label={rule.name} onChange={(enabled) => toggle(rule, enabled)} />
          <Button
            size="sm"
            variant="plain"
            icon={<Trash2 className="size-3.5" />}
            aria-label={`${t('common.delete')} ${rule.name}`}
            onClick={() => void remove(rule)}
          />
        </Row>
      ))}
      <div className="px-4 py-2">
        <Button size="sm" icon={<Plus className="size-3.5" />} disabled={!s.enabled} onClick={() => setAddOpen(true)}>
          {t('settings.privacy.addRule')}
        </Button>
      </div>
      <AddRuleSheet open={addOpen} onOpenChange={setAddOpen} onAdd={(rule) => onPatch({ customRules: [...s.customRules, rule] })} />
    </Group>
  );
}

function AddRuleSheet({ open, onOpenChange, onAdd }: { open: boolean; onOpenChange: (o: boolean) => void; onAdd: (rule: PrivacyCustomRule) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const [name, setName] = useState('');
  const [pattern, setPattern] = useState('');
  const [templateId, setTemplateId] = useState('');

  const pickTemplate = (id: string) => {
    setTemplateId(id);
    const tpl = PRIVACY_RULE_TEMPLATES.find((t) => t.id === id);
    if (tpl) {
      setName(t(tpl.nameKey));
      setPattern(tpl.pattern);
    }
  };

  const submit = () => {
    if (!name.trim() || !pattern.trim()) return;
    try {
      new RegExp(pattern); // client-side preview; the server validates again on save
    } catch (e) {
      toast(`${t('settings.privacy.invalidPattern')}: ${errorText(e)}`, 'error');
      return;
    }
    onAdd({ id: `custom-${Date.now().toString(36)}`, name: name.trim(), pattern: pattern.trim(), enabled: true });
    setName('');
    setPattern('');
    setTemplateId('');
    onOpenChange(false);
  };

  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('settings.privacy.addRule')}
      width={480}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!name.trim() || !pattern.trim()} onClick={submit}>
            {t('common.create')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <Field label={t('settings.privacy.ruleTemplates')} hint={t('settings.privacy.ruleTemplatesHint')}>
          <Select
            ariaLabel={t('settings.privacy.ruleTemplates')}
            value={templateId}
            onChange={pickTemplate}
            options={[
              { value: '', label: t('settings.privacy.ruleTemplateNone') },
              ...PRIVACY_RULE_TEMPLATES.map((tpl) => ({ value: tpl.id, label: t(tpl.nameKey) })),
            ]}
          />
        </Field>
        <Input label={t('settings.privacy.ruleName')} value={name} onChange={(e) => setName(e.target.value)} autoFocus />
        <Field label={t('settings.privacy.rulePattern')} hint={t('settings.privacy.rulePatternHint')}>
          <TextArea mono rows={3} value={pattern} onChange={(e) => setPattern(e.target.value)} />
        </Field>
      </div>
    </Sheet>
  );
}

/** 试用检测：按当前策略 dry-run 一段文本（不影响真实请求）。 */
export function DryRunTester() {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const categoryLabel = usePrivacyCategoryLabel();
  const privacy = usePrivacy();
  const dryRun = usePrivacyDryRun();
  const [text, setText] = useState('');
  const result = dryRun.data;
  const enabled = privacy.data?.settings.enabled ?? false;

  return (
    <Group title={t('settings.privacy.tester')} footer={t('settings.privacy.testerHint')}>
      <div className="flex flex-col gap-3 px-4 py-3">
        <TextArea
          aria-label={t('settings.privacy.tester')}
          placeholder={t('settings.privacy.testerPlaceholder')}
          rows={4}
          mono
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        <div>
          <Button
            icon={<ShieldCheck className="size-3.5" />}
            disabled={!enabled || !text.trim()}
            loading={dryRun.isPending}
            onClick={() => dryRun.mutate({ text }, { onError: (e) => toast(errorText(e), 'error') })}
          >
            {t('settings.privacy.runTest')}
          </Button>
        </div>
        {result && (
          <div className="rounded-lg border border-[var(--border)] px-3 py-2 text-[12px]">
            <div className="flex items-center gap-2">
              {result.blocked && <Badge tone="red">{t('settings.privacy.wouldBlock')}</Badge>}
              {result.wouldRedact > 0 && (
                <Badge tone="orange">{t('settings.privacy.wouldRedact', { count: result.wouldRedact })}</Badge>
              )}
              {!result.blocked && result.wouldRedact === 0 && <Badge tone="green">{t('settings.privacy.noHits')}</Badge>}
            </div>
            {result.hits.length > 0 && (
              <ul className="mt-2 space-y-1">
                {result.hits.map((h) => (
                  <li key={`${h.ruleId}:${h.action}`} className="flex flex-wrap items-center gap-2">
                    <Badge tone={h.action === 'block' ? 'red' : h.action === 'redact' ? 'orange' : 'neutral'}>
                      {t(ACTION_KEY[h.action])}
                    </Badge>
                    <span>{categoryLabel(h.category)} · {h.ruleId} × {h.count}</span>
                    {h.samples && h.samples.length > 0 && (
                      <span className="font-mono text-[11px] text-[var(--text-muted)]">{h.samples.join('  ')}</span>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </div>
    </Group>
  );
}
