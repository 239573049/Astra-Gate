import { ArrowDown, ArrowUp, Copy, Plus, Trash2 } from 'lucide-react';
import { useEffect, useRef, useState, type ComponentProps } from 'react';

import { ApiError } from '../api/client';
import { useRoutingModels, useSmartRouting, useTestSmartRouting, useUpdateSmartRouting } from '../api/hooks';
import type {
  JudgeProtocol,
  RoutingMode,
  RoutingProblem,
  SmartRouting,
  SmartRoutingGrade,
  SmartRoutingJudgeUpdate,
  SmartRoutingTestResult,
  SmartRoutingUpdate,
} from '../api/types';
import { useI18n, type MessageKey, type TFunction } from '../i18n';
import { zh } from '../i18n/messages';
import { formatTime } from '../lib/format';
import { systemActions } from '../shell/systemActions';
import { Alert } from './arc/alert/alert';
import { clientName } from './icons';
import { Badge, Button, Disclosure, Dot, Group, Input, NumberInput, Row, Segmented, Spinner, Switch, TextArea } from './ui/controls';
import { errorText, Select, useFeedback } from './ui/overlays';

const MIN_GRADES = 2;
const MAX_GRADES = 6;

/**
 * A settings row whose hint wraps onto as many lines as it needs. The shared `Row` cuts its detail off at one line,
 * which is too little for the sentences on this page (what a rule does and costs).
 */
function HintRow({ detail, ...rest }: ComponentProps<typeof Row>) {
  return <Row {...rest} detail={detail === undefined || detail === null ? undefined : <span className="block whitespace-normal">{detail}</span>} />;
}

/** Hosts that serve a judge dialect under their own address, so choosing one fills in the address and the model. */
interface Preset {
  id: string;
  baseUrl: string;
  model?: string;
}

const PRESETS: Record<JudgeProtocol, Preset[]> = {
  typesafe: [
    { id: 'typesafe', baseUrl: '', model: 'jev-latest' },
    { id: 'openrouter', baseUrl: 'https://openrouter.ai/api', model: '~typesafe/jev-latest' },
    { id: 'vercel', baseUrl: 'https://ai-gateway.vercel.sh/typesafe', model: 'typesafe-ai/jev' },
  ],
  openai: [{ id: 'openai', baseUrl: '' }],
};

/** A grade's choice in the model picker: one model on one provider (provider ids and model ids never hold a line break). */
const choiceOf = (providerId: string | null | undefined, model: string) => `${providerId ?? ''}\n${model}`;

function presetOf(protocol: JudgeProtocol, baseUrl: string): string {
  const url = baseUrl.trim().replace(/\/+$/, '');
  return PRESETS[protocol].find((p) => p.baseUrl === url)?.id ?? 'custom';
}

/** Scheme, host and port of the address the judge is reached at: what decides who receives its key. */
function originOf(baseUrl: string, protocol: JudgeProtocol, protocols: SmartRouting['protocols']): string {
  const url = baseUrl.trim() || (protocols.find((p) => p.id === protocol)?.defaultBaseUrl ?? '');
  try {
    return new URL(url).origin.toLowerCase();
  } catch {
    return url;
  }
}

/** The page's form: what is on screen, before it is saved. Numbers are null while their field is being retyped. */
interface Draft {
  virtualModel: string;
  protocol: JudgeProtocol;
  baseUrl: string;
  /** Write-only: empty means "keep the saved key". */
  apiKey: string;
  model: string;
  instructions: string;
  timeoutMs: number | null;
  grades: SmartRoutingGrade[];
  mode: RoutingMode;
  sessionTtlMinutes: number | null;
  fallbackGrade: string;
  minConfidence: number | null;
  contextTurns: number | null;
  maxChars: number | null;
}

