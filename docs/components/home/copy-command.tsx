'use client';

import { useState } from 'react';
import { Check, Copy } from 'lucide-react';
import { cn } from '@/lib/cn';

/** A shell command in a glass pill with a copy button (landing page). */
export function CopyCommand({
  command,
  copyLabel,
  copiedLabel,
  className,
}: {
  command: string;
  copyLabel: string;
  copiedLabel: string;
  className?: string;
}) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard can be unavailable (insecure context); the command stays selectable.
    }
  }

  return (
    <div
      className={cn(
        'glass inline-flex items-center gap-3 rounded-full py-1.5 pr-1.5 pl-4 font-mono text-sm',
        className,
      )}
    >
      <span className="text-[var(--accent)] select-none">$</span>
      <span className="select-all">{command}</span>
      <button
        type="button"
        onClick={copy}
        aria-label={copied ? copiedLabel : copyLabel}
        title={copied ? copiedLabel : copyLabel}
        className="inline-flex size-7 items-center justify-center rounded-full text-fd-muted-foreground transition-colors hover:bg-black/5 hover:text-fd-foreground dark:hover:bg-white/10"
      >
        {copied ? <Check className="size-3.5 text-[#34c759]" /> : <Copy className="size-3.5" />}
      </button>
    </div>
  );
}
