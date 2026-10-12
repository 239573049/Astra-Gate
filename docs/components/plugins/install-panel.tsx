'use client';

import { useEffect, useState } from 'react';
import { Check, Copy, ExternalLink } from 'lucide-react';

import { copy, type MarketLang } from './market';

const PORT_KEY = 'astra.console.port';

/**
 * "Install in Astra": the CLI command to copy, and a link to the visitor's own console. The console's port
 * drifts (runtime.json), so the visitor says which one; it is remembered in this browser only. The console asks
 * for confirmation before installing anything.
 */
export function InstallPanel({ spec, lang, minAstraVersion }: { spec: string; lang: MarketLang; minAstraVersion?: string }) {
  const t = copy(lang);
  const command = `astra plugin add ${spec}`;
  const [copied, setCopied] = useState(false);
  const [port, setPort] = useState('');

  useEffect(() => {
    setPort(window.localStorage.getItem(PORT_KEY) ?? '');
  }, []);

  const copyCommand = async () => {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard can be unavailable (insecure context); the command stays selectable.
    }
  };

  const valid = /^\d{2,5}$/.test(port) && Number(port) > 0 && Number(port) < 65536;
  const consoleUrl = valid ? `http://127.0.0.1:${port}/plugins/market?install=${encodeURIComponent(spec)}` : undefined;

  return (
    <div className="rounded-xl border border-fd-border p-5">
      <h2 className="text-sm font-medium">{t.installTitle}</h2>

      <p className="mt-3 text-sm text-fd-muted-foreground">{t.installCli}</p>
      <div className="mt-1.5 flex items-center gap-3 rounded-lg bg-fd-muted/60 py-1.5 pr-1.5 pl-4 font-mono text-sm">
        <span className="select-none text-fd-muted-foreground">$</span>
        <span className="min-w-0 flex-1 select-all overflow-x-auto whitespace-nowrap">{command}</span>
        <button
          type="button"
          onClick={() => void copyCommand()}
          aria-label={copied ? t.installCopied : t.installCopy}
          title={copied ? t.installCopied : t.installCopy}
          className="inline-flex size-7 shrink-0 items-center justify-center rounded-md text-fd-muted-foreground transition-colors hover:bg-fd-accent hover:text-fd-foreground"
        >
          {copied ? <Check className="size-3.5 text-[#34c759]" /> : <Copy className="size-3.5" />}
        </button>
      </div>

      <p className="mt-4 text-sm text-fd-muted-foreground">{t.installConsole}</p>
      <div className="mt-1.5 flex flex-wrap items-center gap-2">
        <input
          inputMode="numeric"
          value={port}
          onChange={(e) => {
            const v = e.target.value.replace(/\D/g, '').slice(0, 5);
            setPort(v);
            window.localStorage.setItem(PORT_KEY, v);
          }}
          placeholder={t.installPort}
          aria-label={t.installPort}
          className="h-9 w-32 rounded-lg border border-fd-border bg-transparent px-3 font-mono text-sm outline-none focus:border-fd-primary"
        />
        <a
          href={consoleUrl}
          target="_blank"
          rel="noreferrer"
          aria-disabled={!valid}
          className={
            'inline-flex h-9 items-center gap-1.5 rounded-lg bg-fd-primary px-3 text-sm font-medium text-fd-primary-foreground transition-opacity ' +
            (valid ? 'hover:opacity-90' : 'pointer-events-none opacity-40')
          }
        >
          <ExternalLink className="size-3.5" />
          {t.installOpen}
        </a>
      </div>
      {minAstraVersion && <p className="mt-3 text-xs text-fd-muted-foreground">{t.installNeeds.replace('{version}', minAstraVersion)}</p>}
    </div>
  );
}