function draftOf(s: SmartRouting): Draft {
  return {
    virtualModel: s.virtualModel,
    protocol: s.judge.protocol,
    baseUrl: s.judge.baseUrl ?? '',
    apiKey: '',
    model: s.judge.model,
    instructions: s.judge.instructions ?? '',
    timeoutMs: s.judge.timeoutMs,
    grades: s.grades.map((g) => ({ ...g })),
    mode: s.rules.mode,
    sessionTtlMinutes: s.rules.sessionTtlMinutes,
    fallbackGrade: s.rules.fallbackGrade,
    minConfidence: s.rules.minConfidence,
    contextTurns: s.rules.contextTurns,
    maxChars: s.rules.maxChars,
  };
}

const filled = (d: Draft) => [d.timeoutMs, d.sessionTtlMinutes, d.minConfidence, d.contextTurns, d.maxChars].every((v) => v !== null);

function judgeOf(d: Draft): SmartRoutingJudgeUpdate {
  return {
    protocol: d.protocol,
    baseUrl: d.baseUrl,
    model: d.model,
    instructions: d.instructions,
    ...(d.timeoutMs !== null && { timeoutMs: d.timeoutMs }),
    ...(d.apiKey !== '' && { apiKey: d.apiKey }),
  };
}

function updateOf(d: Draft): SmartRoutingUpdate {
  return {
    virtualModel: d.virtualModel,
    judge: judgeOf(d),
    grades: d.grades,
    rules: {
      mode: d.mode,
      fallbackGrade: d.fallbackGrade,
      ...(d.sessionTtlMinutes !== null && { sessionTtlMinutes: d.sessionTtlMinutes }),
      ...(d.minConfidence !== null && { minConfidence: d.minConfidence }),
      ...(d.contextTurns !== null && { contextTurns: d.contextTurns }),
      ...(d.maxChars !== null && { maxChars: d.maxChars }),
    },
  };
}

/** One refusal in the UI's language; the server's English sentence when the code is one this build does not know. */
function problemText(p: RoutingProblem, d: Draft, t: TFunction): string {
  const key = `smartRouting.problem.${p.code}`;
  if (!(key in zh)) return p.message;
  const at = /^grades\[(\d+)\]/.exec(p.field ?? '');
  const grade = at ? d.grades[Number(at[1])] : undefined;
  return t(key as MessageKey, { grade: grade ? grade.name || grade.id : '', field: p.field ?? '' });
}

/** What went wrong, line by line: every problem of a refused save, or the error's own words. */
function explain(error: unknown, d: Draft, t: TFunction): string[] {
  if (error instanceof ApiError) {
    const problems = (error.details as { problems?: RoutingProblem[] } | undefined)?.problems;
    if (Array.isArray(problems) && problems.length > 0) return problems.map((p) => problemText(p, d, t));
  }
  return [errorText(error)];
}

/** The message key of why a caller cannot use a grade (a GradeProblem); null for a reason this build does not know. */
function unusableKey(reason: string | null | undefined): MessageKey | null {
  const key = `smartRouting.unusable.${reason}`;
  return reason && key in zh ? (key as MessageKey) : null;
}

/** Why a decision fell back (a failure kind, low_confidence, breaker …) in the UI's language; as it is when this build does not know it. */
function reasonText(reason: string | null | undefined, t: TFunction): string {
  const key = `smartRouting.failure.${reason}`;
  return key in zh ? t(key as MessageKey) : (reason ?? '');
}

/**
 * Smart routing: a small judge model reads a request, and it is sent to the model of the grade that fits. Three steps —
 * the judge, the grades and their models, the rules (once per session, or for every new question) — and a switch that is
 * refused until all of them are complete. The switch saves the page's unsaved changes along with itself.
 */
export function SmartRoutingSettings() {
  const { t } = useI18n();
  const q = useSmartRouting();
  if (!q.data) {
    return (
      <div className="mx-auto max-w-[760px]">
        {q.isError ? (
          <Alert tone="danger" title={t('smartRouting.loadFailed')}>
            {errorText(q.error)}
          </Alert>
        ) : (
          <Spinner className="mx-auto my-8" />
        )}
      </div>
    );
  }
  return <SmartRoutingForm saved={q.data} />;
}

