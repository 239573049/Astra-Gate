import { FileUp } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import type { AuthScheme } from '../api/types';
import { useI18n } from '../i18n';
import { Button, Field, Input, TextArea } from './ui/controls';
import { Select } from './ui/overlays';

// The two cloud auth schemes keep a JSON document in the provider's one encrypted secret (src/Astra.Server/Api/CloudSecrets.cs):
// an IAM access key for Amazon Bedrock, a service account key file for Google Vertex AI. These fields assemble that document
// and hand it to the same `apiKey` field every other scheme uses.

/** Schemes whose secret is not one string typed by the user. */
export const isCloudScheme = (scheme: AuthScheme | undefined): scheme is 'aws-sigv4' | 'gcp-service-account' =>
  scheme === 'aws-sigv4' || scheme === 'gcp-service-account';

/** The region an AWS endpoint names: bedrock-runtime.us-east-1.amazonaws.com, bedrock-mantle.us-east-1.api.aws. */
export function awsRegionOf(baseUrl: string | undefined): string | null {
  try {
    const host = new URL(baseUrl ?? '').hostname.toLowerCase();
    if (!/\.(amazonaws\.com|api\.aws)$/.test(host)) return null;
    const region = host.split('.').at(-3);
    return region && /^[a-z]{2,}(-[a-z0-9]+)+$/.test(region) ? region : null;
  } catch {
    return null;
  }
}

export interface ServiceAccountFacts {
  email: string;
  projectId: string;
}

/** What a pasted key file says about itself; null when it is not a service account key. Never keeps the private key. */
export function readServiceAccount(text: string): ServiceAccountFacts | null {
  try {
    const o = JSON.parse(text) as Record<string, unknown>;
    if (typeof o.client_email !== 'string' || typeof o.private_key !== 'string') return null;
    if (o.type !== undefined && o.type !== 'service_account') return null;
    return { email: o.client_email, projectId: typeof o.project_id === 'string' ? o.project_id : '' };
  } catch {
    return null;
  }
}

/** The IAM access key as the server stores it, or '' while the two required parts are not both there. */
export function awsKeyDocument(accessKeyId: string, secretAccessKey: string, sessionToken: string): string {
  const id = accessKeyId.trim();
  const secret = secretAccessKey.trim();
  if (!id || !secret) return '';
  const token = sessionToken.trim();
  return JSON.stringify({ accessKeyId: id, secretAccessKey: secret, ...(token ? { sessionToken: token } : {}) });
}

/** Access key id, secret and optional session token; reports the assembled document ('' until it is complete). */
export function AwsKeyFields({ onChange }: { onChange: (document: string) => void }) {
  const { t } = useI18n();
  const [id, setId] = useState('');
  const [secret, setSecret] = useState('');
  const [token, setToken] = useState('');
  useEffect(() => onChange(awsKeyDocument(id, secret, token)), [id, secret, token, onChange]);
  return (
    <div className="grid gap-3 font-sans">
      <Field label={t('providers.cloud.aws.accessKeyId')}>
        <Input mono autoComplete="off" spellCheck={false} placeholder="AKIA…" value={id} onChange={(e) => setId(e.target.value)} />
      </Field>
      <Field label={t('providers.cloud.aws.secretAccessKey')} hint={t('providers.apiKeyHint')}>
        <Input mono type="password" autoComplete="off" value={secret} onChange={(e) => setSecret(e.target.value)} />
      </Field>
      <Field label={t('providers.cloud.aws.sessionToken')} hint={t('providers.cloud.aws.sessionTokenHint')}>
        <Input mono type="password" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)} />
      </Field>
    </div>
  );
}

/** Which region an Amazon Bedrock provider's addresses are in (the signature is scoped to it); changed by picking another variant. */
export function AwsRegionHint({ endpoints }: { endpoints: { baseUrl: string }[] }) {
  const { t } = useI18n();
  const region = awsRegionOf(endpoints[0]?.baseUrl);
  return (
    <div className="mt-2 font-sans text-[12px] text-[var(--text-muted)]">
      {region ? t('providers.cloud.aws.region', { region }) : t('providers.cloud.aws.regionUnknown')}
    </div>
  );
}

const LOCATIONS = ['global', 'us', 'eu', 'us-central1', 'us-east1', 'us-east4', 'us-east5', 'us-south1', 'us-west1', 'us-west4', 'northamerica-northeast1',
  'europe-west1', 'europe-west4', 'europe-west9', 'asia-northeast1', 'asia-southeast1', 'australia-southeast1', 'southamerica-east1'];

/** Project + location, which Vertex puts in every request path (stored in settings.vertex). */
export function VertexTargetFields({
  project,
  location,
  projectPlaceholder,
  onChange,
}: {
  project: string;
  location: string;
  projectPlaceholder?: string;
  onChange: (next: { project: string; location: string }) => void;
}) {
  const { t } = useI18n();
  const known = LOCATIONS.includes(location) || location === '' ? LOCATIONS : [...LOCATIONS, location];
  return (
    <div className="grid gap-3 font-sans sm:grid-cols-2">
      <Field label={t('providers.cloud.gcp.project')} hint={t('providers.cloud.gcp.projectHint')}>
        <Input mono autoComplete="off" spellCheck={false} placeholder={projectPlaceholder || 'my-project-123'} value={project} onChange={(e) => onChange({ project: e.target.value, location })} />
      </Field>
      <Field label={t('providers.cloud.gcp.location')} hint={t('providers.cloud.gcp.locationHint')}>
        <Select value={location || 'global'} onChange={(v) => onChange({ project, location: v })} options={known.map((l) => ({ value: l, label: l }))} />
      </Field>
    </div>
  );
}

/** A service account key file: pasted, or chosen from disk (read in the browser, sent only to Astra). */
export function ServiceAccountField({ onChange, onFacts }: { onChange: (document: string) => void; onFacts?: (facts: ServiceAccountFacts | null) => void }) {
  const { t } = useI18n();
  const [text, setText] = useState('');
  const file = useRef<HTMLInputElement>(null);
  const facts = text.trim() ? readServiceAccount(text) : null;
  useEffect(() => {
    onChange(facts ? text.trim() : '');
    onFacts?.(facts);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the document and its facts follow the text only
  }, [text]);
  const choose = async (picked: File | undefined) => {
    if (!picked) return;
    setText(await picked.text());
    if (file.current) file.current.value = '';
  };
  return (
    <div className="grid gap-2 font-sans">
      <Field
        label={t('providers.cloud.gcp.keyFile')}
        hint={text.trim() ? undefined : t('providers.cloud.gcp.keyFileHint')}
        error={text.trim() && !facts ? t('providers.cloud.gcp.invalid') : undefined}
      >
        <TextArea
          mono
          rows={5}
          spellCheck={false}
          autoComplete="off"
          placeholder='{ "type": "service_account", … }'
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
      </Field>
      <div className="flex flex-wrap items-center gap-3 text-[12px] text-[var(--text-secondary)]">
        <input ref={file} type="file" accept=".json,application/json" className="hidden" aria-label={t('providers.cloud.gcp.chooseFile')} onChange={(e) => void choose(e.target.files?.[0])} />
        <Button size="sm" onClick={() => file.current?.click()}>
          <FileUp className="size-3.5" />
          {t('providers.cloud.gcp.chooseFile')}
        </Button>
        {facts && <span>{t('providers.cloud.gcp.detected', { email: facts.email })}</span>}
      </div>
    </div>
  );
}