function SmartRoutingForm({ saved }: { saved: SmartRouting }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const update = useUpdateSmartRouting();
  const [draft, setDraft] = useState<Draft>(() => draftOf(saved));
  const [problems, setProblems] = useState<string[]>([]);

  const patch = (change: Partial<Draft>) => setDraft((d) => ({ ...d, ...change }));
  const dirty = JSON.stringify(draft) !== JSON.stringify(draftOf(saved));
  const complete = filled(draft);

  const save = (enabled?: boolean) =>
    update.mutate(
      { ...updateOf(draft), ...(enabled !== undefined && { enabled }) },
      {
        onSuccess: (next) => {
          setProblems([]);
          setDraft(draftOf(next));
          toast(enabled === undefined ? t('common.saved') : enabled ? t('smartRouting.toast.on') : t('smartRouting.toast.off'));
        },
        onError: (e) => setProblems(explain(e, draft, t)),
      },
    );

  return (
    <div className="mx-auto max-w-[760px]">
      <StatusGroup saved={saved} busy={update.isPending} canSave={complete} onToggle={(on) => save(on)} problems={problems} />
      <JudgeGroup saved={saved} draft={draft} patch={patch} />
      <GradesGroup draft={draft} patch={patch} />
      <RulesGroup draft={draft} patch={patch} />
      {!complete && <p className="mb-3 px-1 text-[12px] text-[var(--warning)]">{t('smartRouting.numberMissing')}</p>}
      <div className="flex items-center justify-end gap-2">
        {dirty && (
          <Button variant="plain" onClick={() => setDraft(draftOf(saved))}>
            {t('smartRouting.discard')}
          </Button>
        )}
        <Button variant="primary" disabled={!dirty || !complete} loading={update.isPending} onClick={() => save()}>
          {t('common.save')}
        </Button>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------- status and the switch

function StatusGroup({
  saved,
  busy,
  canSave,
  onToggle,
  problems,
}: {
  saved: SmartRouting;
  busy: boolean;
  canSave: boolean;
  onToggle: (on: boolean) => void;
  problems: string[];
}) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const { status } = saved;
  const copy = async (text: string) => {
    if (await systemActions.copy(text)) toast(t('common.copied'));
  };
  // The Save button is at the bottom of a long page and the reasons are up here: bring them into view when a save fails.
  const reasons = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (problems.length > 0) reasons.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }, [problems]);

  return (
    <Group title={t('smartRouting.status')} footer={t('smartRouting.statusFooter')}>
      <HintRow
        label={t('smartRouting.enable')}
        detail={
          <span className="inline-flex items-center gap-1.5">
            <Dot tone={saved.enabled ? (status.breakerOpen ? 'orange' : 'green') : 'neutral'} />
            {saved.enabled
              ? status.breakerOpen
                ? t('smartRouting.state.breaker')
                : `${t('smartRouting.state.on')}${status.pins > 0 ? ` · ${t('smartRouting.state.pins', { count: status.pins })}` : ''}`
              : t('smartRouting.state.off')}
          </span>
        }
      >
        <Switch checked={saved.enabled} label={t('smartRouting.enable')} disabled={busy || !canSave} onChange={onToggle} />
      </HintRow>
      {saved.enabled && (
        <HintRow label={t('smartRouting.use')} detail={t('smartRouting.useHint')}>
          <span className="font-mono text-[12px] selectable">{saved.virtualModel}</span>
          <Button size="sm" variant="plain" icon={<Copy className="size-3.5" />} aria-label={t('common.copy')} onClick={() => void copy(saved.virtualModel)} />
        </HintRow>
      )}
      {saved.enabled && <RecentDecisions saved={saved} />}
      {problems.length > 0 && (
        <div ref={reasons} className="px-3 py-2">
          <Alert tone="danger" title={t('smartRouting.problems')}>
            <ul className="list-disc space-y-0.5 pl-4">
              {problems.map((line, i) => (
                <li key={`${i}-${line}`}>{line}</li>
              ))}
            </ul>
          </Alert>
        </div>
      )}
    </Group>
  );
}

/**
 * What the last fresh decisions came to. A fallback never fails a request, so without this nobody sees it: the request
 * just lands on the fallback grade. Warns when most decisions fell back (with what to check for the commonest reason)
 * and when a caller could not reach a grade's model and was given another grade.
 */
function RecentDecisions({ saved }: { saved: SmartRouting }) {
  const { t, locale } = useI18n();
  const recent = saved.status.recent ?? { total: 0, outcomes: {} };
  const unreachable = saved.status.unreachable ?? [];
  const gradeName = (id: string) => {
    const g = saved.grades.find((x) => x.id === id);
    return g ? g.name || g.id : id;
  };
  const judged = recent.outcomes.judge ?? 0;
  const fallbacks = Object.entries(recent.outcomes)
    .filter(([outcome]) => outcome !== 'judge')
    .sort((a, b) => b[1] - a[1]);
  const fell = recent.total - judged;
  const summary = [
    t('smartRouting.recent.judged', { count: judged, total: recent.total }),
    ...fallbacks.map(([outcome, count]) => t('smartRouting.recent.fallback', { reason: reasonText(outcome, t), count })),
    ...(recent.medianJudgeMs != null ? [t('smartRouting.recent.median', { ms: recent.medianJudgeMs })] : []),
  ].join(' · ');
  const commonest = fallbacks[0]?.[0];
  const hint = `smartRouting.recent.hint.${commonest}`;

  return (
    <>
      <HintRow label={t('smartRouting.recent')} detail={recent.total === 0 ? t('smartRouting.recent.none') : summary} />
      {fell >= 2 && fell * 2 > recent.total && (
        <div className="px-3 py-2">
          <Alert tone="warning" title={t('smartRouting.recent.failing', { grade: gradeName(saved.rules.fallbackGrade) })}>
            <p>{t((hint in zh ? hint : 'smartRouting.recent.hint.other') as MessageKey)}</p>
            {recent.lastFallbackAt && (
              <p className="mt-1 text-[var(--text-muted)]">
                {t('smartRouting.recent.last', { reason: reasonText(recent.lastFallback, t), time: formatTime(recent.lastFallbackAt, locale) })}
              </p>
            )}
          </Alert>
        </div>
      )}
      {unreachable.length > 0 && (
        <div className="px-3 py-2">
          <Alert tone="warning" title={t('smartRouting.unreachable.title')}>
            <ul className="list-disc space-y-1.5 pl-4">
              {unreachable.map((u) => {
                const reason = unusableKey(u.reason);
                const when = { count: u.count, time: formatTime(u.lastAt, locale) };
                return (
                  <li key={`${u.caller}\n${u.grade}`}>
                    <div>
                      {t('smartRouting.unreachable.item', {
                        caller: clientName(u.caller),
                        grade: gradeName(u.grade),
                        where: u.providerName ? t('smartRouting.unreachable.where', { provider: u.providerName, model: u.model }) : u.model,
                        reason: t(reason ?? 'smartRouting.unusable.unknown'),
                      })}{' '}
                      {u.usedGrade ? t('smartRouting.unreachable.used', { ...when, used: gradeName(u.usedGrade) }) : t('smartRouting.unreachable.none', when)}
                    </div>
                    {reason && <div className="text-[var(--text-muted)]">{t(`${reason}.fix` as MessageKey)}</div>}
                  </li>
                );
              })}
            </ul>
            {unreachable.some((u) => !unusableKey(u.reason)) && <p className="mt-1">{t('smartRouting.unreachable.hint')}</p>}
          </Alert>
        </div>
      )}
    </>
  );
}

// ---------------------------------------------------------------------------- ① the judge

function JudgeGroup({ saved, draft, patch }: { saved: SmartRouting; draft: Draft; patch: (change: Partial<Draft>) => void }) {
  const { t } = useI18n();
  const preset = presetOf(draft.protocol, draft.baseUrl);
  const keyDropped =
    saved.judge.hasApiKey &&
    draft.apiKey === '' &&
    originOf(draft.baseUrl, draft.protocol, saved.protocols) !== originOf(saved.judge.baseUrl ?? '', saved.judge.protocol, saved.protocols);

  const changeProtocol = (protocol: JudgeProtocol) =>
    // Another dialect starts from its own address and model, and from no key — the same as the server does.
    patch({ protocol, baseUrl: '', apiKey: '', model: saved.protocols.find((p) => p.id === protocol)?.defaultModel ?? '' });

  const choosePreset = (id: string) => {
    if (id === 'custom') return patch({ baseUrl: draft.baseUrl || 'https://' });
    const p = PRESETS[draft.protocol].find((x) => x.id === id);
    if (p) patch({ baseUrl: p.baseUrl, ...(p.model !== undefined && { model: p.model }) });
  };

  return (
    <Group title={t('smartRouting.judge.title')} footer={t('smartRouting.judge.footer')}>
      <HintRow label={t('smartRouting.judge.protocol')} detail={t(`smartRouting.judge.protocol.${draft.protocol}.hint` as MessageKey)}>
        <Segmented
          ariaLabel={t('smartRouting.judge.protocol')}
          value={draft.protocol}
          onChange={changeProtocol}
          items={[
            { value: 'typesafe', label: t('smartRouting.judge.protocol.typesafe') },
            { value: 'openai', label: t('smartRouting.judge.protocol.openai') },
          ]}
        />
      </HintRow>
      {draft.protocol === 'openai' && (
        <div className="px-3 py-2">
          <Alert tone="warning" title={t('smartRouting.judge.openaiDraft')} />
        </div>
      )}
      <HintRow label={t('smartRouting.judge.service')}>
        <Select
          className="w-52"
          ariaLabel={t('smartRouting.judge.service')}
          value={preset}
          onChange={choosePreset}
          options={[
            ...PRESETS[draft.protocol].map((p) => ({ value: p.id, label: t(`smartRouting.preset.${p.id}` as MessageKey) })),
            { value: 'custom', label: t('smartRouting.preset.custom') },
          ]}
        />
      </HintRow>
      {preset === 'custom' && (
        <HintRow label={t('smartRouting.judge.baseUrl')} detail={t('smartRouting.judge.baseUrlHint')}>
          <Input
            className="w-72"
            mono
            aria-label={t('smartRouting.judge.baseUrl')}
            placeholder="https://…"
            value={draft.baseUrl}
            onChange={(e) => patch({ baseUrl: e.target.value })}
          />
        </HintRow>
      )}
      <HintRow
        label={t('smartRouting.judge.apiKey')}
        detail={
          keyDropped
            ? t('smartRouting.judge.keyReplace')
            : saved.judge.hasApiKey
              ? t('smartRouting.judge.keySaved', { mask: saved.judge.apiKeyMasked ?? '' })
              : t('smartRouting.judge.keyMissing')
        }
      >
        <Input
          className="w-72"
          type="password"
          autoComplete="off"
          mono
          aria-label={t('smartRouting.judge.apiKey')}
          placeholder={saved.judge.hasApiKey && !keyDropped ? '••••••••' : 'sk-…'}
          value={draft.apiKey}
          onChange={(e) => patch({ apiKey: e.target.value })}
        />
      </HintRow>
      {keyDropped && (
        <div className="px-3 py-2">
          <Alert tone="warning" title={t('smartRouting.judge.keyDropped')} />
        </div>
      )}
      <HintRow label={t('smartRouting.judge.model')} detail={t('smartRouting.judge.modelHint')}>
        <Input
          className="w-72"
          mono
          aria-label={t('smartRouting.judge.model')}
          placeholder={saved.protocols.find((p) => p.id === draft.protocol)?.defaultModel || 'model'}
          value={draft.model}
          onChange={(e) => patch({ model: e.target.value })}
        />
      </HintRow>
      <HintRow label={t('smartRouting.judge.timeout')} detail={t('smartRouting.judge.timeoutHint')}>
        <NumberInput className="w-28" label={t('smartRouting.judge.timeout')} step={100} min={200} value={draft.timeoutMs} onChange={(v) => patch({ timeoutMs: v })} />
      </HintRow>
      <div className="space-y-2 px-3 py-2">
        <Alert tone="info" title={t('smartRouting.privacy.title')}>
          {t('smartRouting.privacy.body', { turns: draft.contextTurns ?? 0, chars: draft.maxChars ?? 0 })}
        </Alert>
        {draft.protocol === 'typesafe' && <Alert tone="warning" title={t('smartRouting.judge.nonEnglish')} />}
        <Disclosure title={t('smartRouting.judge.instructions')}>
          <p className="mb-2 text-[11px] text-[var(--text-muted)]">{t('smartRouting.judge.instructionsHint')}</p>
          <TextArea
            rows={4}
            aria-label={t('smartRouting.judge.instructions')}
            placeholder={saved.defaultInstructions}
            value={draft.instructions}
            onChange={(e) => patch({ instructions: e.target.value })}
          />
        </Disclosure>
      </div>
      <Tester draft={draft} />
    </Group>
  );
}

/** Judges a sample text with the settings as they are on screen (unsaved), to see what the judge would choose. */
function Tester({ draft }: { draft: Draft }) {
  const { t } = useI18n();
  const run = useTestSmartRouting();
  const [text, setText] = useState('');
  const [result, setResult] = useState<SmartRoutingTestResult | null>(null);
  const [errors, setErrors] = useState<string[]>([]);

  const test = () => {
    setResult(null);
    setErrors([]);
    run.mutate(
      { text, judge: judgeOf(draft), grades: draft.grades },
      { onSuccess: setResult, onError: (e) => setErrors(explain(e, draft, t)) },
    );
  };

  // The test waits longer than a request would: an answer slower than that is shown, but a request would have fallen back.
  const slow = result !== null && result.latencyMs > result.limitMs;

  return (
    <div className="space-y-2 px-4 py-3">
      <div className="text-[13px]">{t('smartRouting.test.title')}</div>
      <p className="text-[11px] text-[var(--text-muted)]">{t('smartRouting.test.hint')}</p>
      <TextArea rows={3} aria-label={t('smartRouting.test.title')} placeholder={t('smartRouting.test.placeholder')} value={text} onChange={(e) => setText(e.target.value)} />
      <div className="flex justify-end">
        <Button size="sm" disabled={text.trim() === ''} loading={run.isPending} onClick={test}>
          {t('smartRouting.test.run')}
        </Button>
      </div>
      {errors.length > 0 && (
        <Alert tone="danger" title={t('smartRouting.problems')}>
          <ul className="list-disc space-y-0.5 pl-4">
            {errors.map((line, i) => (
              <li key={`${i}-${line}`}>{line}</li>
            ))}
          </ul>
        </Alert>
      )}
      {result &&
        (result.ok ? (
          <Alert tone={result.confident && !slow ? 'success' : 'warning'} title={t('smartRouting.test.result', { grade: result.gradeName ?? result.grade ?? '' })}>
            <div className="space-y-0.5 text-[12px]">
              <div>{result.model ? t('smartRouting.test.model', { model: result.model }) : t('smartRouting.test.noModel')}</div>
              <div>
                {t('smartRouting.test.confidence', { percent: Math.round((result.confidence ?? 0) * 100) })} ·{' '}
                {result.confident ? t('smartRouting.test.confident') : t('smartRouting.test.notConfident')}
              </div>
              {slow && (
                <div className="text-[var(--warning)]">
                  {t('smartRouting.test.slow', { ms: result.latencyMs, limit: result.limitMs })}
                  {result.newConnection && ` ${t('smartRouting.test.connecting')}`}
                </div>
              )}
              <div className="text-[var(--text-muted)]">
                {t('smartRouting.test.meta', { ms: result.latencyMs, input: result.inputTokens, output: result.outputTokens, model: result.judgeModel ?? '' })}
              </div>
            </div>
          </Alert>
        ) : (
          <Alert tone="danger" title={t('smartRouting.test.failed', { reason: reasonText(result.failure, t) })}>
            {result.message}
            {result.failure === 'timeout' && result.newConnection && <p className="mt-1">{t('smartRouting.test.connecting')}</p>}
          </Alert>
        ))}
    </div>
  );
}

// ---------------------------------------------------------------------------- ② grades and their models

function GradesGroup({ draft, patch }: { draft: Draft; patch: (change: Partial<Draft>) => void }) {
  const { t } = useI18n();
  const models = useRoutingModels();
  const grades = draft.grades;

  const setGrades = (next: SmartRoutingGrade[], fallbackGrade = draft.fallbackGrade) =>
    patch({ grades: next, fallbackGrade: next.some((g) => g.id === fallbackGrade) ? fallbackGrade : (next[Math.floor(next.length / 2)]?.id ?? '') });
  const edit = (i: number, change: Partial<SmartRoutingGrade>) => setGrades(grades.map((g, at) => (at === i ? { ...g, ...change } : g)));
  const move = (i: number, by: -1 | 1) => {
    const next = [...grades];
    [next[i], next[i + by]] = [next[i + by]!, next[i]!];
    setGrades(next);
  };
  const add = () => {
    let n = grades.length + 1;
    while (grades.some((g) => g.id === `grade-${n}`)) n++;
    setGrades([...grades, { id: `grade-${n}`, name: '', description: '', model: '' }]);
  };

  // A grade names the provider its requests go to, so a choice is a model on one provider: the same id on two providers is
  // two choices (and then says which provider it is in its own label, since a short list shows no detail).
  const served = models.data ?? [];
  const shared = new Set(served.filter((m, i) => served.findIndex((o) => o.id === m.id) !== i).map((m) => m.id));
  const choices = served.map((m) => ({
    value: choiceOf(m.providerId, m.id),
    label: shared.has(m.id) ? `${m.id} · ${m.providerName}` : m.id,
    detail: shared.has(m.id) ? undefined : m.providerName,
  }));
  const options = (g: SmartRoutingGrade) => {
    const current = choiceOf(g.providerId, g.model);
    return g.model && !choices.some((o) => o.value === current) ? [{ value: current, label: t('smartRouting.grade.unavailable', { model: g.model }) }, ...choices] : choices;
  };
  const choose = (i: number, value: string) => {
    const at = value.indexOf('\n');
    edit(i, { providerId: value.slice(0, at) || null, model: value.slice(at + 1) });
  };

  return (
    <Group title={t('smartRouting.grades.title')} footer={t('smartRouting.grades.footer')}>
      {grades.map((g, i) => (
        <div key={g.id} className="space-y-2 px-4 py-3">
          <div className="flex items-center gap-2">
            <span className="w-4 text-center font-mono text-[11px] text-[var(--text-muted)]">{i + 1}</span>
            <Input className="flex-1" aria-label={t('smartRouting.grade.name')} placeholder={g.id} value={g.name} onChange={(e) => edit(i, { name: e.target.value })} />
            {draft.fallbackGrade === g.id && <Badge tone="accent">{t('smartRouting.grade.fallbackBadge')}</Badge>}
            <Button size="sm" variant="plain" icon={<ArrowUp className="size-3.5" />} aria-label={t('smartRouting.grade.up')} disabled={i === 0} onClick={() => move(i, -1)} />
            <Button size="sm" variant="plain" icon={<ArrowDown className="size-3.5" />} aria-label={t('smartRouting.grade.down')} disabled={i === grades.length - 1} onClick={() => move(i, 1)} />
            <Button
              size="sm"
              variant="plain"
              icon={<Trash2 className="size-3.5" />}
              aria-label={t('smartRouting.grade.remove')}
              disabled={grades.length <= MIN_GRADES}
              onClick={() => setGrades(grades.filter((_, at) => at !== i))}
            />
          </div>
          <TextArea
            rows={2}
            aria-label={t('smartRouting.grade.description')}
            placeholder={t('smartRouting.grade.descriptionPlaceholder')}
            value={g.description}
            onChange={(e) => edit(i, { description: e.target.value })}
          />
          <Select
            ariaLabel={t('smartRouting.grade.model')}
            placeholder={t('smartRouting.grade.pickModel')}
            value={g.model ? choiceOf(g.providerId, g.model) : ''}
            options={options(g)}
            onChange={(value) => choose(i, value)}
          />
        </div>
      ))}
      {models.data?.length === 0 && <p className="px-4 py-2 text-[12px] text-[var(--warning)]">{t('smartRouting.grade.noModels')}</p>}
      <div className="flex justify-end px-4 py-2">
        <Button size="sm" icon={<Plus className="size-3.5" />} disabled={grades.length >= MAX_GRADES} onClick={add}>
          {t('smartRouting.grade.add')}
        </Button>
      </div>
    </Group>
  );
}

// ---------------------------------------------------------------------------- ③ the rules

function RulesGroup({ draft, patch }: { draft: Draft; patch: (change: Partial<Draft>) => void }) {
  const { t } = useI18n();
  return (
    <Group title={t('smartRouting.rules.title')} footer={t('smartRouting.rules.footer')}>
      <HintRow label={t('smartRouting.rules.mode')} detail={t(`smartRouting.rules.mode.${draft.mode}.hint` as MessageKey)}>
        <Segmented
          ariaLabel={t('smartRouting.rules.mode')}
          value={draft.mode}
          onChange={(mode) => patch({ mode })}
          items={[
            { value: 'session', label: t('smartRouting.rules.mode.session') },
            { value: 'turn', label: t('smartRouting.rules.mode.turn') },
          ]}
        />
      </HintRow>
      {draft.mode === 'session' && (
        <HintRow label={t('smartRouting.rules.ttl')} detail={t('smartRouting.rules.ttlHint')}>
          <NumberInput className="w-28" label={t('smartRouting.rules.ttl')} step={1} min={1} value={draft.sessionTtlMinutes} onChange={(v) => patch({ sessionTtlMinutes: v })} />
        </HintRow>
      )}
      <HintRow label={t('smartRouting.rules.fallback')} detail={t('smartRouting.rules.fallbackHint')}>
        <Select
          className="w-44"
          ariaLabel={t('smartRouting.rules.fallback')}
          value={draft.fallbackGrade}
          onChange={(fallbackGrade) => patch({ fallbackGrade })}
          options={draft.grades.map((g) => ({ value: g.id, label: g.name || g.id }))}
        />
      </HintRow>
      <HintRow label={t('smartRouting.rules.minConfidence')} detail={t('smartRouting.rules.minConfidenceHint')}>
        <NumberInput className="w-28" label={t('smartRouting.rules.minConfidence')} step={0.05} min={0} value={draft.minConfidence} onChange={(v) => patch({ minConfidence: v })} />
      </HintRow>
      <HintRow label={t('smartRouting.rules.contextTurns')} detail={t('smartRouting.rules.contextTurnsHint')}>
        <NumberInput className="w-28" label={t('smartRouting.rules.contextTurns')} step={1} min={0} value={draft.contextTurns} onChange={(v) => patch({ contextTurns: v })} />
      </HintRow>
      <HintRow label={t('smartRouting.rules.maxChars')} detail={t('smartRouting.rules.maxCharsHint')}>
        <NumberInput className="w-28" label={t('smartRouting.rules.maxChars')} step={500} min={500} value={draft.maxChars} onChange={(v) => patch({ maxChars: v })} />
      </HintRow>
      <div className="px-3 py-2">
        <Disclosure title={t('smartRouting.rules.advanced')}>
          <HintRow label={t('smartRouting.rules.virtualModel')} detail={t('smartRouting.rules.virtualModelHint')}>
            <Input className="w-56" mono aria-label={t('smartRouting.rules.virtualModel')} value={draft.virtualModel} onChange={(e) => patch({ virtualModel: e.target.value })} />
          </HintRow>
        </Disclosure>
      </div>
    </Group>
  );
}
